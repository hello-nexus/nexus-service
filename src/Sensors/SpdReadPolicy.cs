using System;
using System.Threading.Tasks;

namespace Nexus.Service.Sensors;

/// <summary>Resets every DIMM SPD hub to page 0 so a reboot never starts with the hub on a non-zero page.</summary>
public interface ISpdPageReset
{
    /// <summary>Synchronous and bounded by <paramref name="budget"/>; never throws. Returns the DIMM count reset, 0 when SPD reading was never active.</summary>
    int ResetSpdPages(TimeSpan budget);
}

/// <summary>The outcome of comparing the wanted SPD state with the applied one.</summary>
public readonly record struct SpdDecision(bool Wanted, bool Changed, string Reason);

/// <summary>Platform-neutral SPD read decisions, kept apart from LhmComputer so they are testable off Windows.</summary>
public static class SpdReadPolicy
{
    /// <summary>SPD reads run only while the memory device's Nexus Control gate is on AND the monitoring setting allows them.</summary>
    public static bool ReadingEnabled(bool gateEnabled, bool readRamTemperatures) => gateEnabled && readRamTemperatures;

    public static SpdDecision Decide(bool gateEnabled, bool readRamTemperatures, bool currentlyActive)
    {
        var wanted = ReadingEnabled(gateEnabled, readRamTemperatures);
        var reason = (gateEnabled, readRamTemperatures) switch
        {
            (true, true) => "Nexus Control for the memory device and the monitoring RAM temperature setting are on",
            (false, true) => "Nexus Control for the memory device is off",
            (true, false) => "the monitoring RAM temperature setting is off",
            _ => "Nexus Control for the memory device and the monitoring RAM temperature setting are off",
        };
        return new SpdDecision(wanted, wanted != currentlyActive, reason);
    }
}

/// <summary>Runs an SPD page reset as a no-op when inactive, within a time budget, and without ever throwing.</summary>
public static class SpdPageResetRunner
{
    public static int Run(bool active, Func<int> reset, TimeSpan budget, Action<string> log)
    {
        if (!active)
        {
            return 0;
        }
        try
        {
            var task = Task.Factory.StartNew(reset, default, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!task.Wait(budget))
            {
                log($"[lhm] DIMM SPD page reset timed out after {(int)budget.TotalMilliseconds}ms");
                return 0;
            }
            log($"[lhm] DIMM SPD page reset to 0 on {task.Result} DIMM(s)");
            return task.Result;
        }
        catch (Exception ex)
        {
            log($"[lhm] DIMM SPD page reset failed: {(ex is AggregateException a && a.InnerException is not null ? a.InnerException.Message : ex.Message)}");
            return 0;
        }
    }
}
