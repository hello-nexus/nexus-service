using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Models.Reminders;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Reminders;

public readonly struct ReminderResult<T>
{
    public T? Value { get; init; }
    public string? Error { get; init; }
    public static ReminderResult<T> Ok(T value) => new() { Value = value };
    public static ReminderResult<T> Fail(string code) => new() { Error = code };
}

/// <summary>
/// Local reminder lists and items, owned by the service. One lock guards the
/// store. The stored lists and items are never edited in place: every mutation
/// builds replacement collections (and replacement objects for a changed row)
/// and swaps them in, so a failed validation or a failed write leaves the
/// previous state untouched. A mutation is acknowledged only after the file
/// write succeeded; a failed write rolls the swap back and reports
/// <c>saveFailed</c>. Nothing here logs titles, notes or URLs.
/// </summary>
public sealed class ReminderService
{
    public const int MaxLists = 100;
    public const int MaxItems = 5000;
    public const int MaxTitle = 500;
    public const int MaxNotes = 10000;
    public const int MaxName = 100;
    public const int MaxIcon = 64;
    public const int MaxUrl = 2048;
    public const int MaxOrder = 1_000_000;
    public const string SeedName = "Reminders";
    public const string SeedColor = "#0A84FF";
    public const string SeedIcon = "list";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Func<string, string> _readFile;
    private readonly TimeSpan _retryStep;
    private static int _asideCounter;
    private List<ReminderListDto> _lists = new();
    private List<ReminderDto> _items = new();
    /// <summary>True while the store file exists but could be neither read nor moved aside. Nothing is served or written until a later load succeeds, so the file is never overwritten.</summary>
    private bool _blocked;
    private DateTimeOffset _lastLoadAttempt;

    /// <summary>A blocked store retries its load lazily, at most this often.</summary>
    public static readonly TimeSpan LoadRetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>Raised after a mutation reached disk. Carries no data; the web layer turns it into a topic ping.</summary>
    public event Action? Changed;

    public ReminderService(string root, TimeProvider? time = null)
        : this(root, time, null, null)
    {
    }

    /// <summary><paramref name="readFile"/> and <paramref name="retryStep"/> are test seams for read failures.</summary>
    internal ReminderService(string root, TimeProvider? time, Func<string, string>? readFile, TimeSpan? retryStep)
    {
        _path = Path.Combine(root, "reminders", "reminders.json");
        _time = time ?? TimeProvider.System;
        _readFile = readFile ?? File.ReadAllText;
        _retryStep = retryStep ?? TimeSpan.FromMilliseconds(25);
        lock (_gate)
        {
            Load();
        }
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private enum ReadOutcome { Missing, Ok, Corrupt, Unreadable }

    private ReadOutcome TryRead(string path, out ReminderStoreFile? file)
    {
        file = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return ReadOutcome.Missing;
                }
                file = JsonSerializer.Deserialize(_readFile(path), PersistenceJsonContext.Default.ReminderStoreFile);
                if (file is null || file.Lists is null || file.Items is null || file.Lists.Contains(null!) || file.Items.Contains(null!))
                {
                    file = null;
                    return ReadOutcome.Corrupt;
                }
                return ReadOutcome.Ok;
            }
            catch (JsonException)
            {
                return ReadOutcome.Corrupt;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return ReadOutcome.Missing;
            }
            catch (IOException) when (attempt < 6)
            {
                // A scanner or indexer can hold the file for a moment; that is not corruption.
                Thread.Sleep(_retryStep * (attempt + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Nexus.Service.Platform.ServiceLog.Warn($"[reminders] could not read store ({ex.GetType().Name})");
                return ReadOutcome.Unreadable;
            }
        }
    }

    private void Adopt(ReminderStoreFile file)
    {
        _lists = file.Lists.Where(l => !string.IsNullOrEmpty(l.Id)).ToList();
        var known = new HashSet<string>(_lists.Select(l => l.Id), StringComparer.Ordinal);
        _items = file.Items.Where(i => !string.IsNullOrEmpty(i.Id) && i.ListId is not null && known.Contains(i.ListId)).ToList();
    }

