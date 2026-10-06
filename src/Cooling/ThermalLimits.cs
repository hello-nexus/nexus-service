using System;
using System.Collections.Generic;

namespace Nexus.Service.Cooling;

/// <summary>Where a thermal limit came from. Priority: hardware, then spec, then default.</summary>
public static class ThermalLimitSources
{
    public const string Hardware = "hardware";
    public const string Spec = "spec";
    public const string Default = "default";
}

public readonly record struct ThermalLimit(double LimitC, string Source);

/// <summary>
/// Pure resolution of the temperature the guard defends. The part's own reported
/// limit wins; AMD (whose limit no current sensor path exposes) falls back to the
/// published per-model Tjmax, then to a conservative default.
/// </summary>
public static class ThermalLimits
{
    public const double AmdDefaultC = 95;
    public const double GenericDefaultC = 90;

    // A hardware-reported limit outside this band is a bad read, not a limit.
    private const double MinPlausibleLimitC = 60;
    private const double MaxPlausibleLimitC = 125;

    // Published "Max. Operating Temperature (Tjmax)" per AMD model, substring-matched
    // against the CPU name. Source: the product spec page of each model under
    // https://www.amd.com/en/products/processors/desktops/ryzen.html.
    // Add an entry only after reading it off that page. Models absent here fall back
    // to AmdDefaultC.
    // TODO: an SMU / PM-table reader would be the future hardware source for AMD.
    private static readonly (string Match, double TjMaxC)[] AmdSpec =
    {
        ("9800X3D", 95),
    };

    /// <summary>Limit for a CPU given its model string and, when readable, its reported Tjmax.</summary>
    public static ThermalLimit ResolveCpu(string? cpuModel, double? hardwareTjMaxC)
    {
        if (hardwareTjMaxC is { } hw && double.IsFinite(hw) && hw >= MinPlausibleLimitC && hw <= MaxPlausibleLimitC)
        {
            return new ThermalLimit(hw, ThermalLimitSources.Hardware);
        }

        var model = cpuModel ?? "";
        var isAmd = model.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || model.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
            || model.Contains("Threadripper", StringComparison.OrdinalIgnoreCase)
            || model.Contains("EPYC", StringComparison.OrdinalIgnoreCase);
        if (!isAmd)
        {
            return new ThermalLimit(GenericDefaultC, ThermalLimitSources.Default);
        }

        foreach (var (match, tjMax) in AmdSpec)
        {
            if (model.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                return new ThermalLimit(tjMax, ThermalLimitSources.Spec);
            }
        }
        return new ThermalLimit(AmdDefaultC, ThermalLimitSources.Default);
    }

    /// <summary>Guard temperature: the hottest plausible CPU reading. Null when none is usable.</summary>
    public static double? MaxPlausible(IEnumerable<double> readings)
    {
        double? max = null;
        foreach (var r in readings)
        {
            if (!double.IsFinite(r) || r <= 0 || r > 150)
            {
                continue;
            }
            if (max is null || r > max)
            {
                max = r;
            }
        }
        return max;
    }
}
