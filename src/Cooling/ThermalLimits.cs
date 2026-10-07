using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Cooling;

/// <summary>Where a thermal limit came from. Priority: hardware, then spec, then default.</summary>
public static class ThermalLimitSources
{
    public const string Hardware = "hardware";
    public const string Spec = "spec";
    public const string Default = "default";
    /// <summary>The user's own limit, replacing a spec or default one.</summary>
    public const string User = "user";
}

public readonly record struct ThermalLimit(double LimitC, string Source);

/// <summary>
/// Pure resolution of the temperature the guard defends. The part's own reported
/// limit wins; AMD (whose limit no current sensor path exposes) falls back to the
/// published per-model Tjmax, then to a conservative default.
/// </summary>
public static class ThermalLimits
{
    /// <summary>Range of a user-set CPU limit.</summary>
    public const double UserMinC = 90;
    public const double UserMaxC = 110;

    /// <summary>
    /// A user limit clamped to the range, widened to include the detected limit so a part whose own
    /// limit lies outside it (a lower one, say) can still sit at that value.
    /// </summary>
    public static double ClampUser(double requestedC, double detectedC) =>
        Math.Clamp(requestedC, Math.Min(UserMinC, detectedC), Math.Max(UserMaxC, detectedC));
    public const double AmdDefaultC = 95;
    public const double GenericDefaultC = 90;

    // A hardware-reported limit outside this band is a bad read, not a limit.
    private const double MinPlausibleLimitC = 60;
    private const double MaxPlausibleLimitC = 125;

    // Published "Max. Operating Temperature (Tjmax)" per AMD model, matched as a whole
    // token of the CPU name. Each entry cites its product page on amd.com; add an entry
    // only after reading it off that page. Models absent here fall back to AmdDefaultC.
    // TODO: an SMU / PM-table reader would be the future hardware source for AMD.
    private static readonly (string Match, double TjMaxC)[] AmdSpec =
    {
        ("9950X3D", 95), // https://www.amd.com/en/products/processors/desktops/ryzen/9000-series/amd-ryzen-9-9950x3d.html
        ("9900X3D", 95), // https://www.amd.com/en/products/processors/desktops/ryzen/9000-series/amd-ryzen-9-9900x3d.html
        ("9800X3D", 95), // https://www.amd.com/en/products/processors/desktops/ryzen/9000-series/amd-ryzen-7-9800x3d.html
        ("7950X3D", 89), // https://www.amd.com/en/products/processors/desktops/ryzen/7000-series/amd-ryzen-9-7950x3d.html
        ("7900X3D", 89), // https://www.amd.com/en/products/processors/desktops/ryzen/7000-series/amd-ryzen-9-7900x3d.html
        ("7800X3D", 89), // https://www.amd.com/en/products/processors/desktops/ryzen/7000-series/amd-ryzen-7-7800x3d.html
        ("5800X3D", 90), // https://www.amd.com/en/products/processors/desktops/ryzen/5000-series/amd-ryzen-7-5800x3d.html
        ("5700X3D", 90), // https://www.amd.com/en/products/processors/desktops/ryzen/5000-series/amd-ryzen-7-5700x3d.html
        ("5800X", 90),   // https://www.amd.com/en/products/processors/desktops/ryzen/5000-series/amd-ryzen-7-5800x.html
        ("5700X", 90),   // https://www.amd.com/en/products/processors/desktops/ryzen/5000-series/amd-ryzen-7-5700x.html
        ("5950X", 90),   // https://www.amd.com/en/products/processors/desktops/ryzen/5000-series/amd-ryzen-9-5950x.html
        ("5900X", 90),   // https://www.amd.com/en/products/processors/desktops/ryzen/5000-series/amd-ryzen-9-5900x.html
        ("5600X", 95),   // https://www.amd.com/en/products/processors/desktops/ryzen/5000-series/amd-ryzen-5-5600x.html
    };

    /// <summary>
    /// Intel Tjmax from per-core "Distance to TjMax" sensors: core temperature plus that
    /// core's distance, same core name, highest wins. Null when no pair is usable.
    /// </summary>
    public static double? TjMaxFromCoreDistances(IEnumerable<(string Name, double Value)> temperatures)
    {
        const string suffix = " Distance to TjMax";
        var list = temperatures.ToList();
        double? best = null;
        foreach (var (name, distance) in list)
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var core = name[..^suffix.Length];
            foreach (var (otherName, temp) in list)
            {
                if (!otherName.Equals(core, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var tj = temp + distance;
                if (double.IsFinite(tj) && tj >= MinPlausibleLimitC && tj <= MaxPlausibleLimitC && (best is null || tj > best))
                {
                    best = tj;
                }
            }
        }
        return best;
    }

    /// <summary>True for the sensors that report a distance, not a temperature the guard may read.</summary>
    public static bool IsDistanceToTjMax(string sensorName) =>
        sensorName.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase);

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

        // Exact model token, so 5900X does not match 5900XT.
        var tokens = model.Split(new[] { ' ', '-', '(', ')', ',', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var (match, tjMax) in AmdSpec)
        {
            if (tokens.Any(t => t.Equals(match, StringComparison.OrdinalIgnoreCase)))
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