    /// <summary>
    /// Loads the store under the lock. A missing or corrupt main file recovers from the newest usable
    /// sibling: the staged <c>.tmp</c> (a crash between File.Replace's two renames leaves exactly that),
    /// then the <c>.bak</c>. The seed is written only when no main, tmp or bak file exists. Anything
    /// unreadable, or an unusable main file that cannot be moved aside, blocks the store instead.
    /// </summary>
    private void Load()
    {
        _lastLoadAttempt = _time.GetUtcNow();
        _blocked = true;
        _lists = new List<ReminderListDto>();
        _items = new List<ReminderDto>();
        var main = TryRead(_path, out var mainFile);
        if (main == ReadOutcome.Unreadable)
        {
            return;
        }
        if (main == ReadOutcome.Ok)
        {
            Adopt(mainFile!);
            _blocked = false;
            if (_lists.Count == 0)
            {
                _lists = new List<ReminderListDto> { Seed() };
                Save();
            }
            return;
        }

        ReminderStoreFile? recovered = null;
        foreach (var sibling in new[] { AtomicJsonFile.TempPathFor(_path), AtomicJsonFile.BackupPathFor(_path) })
        {
            var outcome = TryRead(sibling, out var siblingFile);
            if (outcome == ReadOutcome.Unreadable)
            {
                return;
            }
            if (outcome == ReadOutcome.Ok)
            {
                recovered = siblingFile;
                break;
            }
        }
        if (main == ReadOutcome.Corrupt && !MoveCorruptAside())
        {
            return;
        }
        if (recovered is not null)
        {
            Adopt(recovered);
            Nexus.Service.Platform.ServiceLog.Warn("[reminders] main store was unusable; recovered from a sibling file");
        }
        if (_lists.Count == 0)
        {
            _lists = new List<ReminderListDto> { Seed() };
        }
        _blocked = false;
        Save();
    }

    private ReminderListDto Seed() => new() { Id = NewId(), Name = SeedName, Color = SeedColor, Icon = SeedIcon, Order = 0, CreatedAt = Now() };

