using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>Builds the screen's reading from Nexus's own sensors.</summary>
public static class MystiqueSensors
{
    public static MystiqueReading Read(
        ISensorProvider sensors, DeepCoolMystiqueSettings settings, IReadOnlyDictionary<string, string>? fanNames = null) => new(
        TempC: Round(SummarySensors.Value(sensors, SummarySensorKind.CpuTemp)),
        UsagePct: Round(SummarySensors.Value(sensors, SummarySensorKind.CpuUsage)),
        CpuMhz: Round(SummarySensors.Value(sensors, SummarySensorKind.CpuClock)),
        FanRpm: Round(FindFan(sensors, settings.CpuFan, pump: false, fanNames)?.Value),
        PumpRpm: Round(FindFan(sensors, settings.Pump, pump: true, fanNames)?.Value));

    /// <summary>
    /// The picked sensor, or with none picked the motherboard header named for the CPU fan
    /// (or for the pump, which sits on the CPU_OPT or a pump header). Null when nothing matches,
    /// so a missing sensor reads 0 rather than another header's speed. Headers the user renamed on
    /// the cooling page match by their new name.
    /// </summary>
    public static HardwareSensor? FindFan(
        ISensorProvider sensors, DeepCoolMystiqueSensorPick pick, bool pump, IReadOnlyDictionary<string, string>? fanNames = null)
    {
        if (!string.IsNullOrEmpty(pick.Sensor))
        {
            var picked = SensorSnapshotResolver.ResolveExact(
                sensors, pick.Category, pick.Sensor, new SensorSnapshotSources(null, null, null, fanNames));
            return picked is { Type: "Fan" } ? picked : null;
        }
        foreach (var sensor in FanSensorNames.WithRenames(sensors.GetMotherboardSensors(), fanNames))
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

    /// <summary>The cooling page's header renames keyed by tach sensor id, or null when none apply.</summary>
    public static IReadOnlyDictionary<string, string>? FanNames(IConfigStore store, Nexus.Service.Cooling.IFanControlProvider? fans)
    {
        if (fans is null)
        {
            return null;
        }
        Dictionary<string, string>? names = null;
        store.Read(s => names = new Dictionary<string, string>(s.Cooling.FanNames));
        if (names is null || names.Count == 0)
        {
            return null;
        }
        try
        {
            return FanSensorNames.BuildMap(fans.GetFanChannels(), names);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[deepcool-mystique] fan header names skipped: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool Mentions(string name, string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Non-finite guard: a hardware sensor can report NaN or Infinity.</summary>
    private static int Round(float? v) => v is { } f && float.IsFinite(f) ? (int)MathF.Round(f) : 0;
}
