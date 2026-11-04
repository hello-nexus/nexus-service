using System;
using System.Globalization;

namespace Nexus.Service.Sensors;

/// <summary>
/// The one per-type decimals rule shared by every service-side surface that
/// renders a sensor value as text (deck keys, device LCDs). Types are the
/// <see cref="Nexus.Service.Models.Sensors.HardwareSensor.Type"/> strings.
/// With <c>wholeNumbers</c> on, the types a user reads as a rough level round
/// to integers; precision-bearing types (voltage, current, frame time) keep
/// theirs.
/// </summary>
public static class MonitoringDecimals
{
    /// <summary>Decimals for a governed type, or null when the type is not covered by this rule.</summary>
    public static int? For(string? sensorType, bool wholeNumbers) => sensorType switch
    {
        "Temperature" or "Power" or "Load" or "Control" or "Level" or "Flow" or "Noise" or "Frequency" => wholeNumbers ? 0 : 1,
        "Voltage" or "Current" => 3,
        "FrameTime" => 1,
        "Clock" or "Fan" or "Framerate" => 0,
        _ => null,
    };

    /// <summary>True when <paramref name="sensorType"/> follows this rule.</summary>
    public static bool Governs(string? sensorType) => For(sensorType, false) is not null;

    /// <summary>
    /// The number text for <paramref name="value"/> at its type's decimals, rounded half away from zero,
    /// or null for an ungoverned type or a non-finite value.
    /// </summary>
    public static string? FormatNumber(float value, string? sensorType, bool wholeNumbers)
    {
        if (For(sensorType, wholeNumbers) is not { } decimals || !float.IsFinite(value))
        {
            return null;
        }
        var rounded = Math.Round((double)value, decimals, MidpointRounding.AwayFromZero);
        // A small negative rounds to negative zero, which would print as "-0".
        if (rounded == 0d)
        {
            rounded = 0d;
        }
        return rounded.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
