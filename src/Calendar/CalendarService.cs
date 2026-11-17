using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Calendar.Ics;
using Nexus.Service.Models.Calendar;
using Nexus.Service.Persistence;
using Nexus.Service.Security;
using Nexus.Service.Serialization;

namespace Nexus.Service.Calendar;

public sealed class CalendarAddResult
{
    public CalendarSourceDto? Source { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Owns calendar sources, their cached feeds and the sync schedule. Nothing
/// here logs titles, locations, notes, URLs or hosts: only source ids, HTTP
/// status codes and byte counts.
/// </summary>
public sealed class CalendarService
{
    public static readonly int[] AllowedRefreshMinutes = { 5, 15, 30, 60 };
    public static readonly TimeSpan ManualThrottle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);
    public const int MaxQuerySpanDays = 400;
    private const int MaxSources = 50;

    private sealed class SourceState
    {
        public CalendarSourceRecord Record = new();
        public IcsFeed? Feed;
        public string? ETag;
        public string? LastModified;
        public bool Syncing;
        public DateTimeOffset? LastAttempt;
        public DateTimeOffset? LastFinished;
        public int Failures;
        public string? Error;
    }

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly ICalendarFetcher _fetcher;
    private readonly TimeProvider _time;
    private readonly List<SourceState> _sources = new();
    private readonly List<Task> _running = new();
    private int _refreshMinutes = 15;

    /// <summary>Raised after any mutation or finished sync. Carries no data; the web layer turns it into a topic ping.</summary>
    public event Action? Changed;

    public CalendarService(string root, ICalendarFetcher fetcher, TimeProvider? time = null)
    {
        _dir = Path.Combine(root, "calendar");
        _fetcher = fetcher;
        _time = time ?? TimeProvider.System;
        Load();
    }

    private string SourcesPath => Path.Combine(_dir, "sources.json");
    private string CachePath(string id) => Path.Combine(_dir, "cache", id + ".json");

    private void Load()
    {
        try
        {
            if (File.Exists(SourcesPath))
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(SourcesPath), PersistenceJsonContext.Default.CalendarSourcesFile);
                if (file is not null)
                {
                    _refreshMinutes = AllowedRefreshMinutes.Contains(file.RefreshMinutes) ? file.RefreshMinutes : 15;
                    foreach (var rec in file.Sources)
                    {
                        if (string.IsNullOrEmpty(rec.Id))
                        {
                            continue;
                        }
                        var st = new SourceState { Record = rec, Error = rec.LastError };
                        if (rec.LastSyncedAt is { } ms)
                        {
                            st.LastAttempt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                        }
                        LoadCache(st);
                        _sources.Add(st);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[calendar] could not read sources ({ex.GetType().Name})");
        }
    }

    private void LoadCache(SourceState st)
    {
        try
        {
            var path = CachePath(st.Record.Id);
            if (!File.Exists(path))
            {
                return;
            }
            var cache = JsonSerializer.Deserialize(File.ReadAllText(path), PersistenceJsonContext.Default.CalendarFeedCache);
            if (cache is not null)
            {
                st.Feed = cache.Feed;
                st.ETag = cache.ETag;
                st.LastModified = cache.LastModified;
            }
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[calendar] source {st.Record.Id} cache unreadable ({ex.GetType().Name})");
        }
    }

    private void SaveSources()
    {
        // Callers hold _gate.
        var file = new CalendarSourcesFile { RefreshMinutes = _refreshMinutes, Sources = _sources.Select(s => s.Record).ToList() };
        try
        {
            Directory.CreateDirectory(_dir);
            var path = SourcesPath;
            RestrictToOwner(AtomicJsonFile.TempPathFor(path));
            AtomicJsonFile.Write(path, JsonSerializer.Serialize(file, PersistenceJsonContext.Default.CalendarSourcesFile));
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[calendar] could not write sources ({ex.GetType().Name})");
        }
    }

    private static void RestrictToOwner(string tempPath)
    {
        NexusDataPaths.CreateRestricted(tempPath);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (!File.Exists(tempPath))
                {
                    using var _ = File.Create(tempPath);
                }
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // Best effort: the secret is still no weaker than the other plain-stored ones off Windows.
            }
        }
    }

