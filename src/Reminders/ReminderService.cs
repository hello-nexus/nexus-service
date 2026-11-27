using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// store and every mutation is written through atomically before it returns,
/// so a shutdown can never lose an acknowledged write. Nothing here logs
/// titles, notes or URLs.
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
    public const string SeedName = "Reminders";
    public const string SeedColor = "#0A84FF";
    public const string SeedIcon = "list";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;
    private List<ReminderListDto> _lists = new();
    private List<ReminderDto> _items = new();

    /// <summary>Raised after any mutation. Carries no data; the web layer turns it into a topic ping.</summary>
    public event Action? Changed;

    public ReminderService(string root, TimeProvider? time = null)
    {
        _path = Path.Combine(root, "reminders", "reminders.json");
        _time = time ?? TimeProvider.System;
        Load();
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(_path), PersistenceJsonContext.Default.ReminderStoreFile);
                if (file is null)
                {
                    MoveCorruptAside();
                }
                else
                {
                    _lists = file.Lists.Where(l => !string.IsNullOrEmpty(l.Id)).ToList();
                    var known = new HashSet<string>(_lists.Select(l => l.Id), StringComparer.Ordinal);
                    _items = file.Items.Where(i => !string.IsNullOrEmpty(i.Id) && known.Contains(i.ListId)).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[reminders] could not read store ({ex.GetType().Name})");
            MoveCorruptAside();
        }
        if (_lists.Count == 0)
        {
            _lists.Add(new ReminderListDto { Id = NewId(), Name = SeedName, Color = SeedColor, Icon = SeedIcon, Order = 0, CreatedAt = Now() });
            Save();
        }
    }

    /// <summary>An unreadable store is kept for recovery rather than overwritten by the seed.</summary>
    private void MoveCorruptAside()
    {
        try
        {
            File.Move(_path, _path + ".corrupt-" + _time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), overwrite: true);
        }
        catch (Exception)
        {
            // Best effort; the next write still keeps a backup of whatever is there.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var file = new ReminderStoreFile { Lists = _lists, Items = _items };
            AtomicJsonFile.Write(_path, JsonSerializer.Serialize(file, PersistenceJsonContext.Default.ReminderStoreFile), keepBackup: true);
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[reminders] could not write store ({ex.GetType().Name})");
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    private static ReminderListDto Copy(ReminderListDto l) => new() { Id = l.Id, Name = l.Name, Color = l.Color, Icon = l.Icon, Order = l.Order, CreatedAt = l.CreatedAt };

    private static ReminderDto Copy(ReminderDto i) => new()
    {
        Id = i.Id, ListId = i.ListId, Title = i.Title, Notes = i.Notes, DueDate = i.DueDate, DueTime = i.DueTime, Priority = i.Priority,
        Flagged = i.Flagged, Completed = i.Completed, CompletedAt = i.CompletedAt, Url = i.Url, Order = i.Order, CreatedAt = i.CreatedAt, UpdatedAt = i.UpdatedAt,
    };

    public ReminderStoreResponse GetAll()
    {
        lock (_gate)
        {
            return new ReminderStoreResponse
            {
                Lists = _lists.OrderBy(l => l.Order).ThenBy(l => l.CreatedAt).Select(Copy).ToList(),
                Items = _items.OrderBy(i => i.Order).ThenBy(i => i.CreatedAt).Select(Copy).ToList(),
            };
        }
    }

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
        ReminderListDto created;
        lock (_gate)
        {
            if (_lists.Count >= MaxLists)
            {
                return ReminderResult<ReminderListDto>.Fail(ReminderErrors.LimitReached);
            }
            created = new ReminderListDto
            {
                Id = NewId(), Name = name, Color = color, Icon = icon,
                Order = _lists.Count == 0 ? 0 : _lists.Max(l => l.Order) + 1, CreatedAt = Now(),
            };
            _lists.Add(created);
            Commit();
            created = Copy(created);
        }
        return ReminderResult<ReminderListDto>.Ok(created);
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
        if ((hasColor && (color is null || !IsColor(color))) || (hasIcon && (icon is null || !IsIcon(icon))))
        {
            return ReminderResult<ReminderListDto>.Fail(ReminderErrors.Invalid);
        }
        ReminderListDto result;
        lock (_gate)
        {
            var list = _lists.FirstOrDefault(l => l.Id == id);
            if (list is null)
            {
                return ReminderResult<ReminderListDto>.Fail(ReminderErrors.NotFound);
            }
            // Swap a copy in so a concurrent GetAll never sees a half-patched list.
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
            Commit();
            result = Copy(next);
        }
        return ReminderResult<ReminderListDto>.Ok(result);
    }

    /// <summary>Null on success, otherwise <c>notFound</c> or <c>lastList</c>.</summary>
    public string? DeleteList(string id)
    {
        lock (_gate)
        {
            if (!_lists.Any(l => l.Id == id))
            {
                return ReminderErrors.NotFound;
            }
            if (_lists.Count <= 1)
            {
                return ReminderErrors.LastList;
            }
            _lists = _lists.Where(l => l.Id != id).ToList();
            _items = _items.Where(i => i.ListId != id).ToList();
            Commit();
            return null;
        }
    }

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
        var item = new ReminderDto { Title = title, ListId = listId };
        if (hasNotes)
        {
            item.Notes = notes;
        }
        if (hasDate)
        {
            item.DueDate = dueDate;
        }
        if (hasTime)
        {
            item.DueTime = dueTime;
        }
        if (hasPriority)
        {
            item.Priority = priority;
        }
        if (hasFlagged)
        {
            item.Flagged = flagged;
        }
        if (hasUrl)
        {
            item.Url = url;
        }
        var error = ValidateItem(item);
        if (error is not null)
        {
            return ReminderResult<ReminderDto>.Fail(error);
        }
        ReminderDto result;
        lock (_gate)
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
            item.Id = NewId();
            item.Order = NextItemOrder(listId);
            item.CreatedAt = now;
            item.UpdatedAt = now;
            _items = new List<ReminderDto>(_items) { item };
            Commit();
            result = Copy(item);
        }
        return ReminderResult<ReminderDto>.Ok(result);
    }

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
        return max + 1;
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
        if ((hasList && string.IsNullOrEmpty(listId)) || (hasTitle && string.IsNullOrEmpty(title)))
        {
            return ReminderResult<ReminderDto>.Fail(ReminderErrors.Invalid);
        }
        ReminderDto result;
        lock (_gate)
        {
            var current = _items.FirstOrDefault(i => i.Id == id);
            if (current is null)
            {
                return ReminderResult<ReminderDto>.Fail(ReminderErrors.NotFound);
            }
            // Patch a copy and swap it in, so readers never see a half-applied change.
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
            Commit();
            result = Copy(next);
        }
        return ReminderResult<ReminderDto>.Ok(result);
    }

    public bool DeleteItem(string id)
    {
        lock (_gate)
        {
            if (!_items.Any(i => i.Id == id))
            {
                return false;
            }
            _items = _items.Where(i => i.Id != id).ToList();
            Commit();
            return true;
        }
    }

    public ReminderResult<int> ClearCompleted(string? listId)
    {
        lock (_gate)
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
                Commit();
            }
            return ReminderResult<int>.Ok(removed);
        }
    }

    /// <summary>Sets <c>order</c> to each id's position. Every id must exist in <paramref name="listId"/>, or nothing changes.</summary>
    public string? Reorder(string listId, IReadOnlyList<string> itemIds)
    {
        lock (_gate)
        {
            if (!_lists.Any(l => l.Id == listId))
            {
                return ReminderErrors.NotFound;
            }
            var byId = _items.ToDictionary(i => i.Id, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in itemIds)
            {
                if (!byId.TryGetValue(id, out var item))
                {
                    return ReminderErrors.NotFound;
                }
                if (item.ListId != listId || !seen.Add(id))
                {
                    return ReminderErrors.Invalid;
                }
            }
            var now = Now();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var n = 0; n < itemIds.Count; n++)
            {
                positions[itemIds[n]] = n;
            }
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
            Commit();
            return null;
        }
    }
}
