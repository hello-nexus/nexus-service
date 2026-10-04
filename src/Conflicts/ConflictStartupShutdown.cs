using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Persistence;

namespace Nexus.Service.Conflicts;

/// <summary>
/// One-shot: at service start, terminate every conflicting app the watcher
/// currently detects that the user has not excluded, so Nexus takes the
/// hardware before a competing vendor tool grabs it. Gated on
/// <c>Ui.AutoKillConflictsAtStartup</c> and on onboarding having run (see
/// <see cref="SweepAllowed"/>).
///
/// Windows' own Dynamic Lighting rides along under the same switch: it is the
/// one conflict that cannot be ended, only turned off.
///
/// Driven off the watcher's detected set rather than walking the whole
/// catalog: the watcher already filters out our own bundled OpenRGB child by
/// install path, and one process-list scan replaces ~50.
///
/// Then, for <see cref="LaunchKillWindow"/> after service start and after each
/// logon, it also ends every non-whitelisted app that launches: the service
/// starts in session 0, before the vendor apps that launch at logon, so the
/// one-shot sweep alone misses them. Outside the window a launch only raises
/// the <see cref="ConflictLaunchNotifier"/> notice, unless
/// <c>Ui.EndConflictsOnLaunch</c> ends it at any time. An
/// <see cref="ConflictAppDefinition.AlwaysEnded"/> app (Nexus 2) is ended at
/// the same points with no switch, whitelist or onboarding gate.
/// </summary>
public sealed class ConflictStartupShutdown : IHostedService
{
    /// <summary>
    /// Raised after each pass that ended something (the sweep, each launch in
    /// the window, or the end-on-launch switch), with the display names of the apps actually ended. The
    /// Windows tray bootstrap turns this into a native notification; the
    /// service runs in session 0 and cannot draw UI itself.
    /// </summary>
    public event Action<IReadOnlyList<string>>? AppsTerminated;

    /// <summary>Raised once per process the end-on-launch path tried to end that is still running <see cref="SurvivorGrace"/> later, so the user can retry from a notice.</summary>
    public event Action<DetectedConflict>? AppSurvived;

    /// <summary>An ended iCUE stayed in the scan for a couple of polls on T1; a shorter wait would report apps already on their way out.</summary>
    internal static readonly TimeSpan SurvivorGrace = TimeSpan.FromSeconds(20);

    /// <summary>Unmeasured: long enough for most Run-key and at-logon-task apps to come up.</summary>
    internal static readonly TimeSpan LaunchKillWindow = TimeSpan.FromSeconds(30);

    private readonly IConfigStore _store;
    private readonly IConflictDetector _watcher;
    private readonly ILogger<ConflictStartupShutdown> _log;
    private readonly Action<ConflictAppDefinition> _kill;
    private readonly Func<ConflictAppDefinition, bool> _stillRunning;
    private readonly Func<long> _clockMs;
    private readonly CancellationTokenSource _stopping = new();

    // id:pid pairs already ended (or tried) and still detected, so a kill that
    // did not stick is not retried every tick while a relaunch under a new pid is.
    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    // App id -> when the end-on-launch path last reported ending it.
    private readonly Dictionary<string, long> _lastNoticeMs = new(StringComparer.OrdinalIgnoreCase);
    // id:pid -> when the end-on-launch path first tried it; long.MaxValue once reported as surviving.
    private readonly Dictionary<string, long> _endTriedAtMs = new(StringComparer.OrdinalIgnoreCase);
    private long _windowEndsMs = long.MinValue;
    // The full sweep was allowed when the current window opened; otherwise the window ends AlwaysEnded apps only.
    private volatile bool _sweepWindow;
    private int _watching;

    public ConflictStartupShutdown(IConfigStore store, IConflictDetector watcher, ILogger<ConflictStartupShutdown> log)
        : this(store, watcher, log, def => ConflictKiller.Kill(def), ConflictKiller.AnyProcessRunning)
    {
    }

