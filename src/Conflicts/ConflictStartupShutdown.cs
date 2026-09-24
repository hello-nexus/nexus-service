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
/// Runs once and returns - it is not a watcher. An app the user launches
/// after boot still only surfaces in the conflict warning; this never kills
/// anything a second time.
/// </summary>
public sealed class ConflictStartupShutdown : IHostedService
{
    /// <summary>
    /// Raised once, after the sweep, with the display names of the apps that
    /// were actually ended - never for a sweep that ended nothing. The Windows
    /// tray bootstrap turns this into a native notification; the service runs
    /// in session 0 and cannot draw UI itself.
    /// </summary>
    public event Action<IReadOnlyList<string>>? AppsTerminated;

    private readonly IConfigStore _store;
    private readonly ConflictWatcher _watcher;
    private readonly ILogger<ConflictStartupShutdown> _log;

    public ConflictStartupShutdown(IConfigStore store, ConflictWatcher watcher, ILogger<ConflictStartupShutdown> log)
    {
        _store = store;
        _watcher = watcher;
        _log = log;
    }

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
        var settings = _store.Load();
        if (!SweepAllowed(settings)) return Task.CompletedTask;

        var excluded = new HashSet<string>(
            settings.Ui.ConflictAutoKillExclusions,
            StringComparer.OrdinalIgnoreCase);

        // Off the startup path: the scan plus each kill's exit wait would
        // otherwise stall every hosted service queued behind this one.
        _ = Task.Run(() => RunSweep(excluded), CancellationToken.None);
        return Task.CompletedTask;
    }

    private void RunSweep(HashSet<string> excluded)
    {
        // Nothing awaits the sweep, so an escape here would be a silent no-op.
        try { RunSweepCore(excluded); }
        catch (Exception ex) { _log.LogWarning(ex, "Startup conflict shutdown swept nothing; enumeration failed."); }
    }

    private void RunSweepCore(HashSet<string> excluded)
    {
        TurnOffWindowsDynamicLighting();

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
            var def = ConflictWatcher.FindById(detected.Id);
            if (def is not null) targets.Add(def);
        }

        foreach (var def in targets)
        {
            try { ConflictKiller.Kill(def); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Startup conflict shutdown failed for {App}; leaving it running.", def.DisplayName);
            }
        }

        var killed = new List<string>();
        foreach (var def in targets)
        {
            try
            {
                if (ConflictKiller.AnyProcessRunning(def))
                {
                    _log.LogInformation("Startup conflict shutdown: {App} is still running.", def.DisplayName);
                    continue;
                }
                killed.Add(def.DisplayName);
                _log.LogInformation("Startup conflict shutdown: ended {App}.", def.DisplayName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Startup conflict shutdown could not confirm {App}.", def.DisplayName);
            }
        }
        if (killed.Count == 0) return;

        _log.LogInformation("Startup conflict shutdown ended {Count} app(s).", killed.Count);
        try { AppsTerminated?.Invoke(killed); }
        catch (Exception ex) { _log.LogWarning(ex, "Startup conflict shutdown notification failed."); }
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
