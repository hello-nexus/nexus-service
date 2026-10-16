using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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

/// <summary>One SMBus as the page reset sees it.</summary>
public interface ISpdBus
{
    /// <summary>Negative on a failed transaction.</summary>
    int ReadByte(byte address, byte register);
    /// <summary>Writes MR11 page 0 when the hub is off it; true when the DDR5 hub answered.</summary>
    bool ResetDdr5Page(byte address);
    /// <summary>The broadcast SPA0 write that selects DDR4 page 0 on every module of the bus.</summary>
    void SelectDdr4Page0();
}

public static class SpdPageReset
{
    private const byte Ddr5Id0 = 0x51;
    private const byte Ddr5Id1 = 0x18;

    /// <summary>Touches only the given detected addresses; MR0/MR1 are unpaged so they identify DDR5 before any write. A failed read skips the address; at most one SPA0 write per bus, and only for a bus with a non-DDR5 responder.</summary>
    public static int Run(IReadOnlyList<ISpdBus> buses, IReadOnlyCollection<byte> addresses, Func<bool> expired)
    {
        var reset = 0;
        foreach (var bus in buses)
        {
            var ddr4Seen = false;
            foreach (var address in addresses)
            {
                if (expired())
                {
                    return reset;
                }
                var id0 = bus.ReadByte(address, 0);
                if (id0 < 0 || expired())
                {
                    continue;
                }
                var id1 = bus.ReadByte(address, 1);
                if (id1 < 0)
                {
                    continue;
                }
                if (id0 == Ddr5Id0 && id1 == Ddr5Id1)
                {
                    if (bus.ResetDdr5Page(address))
                    {
                        reset++;
                    }
                }
                else
                {
                    ddr4Seen = true;
                    reset++;
                }
            }
            if (ddr4Seen && !expired())
            {
                bus.SelectDdr4Page0();
            }
        }
        return reset;
    }
}

/// <summary>Runs an SPD page reset as a no-op when inactive, within a time budget, and without ever throwing.</summary>
public static class SpdPageResetRunner
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);

    /// <summary>The reset gets an expiry probe it must poll between bus transactions; past the budget the probe turns true so an abandoned thread stops touching the bus.</summary>
    public static int Run(bool active, Func<Func<bool>, int> reset, TimeSpan budget, Action<string> log)
    {
        if (!active)
        {
            return 0;
        }
        try
        {
            var clock = Stopwatch.StartNew();
            var abandoned = false;
            bool Expired() => Volatile.Read(ref abandoned) || clock.Elapsed >= budget;
            var task = Task.Factory.StartNew(() => reset(Expired), default, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (!task.Wait(budget))
            {
                Volatile.Write(ref abandoned, true);
                task.Wait(Grace);
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
