using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Displays;
using Nexus.Service.Serialization;

namespace Nexus.Service.Panel;

/// <summary>
/// Holds the display identify in progress and pushes it to the overlay host.
/// Windows hosts read it from /overlay/state after a wake; the macOS helper
/// takes it as a stdin line.
/// </summary>
public sealed class DisplayIdentifyService
{
    public const int DurationMs = 3000;
    private const int MaxLabelChars = 8;
    private const int MaxNameChars = 40;
    private const int MaxDisplays = 32;
    private const int MaxIdChars = 128;

    private readonly IOverlayHost _host;
    private readonly Action? _wakeOverlay;
    private readonly Func<long> _nowMs;
    private readonly Func<IEnumerable<string>?>? _knownDisplayIds;
    private readonly object _lock = new();
    // Orders delivery so a slower Begin can never land an older token after a newer one.
    private readonly object _sendLock = new();
    private long _token;
    private long _expiresMs;
    private List<IdentifyDisplayEntry> _displays = new();

    public DisplayIdentifyService(IOverlayHost host, Action? wakeOverlay = null, Func<long>? nowMs = null, Func<IEnumerable<string>?>? knownDisplayIds = null)
    {
        _knownDisplayIds = knownDisplayIds;
        _host = host;
        _wakeOverlay = wakeOverlay;
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    public bool IsActive => Current() is not null;

    /// <summary>Starts or restarts an identify. Returns the entries kept after sanitizing.</summary>
    public IReadOnlyList<IdentifyDisplayEntry> Begin(IEnumerable<IdentifyDisplayEntry>? requested)
    {
        var known = _knownDisplayIds?.Invoke();
        var kept = Sanitize(requested, known is null ? null : new HashSet<string>(known, StringComparer.Ordinal));
        lock (_lock)
        {
            _token++;
            _displays = kept;
            _expiresMs = kept.Count == 0 ? 0 : _nowMs() + DurationMs;
        }
        if (kept.Count == 0) return kept;

        lock (_sendLock)
        {
            // The latest state at send time, not this call's: a concurrent newer Begin wins.
            OverlayIdentifyDto snapshot;
            lock (_lock)
            {
                if (_displays.Count == 0) return kept;
                snapshot = SnapshotLocked();
            }
            // Idempotent: a host that is not up yet reads the state when it boots.
            if (!_host.IsRunning)
            {
                try { _host.Start(); } catch { }
            }
            try { _host.NotifyIdentify(JsonSerializer.Serialize(snapshot, AppJsonContext.Default.OverlayIdentifyDto)); }
            catch { }
            try { _wakeOverlay?.Invoke(); } catch { }
        }
        return kept;
    }

    /// <summary>The identify still showing, or null.</summary>
    public OverlayIdentifyDto? Current()
    {
        lock (_lock)
        {
            if (_displays.Count == 0 || _nowMs() >= _expiresMs) return null;
            return SnapshotLocked();
        }
    }

    private OverlayIdentifyDto SnapshotLocked() => new()
    {
        Token = _token,
        RemainingMs = (int)Math.Max(0, _expiresMs - _nowMs()),
        Displays = new List<IdentifyDisplayEntry>(_displays),
    };

    internal static List<IdentifyDisplayEntry> Sanitize(IEnumerable<IdentifyDisplayEntry>? requested, ISet<string>? knownIds = null)
    {
        var kept = new List<IdentifyDisplayEntry>();
        if (requested is null) return kept;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in requested)
        {
            if (kept.Count >= MaxDisplays) break;
            var id = e?.DisplayId?.Trim() ?? "";
            if (id.Length == 0 || id.Length > MaxIdChars || (knownIds is not null && !knownIds.Contains(id)) || !seen.Add(id)) continue;
            var name = Clip(e!.Name, MaxNameChars);
            kept.Add(new IdentifyDisplayEntry
            {
                DisplayId = id,
                Label = Clip(e.Label, MaxLabelChars),
                Name = name.Length == 0 ? null : name,
            });
        }
        return kept;
    }

    private static string Clip(string? s, int max)
    {
        var t = (s ?? "").Trim();
        if (t.Length <= max) return t;
        // Whole text elements, so a surrogate pair or combining sequence is never split.
        var end = 0;
        var elements = StringInfo.GetTextElementEnumerator(t);
        for (var n = 0; n < max && elements.MoveNext(); n++)
        {
            end = elements.ElementIndex + ((string)elements.Current).Length;
        }
        return t[..end];
    }
}