    internal ConflictStartupShutdown(
        IConfigStore store,
        IConflictDetector watcher,
        ILogger<ConflictStartupShutdown> log,
        Action<ConflictAppDefinition> kill,
        Func<ConflictAppDefinition, bool> stillRunning,
        Func<long>? clockMs = null)
    {
        _store = store;
        _watcher = watcher;
        _log = log;
        _kill = kill;
        _stillRunning = stillRunning;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    /// <summary>True while a launching non-whitelisted app is ended here; the launch notifier stays quiet for that stretch.</summary>
    public bool InLaunchKillWindow
    {
        get
        {
            var settings = _store.Load();
            return (LaunchWindowOpen && _sweepWindow && SweepAllowed(settings)) || EndOnLaunchAllowed(settings);
        }
    }

    /// <summary>True while a launching <see cref="ConflictAppDefinition.AlwaysEnded"/> app is ended here.</summary>
    public bool LaunchWindowOpen => Environment.TickCount64 < Volatile.Read(ref _windowEndsMs);

    /// <summary>The switch, plus every onboarding flag: a fresh install's first
    /// start comes before the onboarding conflict step, which is where the user
    /// sees these apps and decides about them, so the sweep waits until the
    /// sequence has finished (or been skipped) before it ever ends one.</summary>
    internal static bool SweepAllowed(NexusSettings settings) =>
        settings.Ui.AutoKillConflictsAtStartup && OnboardingDone(settings);

    /// <summary>The end-on-launch switch, behind the same onboarding gate as the sweep.</summary>
    internal static bool EndOnLaunchAllowed(NexusSettings settings) =>
        settings.Ui.EndConflictsOnLaunch && OnboardingDone(settings);

    private static bool OnboardingDone(NexusSettings settings) =>
        settings.OnboardingCompleted
        && settings.FeaturesOnboardingCompleted
        && settings.LightingOnboardingCompleted;

    public Task StartAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        Nexus.Service.Lifecycle.WindowsServiceHost.SessionLogon += OnSessionLogon;
#endif
        OpenLaunchKillWindow();
        // Off the startup path: the scan plus each kill's exit wait would
        // otherwise stall every hosted service queued behind this one.
        _ = Task.Run(async () =>
        {
            // Nothing awaits the sweep, so an escape here would be a silent no-op.
            try
            {
                var sweep = _sweepWindow && SweepAllowed(_store.Load());
                if (sweep) TurnOffWindowsDynamicLighting();
                EndRunningApps(sweep);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Startup conflict shutdown swept nothing; enumeration failed."); }
            await WatchLaunchKillWindowAsync().ConfigureAwait(false);
        }, CancellationToken.None);
        _ = EndOnLaunchAsync();
        return Task.CompletedTask;
    }

    /// <summary>Reads the watcher's shared scan, so it adds no process enumeration of its own; with the switch off a tick only reads the cached settings.</summary>
    private async Task EndOnLaunchAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(ConflictWatcher.PollInterval);
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                try { EndOnLaunchTick(); }
                catch (Exception ex) { _log.LogWarning(ex, "Conflict end-on-launch tick failed."); }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void EndOnLaunchTick()
    {
        if (EndOnLaunchAllowed(_store.Load())) EndRunningApps(throttleNotice: true);
    }

    private void OnSessionLogon()
    {
        OpenLaunchKillWindow();
        _ = WatchLaunchKillWindowAsync();
    }

    private void OpenLaunchKillWindow()
    {
        _sweepWindow = SweepAllowed(_store.Load());
        Volatile.Write(ref _windowEndsMs, Environment.TickCount64 + (long)LaunchKillWindow.TotalMilliseconds);
    }

    private async Task WatchLaunchKillWindowAsync()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        try
        {
            using var timer = new PeriodicTimer(ConflictWatcher.PollInterval);
            while (Environment.TickCount64 < Volatile.Read(ref _windowEndsMs)
                && await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    EndRunningApps(_sweepWindow && SweepAllowed(_store.Load()));
                }
                catch (Exception ex) { _log.LogWarning(ex, "Conflict launch window tick failed."); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Volatile.Write(ref _watching, 0);
        }
        // A logon that reopened the window as this loop was leaving found it
        // still marked as watching, so the loop restarts for it here.
        if (!_stopping.IsCancellationRequested && Environment.TickCount64 < Volatile.Read(ref _windowEndsMs))
        {
            _ = WatchLaunchKillWindowAsync();
        }
    }

    /// <summary>Ends every detected app not on the whitelist (only <see cref="ConflictAppDefinition.AlwaysEnded"/> ones without <paramref name="sweep"/>) and not already tried under its current pid, then raises <see cref="AppsTerminated"/> for the ones confirmed gone.</summary>
    internal void EndRunningApps(bool sweep = true, bool throttleNotice = false)
    {
        // The boot sweep and a logon-started window loop can overlap.
        lock (_attempted) EndRunningAppsLocked(sweep, throttleNotice);
    }

    private void EndRunningAppsLocked(bool sweep, bool throttleNotice)
    {
        var excluded = new HashSet<string>(_store.Load().Ui.ConflictAutoKillExclusions, StringComparer.OrdinalIgnoreCase);

        // The "before" set is captured up front, for every target, and the
        // outcome is read from it afterwards. Per-app Killed flags undercount:
        // ProcessKiller tree-kills, and a vendor launcher can own another
        // catalog entry's process as a child, so ending the first target can
        // take a later one down with it. That one is already gone when its own
        // turn comes, reports "not running before me", and drops out of the
        // notification the user sees.
        var detectedNow = _watcher.GetConflicts();
        var current = new HashSet<string>(detectedNow.Select(d => $"{d.Id}:{d.Pid}"), StringComparer.OrdinalIgnoreCase);
        // Forget pids that are gone, so a self-restarting app cannot grow the sets without bound.
        _attempted.IntersectWith(current);
        foreach (var gone in _endTriedAtMs.Keys.Where(k => !current.Contains(k)).ToList()) _endTriedAtMs.Remove(gone);
        var now = _clockMs();
        var targets = new List<ConflictAppDefinition>();
        var survivors = new List<DetectedConflict>();
        foreach (var detected in detectedNow)
        {
            var def = ConflictWatcher.FindById(detected.Id);
            if (def is null) continue;
            if (!def.AlwaysEnded && (!sweep || excluded.Contains(detected.Id))) continue;
            var key = $"{detected.Id}:{detected.Pid}";
            if (throttleNotice) _endTriedAtMs.TryAdd(key, now);
            if (!_attempted.Add(key))
            {
                if (throttleNotice && SurvivedEnd(key, now)) survivors.Add(detected);
                continue;
            }
            targets.Add(def);
        }
        foreach (var app in survivors)
        {
            _log.LogInformation("Conflict shutdown: {App} is still running after being ended; notifying.", app.DisplayName);
            try { AppSurvived?.Invoke(app); }
            catch (Exception ex) { _log.LogWarning(ex, "Conflict survivor notification failed."); }
        }
        if (targets.Count == 0) return;

        foreach (var def in targets)
        {
            try { _kill(def); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Conflict shutdown failed for {App}; leaving it running.", def.DisplayName);
            }
        }

        var killed = new List<string>();
        foreach (var def in targets)
        {
            try
            {
                if (_stillRunning(def))
                {
                    _log.LogInformation("Conflict shutdown: {App} is still running.", def.DisplayName);
                    continue;
                }
                _log.LogInformation("Conflict shutdown: ended {App}.", def.DisplayName);
                if (throttleNotice && !NoticeDue(def.Id)) continue;
                killed.Add(def.DisplayName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Conflict shutdown could not confirm {App}.", def.DisplayName);
            }
        }
        if (killed.Count == 0) return;

        _log.LogInformation("Conflict shutdown ended {Count} app(s).", killed.Count);
        try { AppsTerminated?.Invoke(killed); }
        catch (Exception ex) { _log.LogWarning(ex, "Conflict shutdown notification failed."); }
    }

    private bool SurvivedEnd(string key, long now)
    {
        var since = _endTriedAtMs[key];
        if (since == long.MaxValue || now - since < (long)SurvivorGrace.TotalMilliseconds) return false;
        _endTriedAtMs[key] = long.MaxValue;
        return true;
    }

    /// <summary>One notice per app per <see cref="ConflictLaunchTracker.Cooldown"/>: a vendor service that restarts itself is ended every poll.</summary>
    private bool NoticeDue(string appId)
    {
        var now = _clockMs();
        if (_lastNoticeMs.TryGetValue(appId, out var last) && now - last < (long)ConflictLaunchTracker.Cooldown.TotalMilliseconds) return false;
        _lastNoticeMs[appId] = now;
        return true;
    }

    /// <summary>Unconditional: gating on a device being present would race HID enumeration at boot, and this sweep never runs twice.</summary>
    private void TurnOffWindowsDynamicLighting()
    {
        if (!WindowsDynamicLighting.IsSupported()) return;
        try
        {
            WindowsDynamicLighting.Write(enabled: false);
            _log.LogInformation("Startup conflict shutdown: switched Windows Dynamic Lighting off.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup conflict shutdown could not switch Windows Dynamic Lighting off.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        Nexus.Service.Lifecycle.WindowsServiceHost.SessionLogon -= OnSessionLogon;
#endif
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
