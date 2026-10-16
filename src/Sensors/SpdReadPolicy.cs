using System;

namespace Nexus.Service.Sensors;

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
