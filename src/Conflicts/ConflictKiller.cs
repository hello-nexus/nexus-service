using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nexus.Service.Activity;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Terminates a competing app named by a <see cref="ConflictAppDefinition"/>.
/// Shared by the "End task" endpoint and the opt-in startup shutdown so both
/// paths use the same vetted process/service names - a caller never supplies
/// a process name, only a catalog id.
/// </summary>
public static class ConflictKiller
{
    /// <summary>Outcome of a kill attempt. <paramref name="Killed"/> is measured, not inferred: the app was running before and is not running after.</summary>
    public readonly record struct KillOutcome(bool RunningBefore, bool RunningAfter)
    {
        public bool Killed => RunningBefore && !RunningAfter;
    }

    public static KillOutcome Kill(ConflictAppDefinition def) => Kill(def, ConflictSignerGate.Default);

    internal static KillOutcome Kill(ConflictAppDefinition def, ConflictSignerGate gate)
    {
        var runningBefore = AnyProcessRunning(def, gate);

        // Services first: these entries exist because the service restarts
        // the app's processes, so killing those first lets a live service
        // relaunch them inside ProcessKiller's exit wait. Stopping first
        // also means the process pass reaps a host still in STOP_PENDING.
        if (OperatingSystem.IsWindows())
        {
            foreach (var svc in def.WindowsServiceNames)
            {
                WindowsServiceController.StopService(svc);
            }
        }

        foreach (var name in def.ProcessNames)
        {
            if (def.Signers.Length == 0)
            {
                ProcessKiller.Kill(name);
                continue;
            }

            // Signer-pinned: only the processes whose exe identity matches.
            var ours = new List<Process>();
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    if (gate.Matches(def, p.Id))
                    {
                        ours.Add(p);
                    }
                    else
                    {
                        p.Dispose();
                    }
                }
            }
            catch { /* an unreadable process list kills nothing */ }
            ProcessKiller.KillAllCounted(ours);
        }

        return new KillOutcome(runningBefore, AnyProcessRunning(def, gate));
    }

    /// <summary>Whether any process this catalog entry names is running; the oracle for whether a kill did anything.</summary>
    public static bool AnyProcessRunning(ConflictAppDefinition def) => AnyProcessRunning(def, ConflictSignerGate.Default);

    internal static bool AnyProcessRunning(ConflictAppDefinition def, ConflictSignerGate gate)
    {
        foreach (var name in def.ProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                var any = false;
                foreach (var p in procs)
                {
                    if (!any && gate.Matches(def, p.Id)) any = true;
                    p.Dispose();
                }
                if (any) return true;
            }
            catch { /* an unreadable process list is not evidence of absence */ }
        }
        return false;
    }
}
