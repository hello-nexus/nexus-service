using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Platform;

namespace Nexus.Service.Conflicts;

/// <summary>Restarts the OpenRGB daemon after a competing RGB app exits: detection runs only at daemon start, so a controller the app held stays missing or in the app's hardware mode until then.</summary>
public sealed class ConflictExitRecovery : BackgroundService
{
    /// <summary>Above the watcher's own threshold so every tick scans rather than reading the cache.</summary>
    private static readonly TimeSpan PollInterval = ConflictWatcher.PollInterval + TimeSpan.FromSeconds(1);

    private readonly IConflictDetector _detector;
    private readonly Func<bool> _bridgeActive;
    private readonly Action _recover;
    private readonly ConflictExitTracker _tracker = new();

    public ConflictExitRecovery(IConflictDetector detector, RgbBridge bridge)
        : this(detector, () => bridge.IsActive, bridge.RecoverAfterConflictExit)
    {
    }

    internal ConflictExitRecovery(IConflictDetector detector, Func<bool> bridgeActive, Action recover)
    {
        _detector = detector;
        _bridgeActive = bridgeActive;
        _recover = recover;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { Tick(Environment.TickCount64); }
                catch (Exception ex) { Console.Error.WriteLine($"[conflicts] exit recovery tick failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void Tick(long nowMs)
    {
        // Lighting off: the next Activate spawns a fresh daemon, which detects from scratch.
        if (!_bridgeActive())
        {
            _tracker.Reset();
            return;
        }

        var exited = _tracker.Observe(_detector.GetConflicts(), nowMs, out var capped);
        if (capped is not null)
        {
            ServiceLog.Info($"[conflicts] {string.Join(", ", capped)} keeps cycling; no further OpenRGB restarts for it for {ConflictExitTracker.CapWindow.TotalMinutes:0} min");
        }
        if (exited is null) return;

        ServiceLog.Info($"[conflicts] {string.Join(", ", exited)} closed; restarting OpenRGB to reclaim its devices");
        _recover();
    }
}

/// <summary>Decides, one scan at a time, when a competing RGB app's exit warrants a daemon restart. Pure: the caller supplies the clock.</summary>
internal sealed class ConflictExitTracker
{
    /// <summary>An app back inside this window (updater relaunch, crash loop) cancels the restart its exit armed.</summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    /// <summary>Floor between two restarts, so an app cycling slower than the debounce cannot hold the daemon in detection.</summary>
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    /// <summary>Restarts one app may cause per <see cref="CapWindow"/>; past it, its exits are ignored until the window rolls.</summary>
    internal const int MaxRestartsPerWindow = 3;

    internal static readonly TimeSpan CapWindow = TimeSpan.FromMinutes(10);

    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exited = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _restartCounts = new(StringComparer.OrdinalIgnoreCase);
    private long _capWindowStartMs;
    private long _dueMs;
    private long _lastRestartMs = long.MinValue / 2;

    /// <summary>Feeds one scan. Returns the display names of the exited apps when the daemon should restart now, else null; <paramref name="capped"/> names apps whose exit just crossed the per-window cap.</summary>
    public IReadOnlyList<string>? Observe(IReadOnlyList<DetectedConflict> detected, long nowMs, out IReadOnlyList<string>? capped)
    {
        capped = null;
        if (nowMs - _capWindowStartMs > (long)CapWindow.TotalMilliseconds)
        {
            _capWindowStartMs = nowMs;
            _restartCounts.Clear();
        }

        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in detected)
        {
            if (CompetesForOpenRgb(conflict.Id))
                current.Add(conflict.Id);
        }

        List<string>? cappedNow = null;
        foreach (var id in _present)
        {
            if (current.Contains(id))
                continue;
            _restartCounts.TryGetValue(id, out var restarts);
            if (restarts >= MaxRestartsPerWindow)
            {
                if (restarts == MaxRestartsPerWindow)
                {
                    _restartCounts[id] = restarts + 1;
                    (cappedNow ??= new()).Add(DisplayName(id));
                }
                continue;
            }
            _exited.Add(id);
            _dueMs = Math.Max(nowMs + (long)Debounce.TotalMilliseconds, _lastRestartMs + (long)MinInterval.TotalMilliseconds);
        }
        _exited.RemoveWhere(current.Contains);

        _present.Clear();
        _present.UnionWith(current);
        capped = cappedNow;

        if (_exited.Count == 0 || nowMs < _dueMs)
            return null;

        var names = new List<string>(_exited.Count);
        foreach (var id in _exited)
        {
            names.Add(DisplayName(id));
            _restartCounts.TryGetValue(id, out var restarts);
            _restartCounts[id] = restarts + 1;
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        _exited.Clear();
        _lastRestartMs = nowMs;
        return names;
    }

    /// <summary>Forgets every app seen; the next scan is a fresh baseline and never reads as an exit. The restart floor and cap survive.</summary>
    public void Reset()
    {
        _present.Clear();
        _exited.Clear();
    }

    /// <summary>Universal RGB apps and the lighting category; peripheral suites (deck, mouse, keyboard tools) hold no OpenRGB controller worth a restart.</summary>
    internal static bool CompetesForOpenRgb(string id)
    {
        var def = ConflictWatcher.FindById(id);
        return def is not null && (def.ClaimsAllRgb || string.Equals(def.Category, "lighting", StringComparison.OrdinalIgnoreCase));
    }

    private static string DisplayName(string id) => ConflictWatcher.FindById(id)?.DisplayName ?? id;
}
