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
        if (def.IsSetting) return TurnOffSetting(def);
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

            var ours = new List<Process>();
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in found)
            {
                var keep = false;
                // Holding the handle keeps the pid from being reused between the identity read and the kill.
                // Full access can be refused where terminate is allowed, so a refusal falls back to the pid.
                try { _ = p.SafeHandle; }
                catch { }
                try { keep = gate.Matches(def, p.Id); }
                catch { }
                if (keep)
                {
                    ours.Add(p);
                }
                else
                {
                    p.Dispose();
                }
            }
            ProcessKiller.KillAllCounted(ours);
        }

        return new KillOutcome(runningBefore, AnyProcessRunning(def, gate));
    }

    /// <summary>Whether any process this catalog entry names is running (for a setting, whether it is still active); the oracle for whether a kill did anything.</summary>
    public static bool AnyProcessRunning(ConflictAppDefinition def) => AnyProcessRunning(def, ConflictSignerGate.Default);

    /// <summary>The setting entries: "running" is the setting being active, "ending" is writing it off and trusting only the re-read.</summary>
    private static KillOutcome TurnOffSetting(ConflictAppDefinition def)
    {
        if (!string.Equals(def.Id, WindowsDynamicLighting.ConflictId, StringComparison.OrdinalIgnoreCase)
            || !WindowsDynamicLighting.IsSupported())
        {
            return new KillOutcome(false, false);
        }
        var before = WindowsDynamicLighting.IsActiveConflict(WindowsDynamicLighting.Read());
        // Only Available and Enabled matter after the write, so it skips the second HID walk.
        var after = WindowsDynamicLighting.Write(enabled: false, countDevices: false);
        // An unreadable key after the write proves nothing, so it counts as still on.
        return new KillOutcome(before, after.Enabled || !after.Available);
    }

    internal static bool AnyProcessRunning(ConflictAppDefinition def, ConflictSignerGate gate)
    {
        if (def.IsSetting)
        {
            return string.Equals(def.Id, WindowsDynamicLighting.ConflictId, StringComparison.OrdinalIgnoreCase)
                && WindowsDynamicLighting.IsSupported()
                && WindowsDynamicLighting.IsActiveConflict(WindowsDynamicLighting.Read());
        }
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