    private void SaveCache(SourceState st)
    {
        if (st.Feed is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.Combine(_dir, "cache"));
            var cache = new CalendarFeedCache { ETag = st.ETag, LastModified = st.LastModified, Feed = st.Feed };
            AtomicJsonFile.Write(CachePath(st.Record.Id), JsonSerializer.Serialize(cache, PersistenceJsonContext.Default.CalendarFeedCache), flushToDisk: false);
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[calendar] source {st.Record.Id} cache not written ({ex.GetType().Name})");
        }
    }

    // Reads

    public List<CalendarSourceDto> ListSources()
    {
        lock (_gate)
        {
            return _sources.Select(ToDto).ToList();
        }
    }

    private static CalendarSourceDto ToDto(SourceState s) => new()
    {
        Id = s.Record.Id,
        Kind = s.Record.Kind,
        Name = s.Record.Name,
        Color = s.Record.Color,
        Host = s.Record.Host,
        LastSyncedAt = s.Record.LastSyncedAt,
        Status = s.Syncing ? CalendarStatuses.Syncing : s.Error is not null ? CalendarStatuses.Error : CalendarStatuses.Ok,
        Error = s.Syncing ? null : s.Error,
    };

    public CalendarSettingsDto GetSettings()
    {
        lock (_gate)
        {
            return new CalendarSettingsDto
            {
                RefreshMinutes = _refreshMinutes,
                LastSyncedAt = _sources.Select(s => s.Record.LastSyncedAt).Max(),
                Syncing = _sources.Any(s => s.Syncing),
            };
        }
    }

    /// <returns>Null when the value is not one of the allowed intervals.</returns>
    public CalendarSettingsDto? SetRefreshMinutes(int minutes)
    {
        if (!AllowedRefreshMinutes.Contains(minutes))
        {
            return null;
        }
        lock (_gate)
        {
            _refreshMinutes = minutes;
            SaveSources();
        }
        RaiseChanged();
        return GetSettings();
    }

    /// <returns>Null when the range is invalid (not increasing, or wider than the cap).</returns>
    public CalendarEventsResponse? Query(long fromMs, long toMs, IReadOnlyCollection<string>? calendarIds)
    {
        if (toMs <= fromMs || toMs - fromMs > MaxQuerySpanDays * 86_400_000L)
        {
            return null;
        }
        List<(string Id, IcsFeed Feed)> feeds;
        long? syncedAt;
        lock (_gate)
        {
            feeds = _sources
                .Where(s => s.Feed is not null && (calendarIds is null || calendarIds.Count == 0 || calendarIds.Contains(s.Record.Id)))
                .Select(s => (s.Record.Id, s.Feed!))
                .ToList();
            syncedAt = _sources.Select(s => s.Record.LastSyncedAt).Max();
        }
        var events = new List<CalendarEventDto>();
        foreach (var (id, feed) in feeds)
        {
            IcsEventExpander.Expand(feed, id, fromMs, toMs, events);
        }
        events.Sort(static (a, b) =>
        {
            var c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        return new CalendarEventsResponse { Events = events, SyncedAt = syncedAt };
    }

    // Mutations

    public async Task<CalendarAddResult> AddAsync(string? url, string? name, string? color, CancellationToken ct)
    {
        if (!CalendarUrl.TryNormalize(url, out var uri))
        {
            return new CalendarAddResult { Error = CalendarErrors.InvalidUrl };
        }
        SourceState st;
        lock (_gate)
        {
            if (_sources.Count >= MaxSources)
            {
                return new CalendarAddResult { Error = CalendarErrors.InvalidUrl };
            }
            st = new SourceState
            {
                Record = new CalendarSourceRecord
                {
                    Id = "cal_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Kind = CalendarKinds.Ics,
                    Name = CleanName(name, uri.Host),
                    Color = CleanColor(color) ?? "#4f8cff",
                    UrlProtected = SecretProtector.Protect(uri.AbsoluteUri),
                    Host = uri.Host.ToLowerInvariant(),
                },
            };
            _sources.Add(st);
        }
        await RunSyncAsync(st, ct).ConfigureAwait(false);
        string? failure;
        CalendarSourceDto? created = null;
        lock (_gate)
        {
            failure = st.Error;
            if (failure is not null)
            {
                _sources.Remove(st);
                TryDelete(CachePath(st.Record.Id));
            }
            else
            {
                created = ToDto(st);
            }
            SaveSources();
        }
        if (failure is not null)
        {
            RaiseChanged();
            return new CalendarAddResult { Error = failure };
        }
        return new CalendarAddResult { Source = created };
    }

    public CalendarSourceDto? Patch(string id, string? name, string? color)
    {
        CalendarSourceDto dto;
        lock (_gate)
        {
            var st = _sources.FirstOrDefault(s => s.Record.Id == id);
            if (st is null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                st.Record.Name = CleanName(name, st.Record.Name);
            }
            if (CleanColor(color) is { } c)
            {
                st.Record.Color = c;
            }
            SaveSources();
            dto = ToDto(st);
        }
        RaiseChanged();
        return dto;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var st = _sources.FirstOrDefault(s => s.Record.Id == id);
            if (st is null)
            {
                return false;
            }
            _sources.Remove(st);
            TryDelete(CachePath(id));
            SaveSources();
        }
        RaiseChanged();
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // A stranded cache file is harmless: it is never read without its source record.
        }
    }

    private static string CleanName(string? name, string fallback)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0)
        {
            n = fallback;
        }
        return n.Length > 80 ? n.Substring(0, 80) : n;
    }

    internal static string? CleanColor(string? color)
    {
        if (color is null || color.Length != 7 || color[0] != '#')
        {
            return null;
        }
        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(color[i]))
            {
                return null;
            }
        }
        return color.ToLowerInvariant();
    }

    // Sync

    /// <summary>Starts a sync of every source. False while one is running or if the last one finished under the throttle window ago.</summary>
    public bool TryStartRefreshAll()
    {
        var now = _time.GetUtcNow();
        List<SourceState> targets;
        lock (_gate)
        {
            if (_sources.Count == 0 || _sources.Any(s => s.Syncing))
            {
                return false;
            }
            var last = _sources.Max(s => s.LastFinished);
            if (last is { } l && now - l < ManualThrottle)
            {
                return false;
            }
            targets = _sources.ToList();
            foreach (var t in targets)
            {
                t.Syncing = true;
            }
        }
        foreach (var t in targets)
        {
            Launch(t);
        }
        return true;
    }

    /// <returns>Null when the source does not exist, otherwise whether a sync started.</returns>
    public bool? TryStartSync(string id)
    {
        var now = _time.GetUtcNow();
        SourceState? st;
        lock (_gate)
        {
            st = _sources.FirstOrDefault(s => s.Record.Id == id);
            if (st is null)
            {
                return null;
            }
            if (st.Syncing || (st.LastFinished is { } l && now - l < ManualThrottle))
            {
                return false;
            }
            st.Syncing = true;
        }
        Launch(st);
        return true;
    }

    private void Launch(SourceState st)
    {
        var task = Task.Run(() => RunSyncAsync(st, CancellationToken.None, alreadyMarked: true));
        lock (_gate)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(task);
        }
    }

    /// <summary>Completes when every sync started so far has finished.</summary>
    public Task WhenIdleAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = _running.ToArray();
        }
        return Task.WhenAll(pending);
    }

    /// <summary>Syncs every source whose schedule has come due. Called on a short tick by the worker.</summary>
    public async Task RunDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        List<SourceState> due;
        lock (_gate)
        {
            due = _sources.Where(s => !s.Syncing && IsDue(s, now)).ToList();
            foreach (var s in due)
            {
                s.Syncing = true;
            }
        }
        foreach (var s in due)
        {
            await RunSyncAsync(s, ct, alreadyMarked: true).ConfigureAwait(false);
        }
    }

    private bool IsDue(SourceState s, DateTimeOffset now)
    {
        if (s.LastAttempt is not { } last)
        {
            return true;
        }
        var interval = TimeSpan.FromMinutes(_refreshMinutes);
        var delay = interval;
        for (var i = 0; i < s.Failures && delay < MaxBackoff; i++)
        {
            delay += delay;
        }
        if (delay > MaxBackoff)
        {
            delay = MaxBackoff;
        }
        return now - last >= delay;
    }

    private async Task RunSyncAsync(SourceState st, CancellationToken ct, bool alreadyMarked = false)
    {
        if (!alreadyMarked)
        {
            lock (_gate)
            {
                st.Syncing = true;
            }
        }
        RaiseChanged();
        string? error = null;
        try
        {
            if (!Uri.TryCreate(SecretProtector.Unprotect(st.Record.UrlProtected), UriKind.Absolute, out var uri))
            {
                error = CalendarErrors.InvalidUrl;
            }
            else
            {
                string? etag, lastMod;
                lock (_gate)
                {
                    etag = st.Feed is null ? null : st.ETag;
                    lastMod = st.Feed is null ? null : st.LastModified;
                }
                var result = await _fetcher.FetchAsync(uri, etag, lastMod, ct).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case CalendarFetchOutcome.NotModified:
                        break;
                    case CalendarFetchOutcome.Ok:
                        error = ApplyBody(st, result);
                        break;
                    default:
                        error = result.ErrorCode ?? CalendarErrors.Unreachable;
                        break;
                }
                Nexus.Service.Platform.ServiceLog.Info(FormatSyncLog(st.Record.Id, result.HttpStatus, result.Bytes, error));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                st.Syncing = false;
            }
            return;
        }
        catch (Exception ex)
        {
            error = CalendarErrors.Unreachable;
            Nexus.Service.Platform.ServiceLog.Warn($"[calendar] source {st.Record.Id} sync failed ({ex.GetType().Name})");
        }
        var finished = _time.GetUtcNow();
        lock (_gate)
        {
            st.Syncing = false;
            st.LastAttempt = finished;
            st.LastFinished = finished;
            st.Error = error;
            st.Record.LastError = error;
            if (error is null)
            {
                st.Failures = 0;
                st.Record.LastSyncedAt = finished.ToUnixTimeMilliseconds();
            }
            else
            {
                st.Failures++;
            }
            if (_sources.Contains(st))
            {
                SaveSources();
            }
        }
        RaiseChanged();
    }

    /// <summary>The only line a sync logs: ids, a status code, a byte count and an error code. Never a title, location, note, URL or host.</summary>
    internal static string FormatSyncLog(string sourceId, int httpStatus, int bytes, string? error) =>
        $"[calendar] source {sourceId} sync status={httpStatus} bytes={bytes} error={error ?? "none"}";

    private string? ApplyBody(SourceState st, CalendarFetchResult result)
    {
        IcsFeed feed;
        try
        {
            feed = IcsParser.Parse(result.Body ?? "");
        }
        catch (Exception)
        {
            return CalendarErrors.NotIcs;
        }
        lock (_gate)
        {
            st.Feed = feed;
            st.ETag = result.ETag;
            st.LastModified = result.LastModified;
        }
        SaveCache(st);
        return null;
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A broken subscriber must not fail a sync.
        }
    }
}
