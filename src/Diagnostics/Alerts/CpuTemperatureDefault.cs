using System;
using System.Linq;
using Nexus.Service.Cooling;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>The CPU's own maximum temperature and whether it came from a hardware reading (which is final) or the model table (which a later reading may replace).</summary>
public readonly record struct CpuMax(double MaxC, bool FromHardware);

/// <summary>
/// The default upper limit of the built-in CPU temperature entry: the CPU's own maximum operating temperature, so the
/// alert fires at the part's throttle point and not at a flat number that is too high for some chips.
/// </summary>
public static class CpuTemperatureDefault
{
    /// <summary>Used for an unknown CPU, an Intel part, Apple Silicon and AMD mobile parts the table does not list.</summary>
    public const double FallbackC = 100;

    /// <summary>Degrees below the CPU maximum at which the default limit sits. The single place a safety margin is set.</summary>
    public const double MarginC = 0;

    /// <summary>The default limit for a CPU maximum: the margin applied, then pulled into the entry's limit range (an Intel laptop part above the range is capped).</summary>
    public static double LimitFromMax(double maxC)
    {
        var (min, max) = BuiltInSensorRules.LimitRange(BuiltInSensorRules.CpuTemp);
        return Math.Clamp(maxC - MarginC, min, max);
    }

    /// <summary>
    /// A real hardware reading wins. Otherwise a non-AMD part gets <see cref="FallbackC"/>; an AMD part gets the published per-model
    /// value from <see cref="ThermalLimits"/>, else <see cref="FallbackC"/> for a mobile part (their limit is higher than desktop)
    /// and the AMD desktop default for any other Ryzen the table does not list.
    /// </summary>
    public static CpuMax Resolve(string? cpuModel, double? liveTjMaxC)
    {
        var limit = ThermalLimits.ResolveCpu(cpuModel, liveTjMaxC);
        if (limit.Source == ThermalLimitSources.Hardware)
        {
            return new CpuMax(limit.LimitC, true);
        }
        if (!ThermalLimits.IsAmd(cpuModel))
        {
            return new CpuMax(FallbackC, false);
        }
        if (limit.Source == ThermalLimitSources.Spec)
        {
            return new CpuMax(limit.LimitC, false);
        }
        return new CpuMax(IsMobile(cpuModel) ? FallbackC : limit.LimitC, false);
    }

    // Mobile Ryzen model tokens end in H, HS, HX, HX3D or U ("7840HS", "7945HX3D", "5800H", "7735U"); the Ryzen AI line is mobile too.
    // Whole-token tests only, so a desktop "7950X3D" never matches.
    private static bool IsMobile(string? cpuModel)
    {
        var tokens = (cpuModel ?? "").Split(new[] { ' ', '-', '(', ')', ',', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(t => t.Equals("AI", StringComparison.OrdinalIgnoreCase) || IsMobileModelToken(t));
    }

    private static bool IsMobileModelToken(string token)
    {
        var digits = 0;
        while (digits < token.Length && char.IsAsciiDigit(token[digits]))
        {
            digits++;
        }
        if (digits < 4 || digits == token.Length)
        {
            return false;
        }
        var suffix = token[digits..];
        return suffix.Equals("H", StringComparison.OrdinalIgnoreCase)
            || suffix.Equals("HS", StringComparison.OrdinalIgnoreCase)
            || suffix.Equals("HX", StringComparison.OrdinalIgnoreCase)
            || suffix.Equals("HX3D", StringComparison.OrdinalIgnoreCase)
            || suffix.Equals("U", StringComparison.OrdinalIgnoreCase);
    }
}
