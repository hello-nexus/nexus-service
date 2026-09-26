using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
/// the <see cref="ConflictLaunchNotifier"/> notice.
/// </summary>
public sealed class ConflictStartupShutdown : IHostedService
{
    /// <summary>
    /// Raised after each pass that ended something (the sweep, then each launch
    /// in the window), with the display names of the apps actually ended. The
    /// Windows tray bootstrap turns this into a native notification; the
    /// service runs in session 0 and cannot draw UI itself.
    /// </summary>
    public event Action<IReadOnlyList<string>>? AppsTerminated;

    /// <summary>Unmeasured: long enough for most Run-key and at-logon-task apps to come up.</summary>
    internal static readonly TimeSpan LaunchKillWindow = TimeSpan.FromSeconds(30);

    private readonly IConfigStore _store;
    private readonly IConflictDetector _watcher;
    private readonly ILogger<ConflictStartupShutdown> _log;
    private readonly Action<ConflictAppDefinition> _kill;
    private readonly Func<ConflictAppDefinition, bool> _stillRunning;
    private readonly CancellationTokenSource _stopping = new();

    // id:pid pairs already ended (or tried) this run, so a kill that did not
    // stick is not retried every tick while a relaunch under a new pid is.
    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private long _windowEndsMs = long.MinValue;
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
        Func<ConflictAppDefinition, bool> stillRunning)
    {
        _store = store;
        _watcher = watcher;
        _log = log;
        _kill = kill;
        _stillRunning = stillRunning;
    }

    /// <summary>True while a launching non-whitelisted app is ended here; the launch notifier stays quiet for that stretch.</summary>
    public bool InLaunchKillWindow =>
        Environment.TickCount64 < Volatile.Read(ref _windowEndsMs) && SweepAllowed(_store.Load());

    /// <summary>The switch, plus every onboarding flag: a fresh install's first
    /// start comes before the onboarding conflict step, which is where the user
    /// sees these apps and decides about them, so the sweep waits until the
    /// sequence has finished (or been skipped) before it ever ends one.</summary>
    internal static bool SweepAllowed(NexusSettings settings) =>
        settings.Ui.AutoKillConflictsAtStartup
        && settings.OnboardingCompleted
        && settings.FeaturesOnboardingCompleted
        && settings.LightingOnboardingCompleted;

    public Task StartAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        Nexus.Service.Lifecycle.WindowsServiceHost.SessionLogon += OnSessionLogon;
#endif
        if (!SweepAllowed(_store.Load())) return Task.CompletedTask;

        OpenLaunchKillWindow();
        // Off the startup path: the scan plus each kill's exit wait would
        // otherwise stall every hosted service queued behind this one.
        _ = Task.Run(async () =>
        {
            // Nothing awaits the sweep, so an escape here would be a silent no-op.
            try
            {
                TurnOffWindowsDynamicLighting();
                EndRunningApps();
            }
            catch (Exception ex) { _log.LogWarning(ex, "Startup conflict shutdown swept nothing; enumeration failed."); }
            await WatchLaunchKillWindowAsync().ConfigureAwait(false);
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private void OnSessionLogon()
    {
        if (!SweepAllowed(_store.Load())) return;
        OpenLaunchKillWindow();
        _ = WatchLaunchKillWindowAsync();
    }

    private void OpenLaunchKillWindow() =>
        Volatile.Write(ref _windowEndsMs, Environment.TickCount64 + (long)LaunchKillWindow.TotalMilliseconds);

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
                    if (SweepAllowed(_store.Load())) EndRunningApps();
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

    /// <summary>Ends every detected app not on the whitelist and not already tried under its current pid, then raises <see cref="AppsTerminated"/> for the ones confirmed gone.</summary>
    internal void EndRunningApps()
    {
        // The boot sweep and a logon-started window loop can overlap.
        lock (_attempted) EndRunningAppsLocked();
    }

    private void EndRunningAppsLocked()
    {
        var excluded = new HashSet<string>(_store.Load().Ui.ConflictAutoKillExclusions, StringComparer.OrdinalIgnoreCase);

        // The "before" set is captured up front, for every target, and the
        // outcome is read from it afterwards. Per-app Killed flags undercount:
        // ProcessKiller tree-kills, and a vendor launcher can own another
        // catalog entry's process as a child, so ending the first target can
        // take a later one down with it. That one is already gone when its own
        // turn comes, reports "not running before me", and drops out of the
        // notification the user sees.
        var targets = new List<ConflictAppDefinition>();
        foreach (var detected in _watcher.GetConflicts())
        {
            if (excluded.Contains(detected.Id)) continue;
            if (!_attempted.Add($"{detected.Id}:{detected.Pid}")) continue;
            var def = ConflictWatcher.FindById(detected.Id);
            if (def is not null) targets.Add(def);
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
                killed.Add(def.DisplayName);
                _log.LogInformation("Conflict shutdown: ended {App}.", def.DisplayName);
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
