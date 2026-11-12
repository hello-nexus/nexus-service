using System;
using Nexus.Service.Cooling;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>
/// The default upper limit of the built-in CPU temperature entry: the CPU's own maximum operating temperature, so the
/// alert fires at the part's throttle point and not at a flat number that is too high for some chips (the X3D parts
/// throttle well below 100).
/// </summary>
public static class CpuTemperatureDefault
{
    /// <summary>Used when the CPU is unknown, an Intel part (their package limit is 100 on desktop) or Apple Silicon.</summary>
    public const double FallbackC = 100;

    // Ryzen 5 7600X3D is not in the cooling table; AMD publishes 89 for it, the same as the other 7000 X3D parts.
    private const string Ryzen7600X3D = "7600X3D";
    private const double Ryzen7600X3DC = 89;

    /// <summary>
    /// A real reading from the hardware (Intel Tjmax from the per-core distance sensors, hwmon on Linux) wins. Otherwise the
    /// AMD per-model published value from <see cref="ThermalLimits"/>, then 95 for a Ryzen not listed there. Everything else is
    /// <see cref="FallbackC"/>. The result is always inside the entry's limit range, so an Intel laptop part reporting 105 gives 100.
    /// </summary>
    public static double Resolve(string? cpuModel, double? liveTjMaxC)
    {
        var limit = ThermalLimits.ResolveCpu(cpuModel, liveTjMaxC);
        var value = limit.LimitC;
        if (limit.Source == ThermalLimitSources.Default && value == ThermalLimits.GenericDefaultC)
        {
            // The cooling resolver's generic default is its own conservative guard value, not a spec; non-AMD parts keep the alert default.
            value = FallbackC;
        }
        else if (limit.Source != ThermalLimitSources.Hardware
            && cpuModel is not null
            && cpuModel.Contains(Ryzen7600X3D, StringComparison.OrdinalIgnoreCase))
        {
            value = Ryzen7600X3DC;
        }
        var (min, max) = BuiltInSensorRules.LimitRange(BuiltInSensorRules.CpuTemp);
        return Math.Clamp(value, min, max);
    }
}
