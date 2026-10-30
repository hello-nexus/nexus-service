using System.Collections.Generic;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// Builds <see cref="HardwareSensor"/> rows from a parsed Astral 12VHPWR
/// readout. Formatting matches LibreHardwareSensorProvider's FormatValue for
/// Voltage/Current/Power so these sensors render identically to LHM-sourced
/// ones on the client.
/// </summary>
internal static class AstralSensorBuilder
{
    public static void Append(
        string hwId, string hwName, AstralTelemetryParser.AstralReadout readout, List<HardwareSensor> result)
    {
        var parent = new SensorParent { Id = hwId, Name = hwName };

        for (var i = 0; i < readout.Pins.Length; i++)
        {
            var pin = readout.Pins[i];
            var pinNumber = i + 1;
            result.Add(VoltageSensor($"{hwId}/astral/pin{pinNumber}/voltage", $"GPU 12VHPWR Pin {pinNumber} Voltage", pin.VoltageVolts, parent));
            result.Add(CurrentSensor($"{hwId}/astral/pin{pinNumber}/current", $"GPU 12VHPWR Pin {pinNumber} Current", pin.CurrentAmps, parent));
            result.Add(PowerSensor($"{hwId}/astral/pin{pinNumber}/power", $"GPU 12VHPWR Pin {pinNumber} Power", pin.PowerWatts, parent));
        }

        result.Add(CurrentSensor($"{hwId}/astral/connector/current", "GPU 12VHPWR Connector Current", readout.ConnectorCurrentAmps, parent));
        result.Add(PowerSensor($"{hwId}/astral/connector/power", "GPU 12VHPWR Connector Power", readout.ConnectorPowerWatts, parent));
    }

    public static void AppendLevel(string hwId, string hwName, float x, float y, float sag, float lean, List<HardwareSensor> result)
    {
        var parent = new SensorParent { Id = hwId, Name = hwName };
        result.Add(AngleSensor($"{hwId}/astral/level/x", "GPU Level X", x, parent));
        result.Add(AngleSensor($"{hwId}/astral/level/y", "GPU Level Y", y, parent));
        result.Add(AngleSensor($"{hwId}/astral/level/sag", "GPU Level Sag", sag, parent));
        result.Add(AngleSensor($"{hwId}/astral/level/lean", "GPU Level Lean", lean, parent));
    }

    private static HardwareSensor AngleSensor(string id, string name, float value, SensorParent parent) => new()
    {
        Id = id,
        Name = name,
        Type = "Angle",
        Units = "°",
        Value = value,
        Formatted = $"{value:F1} °",
        Parent = parent,
    };

    private static HardwareSensor VoltageSensor(string id, string name, float value, SensorParent parent) => new()
    {
        Id = id,
        Name = name,
        Type = "Voltage",
        Units = "V",
        Value = value,
        Formatted = $"{value:F3} V",
        Parent = parent,
    };

    private static HardwareSensor CurrentSensor(string id, string name, float value, SensorParent parent) => new()
    {
        Id = id,
        Name = name,
        Type = "Current",
        Units = "A",
        Value = value,
        Formatted = $"{value:F3} A",
        Parent = parent,
    };

    private static HardwareSensor PowerSensor(string id, string name, float value, SensorParent parent) => new()
    {
        Id = id,
        Name = name,
        Type = "Power",
        Units = "W",
        Value = value,
        Formatted = $"{value:F1} W",
        Parent = parent,
    };
}