    /// <summary>An unusable store is kept for recovery rather than overwritten. False when it could not be moved.</summary>
    private bool MoveCorruptAside()
    {
        try
        {
            var stamp = _time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var n = Interlocked.Increment(ref _asideCounter).ToString(CultureInfo.InvariantCulture);
            File.Move(_path, _path + ".corrupt-" + stamp + "-" + n, overwrite: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Retries a blocked load, at most once per <see cref="LoadRetryInterval"/>. Call under the lock.</summary>
    private void RetryLoadIfDue()
    {
        if (_blocked && _time.GetUtcNow() - _lastLoadAttempt >= LoadRetryInterval)
        {
            Load();
        }
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var file = new ReminderStoreFile { Lists = _lists, Items = _items };
            AtomicJsonFile.Write(_path, JsonSerializer.Serialize(file, PersistenceJsonContext.Default.ReminderStoreFile), keepBackup: true);
            return true;
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[reminders] could not write store ({ex.GetType().Name})");
            return false;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Runs <paramref name="body"/> under the lock. The body swaps in new collections or returns an
    /// error. A swap is written before it is acknowledged: a failed write restores the previous
    /// collections and returns <c>saveFailed</c>. A body that changed nothing writes and broadcasts nothing.
    /// </summary>
    private ReminderResult<T> Mutate<T>(Func<ReminderResult<T>> body)
    {
        ReminderResult<T> result;
        bool changed;
        lock (_gate)
        {
            RetryLoadIfDue();
            if (_blocked)
            {
                return ReminderResult<T>.Fail(ReminderErrors.Unavailable);
            }
            var lists = _lists;
            var items = _items;
            result = body();
            changed = result.Error is null && (!ReferenceEquals(lists, _lists) || !ReferenceEquals(items, _items));
            if (result.Error is not null || (changed && !Save()))
            {
                // A body that failed after swapping (an order renumber, say) must leave nothing behind.
                _lists = lists;
                _items = items;
                return result.Error is not null ? result : ReminderResult<T>.Fail(ReminderErrors.SaveFailed);
            }
        }
        if (changed)
        {
            Changed?.Invoke();
        }
        return result;
    }

    private static ReminderListDto Copy(ReminderListDto l) => new() { Id = l.Id, Name = l.Name, Color = l.Color, Icon = l.Icon, Order = l.Order, CreatedAt = l.CreatedAt };

    private static ReminderDto Copy(ReminderDto i) => new()
    {
        Id = i.Id, ListId = i.ListId, Title = i.Title, Notes = i.Notes, DueDate = i.DueDate, DueTime = i.DueTime, Priority = i.Priority,
        Flagged = i.Flagged, Completed = i.Completed, CompletedAt = i.CompletedAt, Url = i.Url, Order = i.Order, CreatedAt = i.CreatedAt, UpdatedAt = i.UpdatedAt,
    };

    /// <summary>The whole store, or <c>unavailable</c> while the store file cannot be loaded.</summary>
    public ReminderResult<ReminderStoreResponse> Read()
    {
        lock (_gate)
        {
            RetryLoadIfDue();
            if (_blocked)
            {
                return ReminderResult<ReminderStoreResponse>.Fail(ReminderErrors.Unavailable);
            }
            return ReminderResult<ReminderStoreResponse>.Ok(new ReminderStoreResponse
            {
                Lists = _lists.OrderBy(l => l.Order).ThenBy(l => l.CreatedAt).Select(Copy).ToList(),
                Items = _items.OrderBy(i => i.Order).ThenBy(i => i.CreatedAt).Select(Copy).ToList(),
            });
        }
    }

    /// <summary>The whole store, empty while unavailable. Callers that must tell the two apart use <see cref="Read"/>.</summary>
    public ReminderStoreResponse GetAll() => Read().Value ?? new ReminderStoreResponse();

    // ---- field parsing ----

    private static bool IsColor(string s) =>
        s.Length == 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    private static bool IsIcon(string s) =>
        s.Length is > 0 and <= MaxIcon && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsDate(string s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsTime(string s) =>
        TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsHttpUrl(string s) =>
        s.Length <= MaxUrl && Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Reads an optional string property. Absent leaves <paramref name="present"/> false; explicit null sets it true with a null value.</summary>
    private static bool TryString(JsonElement body, string name, out bool present, out string? value)
    {
        present = false;
        value = null;
        if (!body.TryGetProperty(name, out var el))
        {
            return true;
        }
        present = true;
        if (el.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = el.GetString();
        return true;
    }

    private static bool TryInt(JsonElement body, string name, out bool present, out int value)
    {
        present = false;
        value = 0;
        if (!body.TryGetProperty(name, out var el))
        {
            return true;
        }
        present = true;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }

    private static bool TryBool(JsonElement body, string name, out bool present, out bool value)
    {
        present = false;
        value = false;
        if (!body.TryGetProperty(name, out var el))
        {
            return true;
        }
        present = true;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = el.GetBoolean();
            return true;
        }
        return false;
    }

    /// <summary>Validates a list name, returning the trimmed name or an error code.</summary>
    private static string? CheckName(string? name, out string error)
    {
        error = ReminderErrors.Invalid;
        var t = name?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }
        if (t.Length > MaxName)
        {
            error = ReminderErrors.LimitReached;
            return null;
        }
        return t;
    }

    private static bool IsOrder(int order) => order is >= 0 and <= MaxOrder;

    // ---- lists ----

    public ReminderResult<ReminderListDto> CreateList(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !TryString(body, "name", out _, out var rawName)
            || !TryString(body, "color", out _, out var color)
            || !TryString(body, "icon", out _, out var icon))
        {
            return ReminderResult<ReminderListDto>.Fail(ReminderErrors.Invalid);
        }
        var name = CheckName(rawName, out var nameError);
        if (name is null)
        {
            return ReminderResult<ReminderListDto>.Fail(nameError);
        }
        if (color is null || !IsColor(color) || icon is null || !IsIcon(icon))
        {
            return ReminderResult<ReminderListDto>.Fail(ReminderErrors.Invalid);
        }
        return Mutate(() =>
        {
            if (_lists.Count >= MaxLists)
            {
                return ReminderResult<ReminderListDto>.Fail(ReminderErrors.LimitReached);
            }
            var next = _lists.Count == 0 ? 0 : _lists.Max(l => l.Order) + 1;
            if (next > MaxOrder)
            {
                // Renumber densely so the new list lands last without exceeding the cap.
                _lists = _lists.OrderBy(l => l.Order).ThenBy(l => l.CreatedAt).Select((l, n) => { var c = Copy(l); c.Order = n; return c; }).ToList();
                next = _lists.Count;
            }
            var created = new ReminderListDto { Id = NewId(), Name = name, Color = color, Icon = icon, Order = next, CreatedAt = Now() };
            _lists = new List<ReminderListDto>(_lists) { created };
            return ReminderResult<ReminderListDto>.Ok(Copy(created));
        });
    }

    public ReminderResult<ReminderListDto> PatchList(string id, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !TryString(body, "name", out var hasName, out var rawName)
            || !TryString(body, "color", out var hasColor, out var color)
            || !TryString(body, "icon", out var hasIcon, out var icon)
            || !TryInt(body, "order", out var hasOrder, out var order))
        {
            return ReminderResult<ReminderListDto>.Fail(ReminderErrors.Invalid);
        }
        string? name = null;
        if (hasName)
        {
            name = CheckName(rawName, out var nameError);
            if (name is null)
            {
                return ReminderResult<ReminderListDto>.Fail(nameError);
            }
        }
        if ((hasColor && (color is null || !IsColor(color))) || (hasIcon && (icon is null || !IsIcon(icon))) || (hasOrder && !IsOrder(order)))
        {
            return ReminderResult<ReminderListDto>.Fail(ReminderErrors.Invalid);
        }
        return Mutate(() =>
        {
            var list = _lists.FirstOrDefault(l => l.Id == id);
            if (list is null)
            {
                return ReminderResult<ReminderListDto>.Fail(ReminderErrors.NotFound);
            }
            var next = Copy(list);
            if (hasName)
            {
                next.Name = name!;
            }
            if (hasColor)
            {
                next.Color = color!;
            }
            if (hasIcon)
            {
                next.Icon = icon!;
            }
            if (hasOrder)
            {
                next.Order = order;
            }
            _lists = _lists.Select(l => l.Id == id ? next : l).ToList();
            return ReminderResult<ReminderListDto>.Ok(Copy(next));
        });
    }

    /// <summary>Deletes a list and its items. Fails with <c>notFound</c>, or <c>lastList</c> for the only list.</summary>
    public ReminderResult<bool> DeleteList(string id) => Mutate(() =>
    {
        if (!_lists.Any(l => l.Id == id))
        {
            return ReminderResult<bool>.Fail(ReminderErrors.NotFound);
        }
        if (_lists.Count <= 1)
        {
            return ReminderResult<bool>.Fail(ReminderErrors.LastList);
        }
        _lists = _lists.Where(l => l.Id != id).ToList();
        _items = _items.Where(i => i.ListId != id).ToList();
        return ReminderResult<bool>.Ok(true);
    });

    // ---- items ----

    public ReminderResult<ReminderDto> CreateItem(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !TryString(body, "listId", out _, out var listId)
            || !TryString(body, "title", out _, out var rawTitle)
            || !TryString(body, "notes", out var hasNotes, out var notes)
            || !TryString(body, "dueDate", out var hasDate, out var dueDate)
            || !TryString(body, "dueTime", out var hasTime, out var dueTime)
            || !TryInt(body, "priority", out var hasPriority, out var priority)
            || !TryBool(body, "flagged", out var hasFlagged, out var flagged)
            || !TryString(body, "url", out var hasUrl, out var url))
        {
            return ReminderResult<ReminderDto>.Fail(ReminderErrors.Invalid);
        }
        var title = rawTitle?.Trim();
        if (string.IsNullOrEmpty(listId) || string.IsNullOrEmpty(title))
        {
            return ReminderResult<ReminderDto>.Fail(ReminderErrors.Invalid);
        }
        var draft = new ReminderDto { Title = title, ListId = listId };
        if (hasNotes)
        {
            draft.Notes = notes;
        }
        if (hasDate)
        {
            draft.DueDate = dueDate;
        }
        if (hasTime)
        {
            draft.DueTime = dueTime;
        }
        if (hasPriority)
        {
            draft.Priority = priority;
        }
        if (hasFlagged)
        {
            draft.Flagged = flagged;
        }
        if (hasUrl)
        {
            draft.Url = url;
        }
        var error = ValidateItem(draft);
        if (error is not null)
        {
            return ReminderResult<ReminderDto>.Fail(error);
        }
        return Mutate(() =>
        {
            if (!_lists.Any(l => l.Id == listId))
            {
                return ReminderResult<ReminderDto>.Fail(ReminderErrors.NotFound);
            }
            if (_items.Count >= MaxItems)
            {
                return ReminderResult<ReminderDto>.Fail(ReminderErrors.LimitReached);
            }
            var now = Now();
            var created = Copy(draft);
            created.Id = NewId();
            created.Order = NextItemOrder(listId);
            created.CreatedAt = now;
            created.UpdatedAt = now;
            _items = new List<ReminderDto>(_items) { created };
            return ReminderResult<ReminderDto>.Ok(Copy(created));
        });
    }

    /// <summary>
    /// The order that appends to <paramref name="listId"/>. When that would exceed the cap, the list's
    /// items are first renumbered 0..n-1 densely (swapping in new items), so the caller must run
    /// inside <see cref="Mutate{T}"/>, which restores the previous items if the mutation then fails.
    /// </summary>
    private int NextItemOrder(string listId)
    {
        var max = -1;
        foreach (var i in _items)
        {
            if (i.ListId == listId && i.Order > max)
            {
                max = i.Order;
            }
        }
        if (max + 1 <= MaxOrder)
        {
            return max + 1;
        }
        var ordered = _items.Where(i => i.ListId == listId).OrderBy(i => i.Order).ThenBy(i => i.CreatedAt).Select(i => i.Id).ToList();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var n = 0; n < ordered.Count; n++)
        {
            positions[ordered[n]] = n;
        }
        _items = _items.Select(i =>
        {
            if (!positions.TryGetValue(i.Id, out var pos))
            {
                return i;
            }
            var c = Copy(i);
            c.Order = pos;
            return c;
        }).ToList();
        return ordered.Count;
    }

