using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nexus.Service.Telemetry;

/// <summary>Emits app_used (and page_viewed, keyed "page:" in the same set) at most once per key per UTC day. The day's set lives in a small file so a restart does not re-emit.</summary>
public sealed class AppUsageTracker
{
    private readonly ITelemetry _telemetry;
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private string _day = "";
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _loaded;

    public AppUsageTracker(ITelemetry telemetry, string? path = null, Func<DateTime>? utcNow = null)
    {
        _telemetry = telemetry;
        _path = path ?? Path.Combine(Persistence.NexusDataPaths.NexusRoot(), "app-usage-day.json");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // Page keys come from caller-chosen slugs, so the day's set is capped; the per-day reset bounds it across days.
    private const int MaxKeysPerDay = 512;
    private const string PagePrefix = "page:";

    public void Note(string appId, string appVersion)
    {
        if (!TryMark(appId, isPage: false)) return;
        _telemetry.Capture(TelemetryEvents.AppUsed, ("app_id", appId), ("app_version", appVersion));
    }

    public void NotePage(string page)
    {
        if (!TryMark(PagePrefix + page, isPage: true)) return;
        _telemetry.Capture(TelemetryEvents.PageViewed, ("page", page));
    }

    private bool TryMark(string key, bool isPage)
    {
        lock (_lock)
        {
            var today = _utcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (!_loaded)
            {
                Load();
                _loaded = true;
            }
            if (_day != today)
            {
                _day = today;
                _seen.Clear();
            }
            if (isPage && _seen.Count >= MaxKeysPerDay) return false;
            if (!_seen.Add(key)) return false;
            Save();
            return true;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            _day = doc.RootElement.GetProperty("day").GetString() ?? "";
            foreach (var id in doc.RootElement.GetProperty("apps").EnumerateArray())
            {
                if (id.GetString() is { } s) _seen.Add(s);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _day = "";
            _seen.Clear();
        }
    }

    private void Save()
    {
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("day", _day);
                w.WriteStartArray("apps");
                foreach (var id in _seen) w.WriteStringValue(id);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a failed write costs at most one duplicate event after a restart.
        }
    }
}
