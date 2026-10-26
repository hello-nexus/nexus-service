using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private readonly IOverlayHost _host;
    private readonly Action? _wakeOverlay;
    private readonly Func<long> _nowMs;
    private readonly object _lock = new();
    private long _token;
    private long _expiresMs;
    private List<IdentifyDisplayEntry> _displays = new();

    public DisplayIdentifyService(IOverlayHost host, Action? wakeOverlay = null, Func<long>? nowMs = null)
    {
        _host = host;
        _wakeOverlay = wakeOverlay;
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    public bool IsActive => Current() is not null;

    /// <summary>Starts or restarts an identify. Returns the entries kept after sanitizing.</summary>
    public IReadOnlyList<IdentifyDisplayEntry> Begin(IEnumerable<IdentifyDisplayEntry>? requested)
    {
        var kept = Sanitize(requested);
        OverlayIdentifyDto snapshot;
        lock (_lock)
        {
            _token++;
            _displays = kept;
            _expiresMs = kept.Count == 0 ? 0 : _nowMs() + DurationMs;
            snapshot = SnapshotLocked();
        }
        if (kept.Count == 0) return kept;

        // Idempotent: a host that is not up yet reads the state when it boots.
        if (!_host.IsRunning)
        {
            try { _host.Start(); } catch { }
        }
        try { _host.NotifyIdentify(JsonSerializer.Serialize(snapshot, AppJsonContext.Default.OverlayIdentifyDto)); }
        catch { }
        try { _wakeOverlay?.Invoke(); } catch { }
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

    internal static List<IdentifyDisplayEntry> Sanitize(IEnumerable<IdentifyDisplayEntry>? requested)
    {
        var kept = new List<IdentifyDisplayEntry>();
        if (requested is null) return kept;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in requested)
        {
            if (kept.Count >= MaxDisplays) break;
            var id = e?.DisplayId?.Trim() ?? "";
            if (id.Length == 0 || !seen.Add(id)) continue;
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
        return t.Length <= max ? t : t[..max];
    }
}