    /// <summary>Checks every mutable field of a fully assembled item; length caps report <c>limitReached</c>.</summary>
    private static string? ValidateItem(ReminderDto i)
    {
        if (i.Title.Length > MaxTitle || (i.Notes?.Length ?? 0) > MaxNotes)
        {
            return ReminderErrors.LimitReached;
        }
        if (i.DueDate is not null && !IsDate(i.DueDate))
        {
            return ReminderErrors.Invalid;
        }
        if (i.DueTime is not null && (i.DueDate is null || !IsTime(i.DueTime)))
        {
            return ReminderErrors.Invalid;
        }
        if (i.Priority is < 0 or > 3)
        {
            return ReminderErrors.Invalid;
        }
        if (i.Url is not null && !IsHttpUrl(i.Url))
        {
            return ReminderErrors.Invalid;
        }
        return null;
    }

    public ReminderResult<ReminderDto> PatchItem(string id, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !TryString(body, "listId", out var hasList, out var listId)
            || !TryString(body, "title", out var hasTitle, out var rawTitle)
            || !TryString(body, "notes", out var hasNotes, out var notes)
            || !TryString(body, "dueDate", out var hasDate, out var dueDate)
            || !TryString(body, "dueTime", out var hasTime, out var dueTime)
            || !TryInt(body, "priority", out var hasPriority, out var priority)
            || !TryBool(body, "flagged", out var hasFlagged, out var flagged)
            || !TryBool(body, "completed", out var hasCompleted, out var completed)
            || !TryString(body, "url", out var hasUrl, out var url)
            || !TryInt(body, "order", out var hasOrder, out var order))
        {
            return ReminderResult<ReminderDto>.Fail(ReminderErrors.Invalid);
        }
        var title = rawTitle?.Trim();
        // Non-nullable fields cannot be cleared with an explicit null.
        if ((hasList && string.IsNullOrEmpty(listId)) || (hasTitle && string.IsNullOrEmpty(title)) || (hasOrder && !IsOrder(order)))
        {
            return ReminderResult<ReminderDto>.Fail(ReminderErrors.Invalid);
        }
        return Mutate(() =>
        {
            var current = _items.FirstOrDefault(i => i.Id == id);
            if (current is null)
            {
                return ReminderResult<ReminderDto>.Fail(ReminderErrors.NotFound);
            }
            // Patch a copy: a failed validation below must leave the stored item untouched.
            var next = Copy(current);
            if (hasTitle)
            {
                next.Title = title!;
            }
            if (hasNotes)
            {
                next.Notes = notes;
            }
            if (hasDate)
            {
                next.DueDate = dueDate;
                if (dueDate is null && !hasTime)
                {
                    next.DueTime = null;
                }
            }
            if (hasTime)
            {
                next.DueTime = dueTime;
            }
            if (hasPriority)
            {
                next.Priority = priority;
            }
            if (hasFlagged)
            {
                next.Flagged = flagged;
            }
            if (hasUrl)
            {
                next.Url = url;
            }
            var error = ValidateItem(next);
            if (error is not null)
            {
                return ReminderResult<ReminderDto>.Fail(error);
            }
            var now = Now();
            if (hasCompleted && completed != next.Completed)
            {
                next.Completed = completed;
                next.CompletedAt = completed ? now : null;
            }
            if (hasList && listId != next.ListId)
            {
                if (!_lists.Any(l => l.Id == listId))
                {
                    return ReminderResult<ReminderDto>.Fail(ReminderErrors.NotFound);
                }
                next.ListId = listId!;
                next.Order = NextItemOrder(listId!);
            }
            if (hasOrder)
            {
                next.Order = order;
            }
            next.UpdatedAt = now;
            _items = _items.Select(i => i.Id == id ? next : i).ToList();
            return ReminderResult<ReminderDto>.Ok(Copy(next));
        });
    }

    public ReminderResult<bool> DeleteItem(string id) => Mutate(() =>
    {
        if (!_items.Any(i => i.Id == id))
        {
            return ReminderResult<bool>.Fail(ReminderErrors.NotFound);
        }
        _items = _items.Where(i => i.Id != id).ToList();
        return ReminderResult<bool>.Ok(true);
    });

    public ReminderResult<int> ClearCompleted(string? listId) => Mutate(() =>
    {
        if (listId is not null && !_lists.Any(l => l.Id == listId))
        {
            return ReminderResult<int>.Fail(ReminderErrors.NotFound);
        }
        var kept = _items.Where(i => !(i.Completed && (listId is null || i.ListId == listId))).ToList();
        var removed = _items.Count - kept.Count;
        if (removed > 0)
        {
            _items = kept;
        }
        return ReminderResult<int>.Ok(removed);
    });

    /// <summary>
    /// Sets <c>order</c> to each id's position. <paramref name="itemIds"/> must be exactly the list's
    /// current items, otherwise the client's view is stale and the call fails with <c>stale</c> and
    /// changes nothing. An empty list is a no-op.
    /// </summary>
    public ReminderResult<bool> Reorder(string listId, IReadOnlyList<string> itemIds) => Mutate(() =>
    {
        if (!_lists.Any(l => l.Id == listId))
        {
            return ReminderResult<bool>.Fail(ReminderErrors.NotFound);
        }
        var current = new HashSet<string>(_items.Where(i => i.ListId == listId).Select(i => i.Id), StringComparer.Ordinal);
        var given = new HashSet<string>(itemIds, StringComparer.Ordinal);
        if (given.Count != itemIds.Count || !given.SetEquals(current))
        {
            return ReminderResult<bool>.Fail(ReminderErrors.Stale);
        }
        if (itemIds.Count == 0)
        {
            return ReminderResult<bool>.Ok(true);
        }
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var n = 0; n < itemIds.Count; n++)
        {
            positions[itemIds[n]] = n;
        }
        var now = Now();
        _items = _items.Select(i =>
        {
            if (!positions.TryGetValue(i.Id, out var pos))
            {
                return i;
            }
            var next = Copy(i);
            next.Order = pos;
            next.UpdatedAt = now;
            return next;
        }).ToList();
        return ReminderResult<bool>.Ok(true);
    });
}
