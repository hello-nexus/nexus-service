using System;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>Builds the screen's reading from Nexus's own sensors.</summary>
public static class MystiqueSensors
{
    public static MystiqueReading Read(ISensorProvider sensors, DeepCoolMystiqueSettings settings) => new(
        TempC: Round(SummarySensors.Value(sensors, SummarySensorKind.CpuTemp)),
        UsagePct: Round(SummarySensors.Value(sensors, SummarySensorKind.CpuUsage)),
        CpuMhz: Round(SummarySensors.Value(sensors, SummarySensorKind.CpuClock)),
        FanRpm: Round(FindFan(sensors, settings.CpuFan, pump: false)?.Value),
        PumpRpm: Round(FindFan(sensors, settings.Pump, pump: true)?.Value));

    /// <summary>
    /// The picked sensor, or with none picked the motherboard header named for the CPU fan
    /// (or for the pump, which sits on the CPU_OPT or a pump header). Null when nothing matches,
    /// so a missing sensor reads 0 rather than another header's speed.
    /// </summary>
    public static HardwareSensor? FindFan(ISensorProvider sensors, DeepCoolMystiqueSensorPick pick, bool pump)
    {
        if (!string.IsNullOrEmpty(pick.Sensor))
        {
            return SensorSnapshotResolver.ResolveExact(sensors, pick.Category, pick.Sensor);
        }
        foreach (var sensor in sensors.GetMotherboardSensors())
        {
            if (sensor.Type != "Fan")
            {
                continue;
            }
            bool pumpHeader = Mentions(sensor.Name, "OPT") || Mentions(sensor.Name, "PUMP") || Mentions(sensor.Name, "AIO");
            if (pump ? pumpHeader : Mentions(sensor.Name, "CPU") && !pumpHeader)
            {
                return sensor;
            }
        }
        return null;
    }

    private static bool Mentions(string name, string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Non-finite guard: a hardware sensor can report NaN or Infinity.</summary>
    private static int Round(float? v) => v is { } f && float.IsFinite(f) ? (int)MathF.Round(f) : 0;
}
