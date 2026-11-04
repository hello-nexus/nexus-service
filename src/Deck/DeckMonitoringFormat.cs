using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Deck;

/// <summary>
/// Ports nexus-web's deck monitoring tile text rules (MonitoringWidget.tsx's
/// labelForDevice/sensorNames.ts, sensorValueFormat.ts's
/// formatScaledDataValue/formatSensorValue, and lib/units.ts's
/// convertTemperature/localizeNumbers) so the physical key matches the
/// touch-panel DeckMonitoringCell for the same sensor. Category strings are
/// nexus-web's DeckMonitoringCategory set (deck/types.ts), equal to its
/// monitoring picker's own DEVICE_OPTION_KEYS.
/// </summary>
internal static partial class DeckMonitoringFormat
{
    private static readonly string[] DataUnitLadder = { "B", "KB", "MB", "GB", "TB", "PB" };

    [GeneratedRegex(@"-?\d[\d,]*(?:\.\d+)?")]
    private static partial Regex NumberTokenRegex();

    /// <summary>Categories whose sensor names arrive with the category baked in ("CPU Total"), mirroring sensorNames.ts's DEVICE_PREFIXES.</summary>
    private static string? DevicePrefix(string? category) => category switch
    {
        "cpu" => "CPU",
        "gpu" => "GPU",
        "memory" => "Memory",
        "network" => "Network",
        _ => null,
    };

    /// <summary>
    /// Default top label for a monitoring tile when the slot has no
    /// LabelText override, mirroring labelForDevice: "&lt;Prefix&gt; &lt;Sensor&gt;"
    /// for a prefixed category (normalizing an already-prefixed or bare
    /// sensor name to the same output), the sensor name unchanged for any
    /// other category, or the category's own display name when the sensor
    /// id is unresolved.
    /// </summary>
    internal static string ResolveLabel(string? category, string sensorName)
    {
        if (!string.IsNullOrEmpty(sensorName))
        {
            var prefix = DevicePrefix(category);
            if (prefix is null)
            {
                return sensorName;
            }
            var bare = StripPrefix(sensorName, prefix);
            return bare.Length > 0 ? $"{prefix} {bare}" : prefix;
        }
        // Mirrors labelForDevice's no-sensor fallbacks (MonitoringWidget.tsx).
        return category switch
        {
            "quick" => "Quick",
            "cpu" => "CPU",
            "gpu" => "GPU",
            "memory" => "RAM",
            "memoryModule" => "DIMM",
            "motherboard" => "MB",
            "storage" => "Storage",
            "smart" => "SMART",
            "network" => "Network",
            "fps" => "FPS",
            "battery" => "BATT",
            "cooler" => "COOL",
            "psu" => "PSU",
            "embeddedController" => "EC",
            _ => "",
        };
    }

    /// <summary>Mirrors bareSensorLabel: strips a leading "&lt;prefix&gt; " (case-insensitive), or returns "" for a name equal to the prefix alone.</summary>
    private static string StripPrefix(string name, string prefix)
    {
        if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        if (name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
        {
            return name[(prefix.Length + 1)..];
        }
        return name;
    }

    /// <summary>
    /// Value text for a resolved sensor, mirroring formatSensorValue. A Celsius
    /// temperature converts to Fahrenheit when tempUnit is "f". Types covered by
    /// <see cref="MonitoringDecimals"/> take their decimals from it (wholeNumbers is the
    /// monitoringWholeNumbers preference), swapping the number inside the provider's formatted
    /// string. A byte-based Units reading auto-scales along the B/KB/MB/GB/TB/PB ladder
    /// (mirroring formatScaledDataValue); any other reading keeps the provider's formatted
    /// string. The result is then re-separated to numberFormat, mirroring localizeNumbers.
    /// </summary>
    internal static string ResolveValueText(HardwareSensor sensor, string? tempUnit, string? numberFormat, bool wholeNumbers = false)
    {
        if (string.Equals(tempUnit, "f", StringComparison.OrdinalIgnoreCase) && IsCelsiusUnit(sensor.Units))
        {
            // Converted in double on the web's parsed value, like convertTemperature.
            var fahrenheit = MonitoringDecimals.ToWebDouble(sensor.Value) * 9d / 5d + 32d;
            var number = float.IsFinite(sensor.Value) ? MonitoringDecimals.FormatNumber(fahrenheit, "Temperature", wholeNumbers) : null;
            if (number is not null)
            {
                return LocalizeNumbers($"{number} °F", numberFormat);
            }
        }
        if (FormatScaledDataValue(sensor.Value, sensor.Units) is { } scaled)
        {
            return LocalizeNumbers(scaled, numberFormat);
        }
        return LocalizeNumbers(ApplyDecimals(sensor, wholeNumbers), numberFormat);
    }

    /// <summary>
    /// Swaps the number in the provider's formatted string for the shared per-type rule's
    /// text, keeping the provider's own spacing and unit. Types the rule does not cover, or a
    /// formatted string with no number, pass through unchanged.
    /// </summary>
    private static string ApplyDecimals(HardwareSensor sensor, bool wholeNumbers)
    {
        var number = MonitoringDecimals.FormatNumber(sensor.Value, sensor.Type, wholeNumbers);
        var match = NumberTokenRegex().Match(sensor.Formatted);
        return number is null || !match.Success
            ? sensor.Formatted
            : string.Concat(sensor.Formatted.AsSpan(0, match.Index), number, sensor.Formatted.AsSpan(match.Index + match.Length));
    }

    /// <summary>Mirrors isCelsiusUnit: strips a degree sign and trims before the case-insensitive "C" compare.</summary>
    private static bool IsCelsiusUnit(string units) =>
        string.Equals(units.Replace("°", "").Trim(), "C", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mirrors localizeNumbers's separator swap for the "comma" number format
    /// (group '.', decimal ','). Source strings are already the "dot" style
    /// (group ',', decimal '.'), matching that format as a no-op; "system"
    /// has no OS/browser locale to resolve on the service side (the process
    /// runs InvariantGlobalization), so it falls back to the same untouched
    /// string rather than reading machine culture.
    /// </summary>
    private static string LocalizeNumbers(string display, string? numberFormat)
    {
        if (!string.Equals(numberFormat, "comma", StringComparison.OrdinalIgnoreCase))
        {
            return display;
        }
        return NumberTokenRegex().Replace(display, m =>
        {
            var token = m.Value;
            var dot = token.IndexOf('.');
            var intPart = dot < 0 ? token : token[..dot];
            var frac = dot < 0 ? "" : token[(dot + 1)..];
            var newInt = intPart.Replace(',', '.');
            return frac.Length > 0 ? $"{newInt},{frac}" : newInt;
        });
    }

    private static string? FormatScaledDataValue(float value, string units)
    {
        if (!float.IsFinite(value))
        {
            return null;
        }
        var unitIndex = Array.IndexOf(DataUnitLadder, (units ?? "").ToUpperInvariant());
        if (unitIndex < 0)
        {
            return null;
        }

        var scaled = Math.Max(0f, value);
        var index = unitIndex;
        while (scaled >= 1024f && index < DataUnitLadder.Length - 1)
        {
            scaled /= 1024f;
            index++;
        }
        while (scaled > 0f && scaled < 1f && index > 0)
        {
            scaled *= 1024f;
            index--;
        }

        return scaled == MathF.Floor(scaled)
            ? $"{scaled:F0} {DataUnitLadder[index]}"
            : $"{scaled:F1} {DataUnitLadder[index]}";
    }
}
