using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Conflicts;

/// <summary>Raises <see cref="AppLaunched"/> when a catalog conflict app starts while the service runs, gated on <c>Ui.NotifyConflictLaunches</c>. Apps already running at the first scan are the baseline, not launches, and apps in <c>Ui.ConflictAutoKillExclusions</c> are never announced: the user keeps those running on purpose.</summary>
public sealed class ConflictLaunchNotifier : BackgroundService
{
    /// <summary>Above the watcher's own threshold so every tick scans rather than reading the cache.</summary>
    private static readonly TimeSpan PollInterval = ConflictWatcher.PollInterval + TimeSpan.FromSeconds(1);

    private readonly IConflictDetector _detector;
    private readonly IConfigStore _store;
    private readonly ConflictLaunchTracker _tracker = new();

    public ConflictLaunchNotifier(IConflictDetector detector, IConfigStore store)
    {
        _detector = detector;
        _store = store;
    }

    /// <summary>Raised on the poll thread, once per launch that clears the per-app cooldown.</summary>
    public event Action<DetectedConflict>? AppLaunched;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { Tick(Environment.TickCount64); }
                catch (Exception ex) { Console.Error.WriteLine($"[conflicts] launch notifier tick failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void Tick(long nowMs)
    {
        // Off: no scan at all, and the next enable re-seeds the baseline instead
        // of announcing every app that is already running.
        var ui = _store.Load().Ui;
        if (!ui.NotifyConflictLaunches)
        {
            _tracker.Reset();
            return;
        }

        foreach (var app in _tracker.Observe(_detector.GetConflicts(), nowMs))
        {
            if (ui.ConflictAutoKillExclusions.Exists(id => string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase))) continue;
            ServiceLog.Info($"[conflicts] {app.DisplayName} opened while Nexus is running; notifying");
            try { AppLaunched?.Invoke(app); }
            catch (Exception ex) { ServiceLog.Warn($"[conflicts] launch notice failed for {app.DisplayName}: {ex.Message}"); }
        }
    }
}

/// <summary>Decides, one scan at a time, which conflict apps just started. Pure: the caller supplies the clock.</summary>
internal sealed class ConflictLaunchTracker
{
    /// <summary>Floor between two notices for one app, so an updater relaunch or a crash loop raises one notice rather than a stream.</summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private HashSet<string>? _present;
    private readonly Dictionary<string, long> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops the baseline; the next scan re-seeds it without reporting launches.</summary>
    public void Reset() => _present = null;

    public List<DetectedConflict> Observe(IReadOnlyList<DetectedConflict> running, long nowMs)
    {
        var launched = new List<DetectedConflict>();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in running)
        {
            if (!present.Add(app.Id)) continue;
            if (_present is null || _present.Contains(app.Id)) continue;
            if (_lastNotified.TryGetValue(app.Id, out var at) && nowMs - at < (long)Cooldown.TotalMilliseconds) continue;
            _lastNotified[app.Id] = nowMs;
            launched.Add(app);
        }
        _present = present;
        return launched;
    }
}
