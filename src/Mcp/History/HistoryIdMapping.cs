using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;

namespace Nexus.Service.Mcp.History;

/// <summary>
/// Maps a raw ISensorProvider sensor id (get_sensors' Id field) to the
/// MonitoringSensorHistoryReader id it feeds, reusing
/// SystemMetricsSource.FindSensor so get_sensors' historyId field and a raw
/// id passed to query_sensor_history never drift from what SystemMetricsSource
/// actually samples. Covers only cpu.temp, gpu.&lt;id&gt;.load, gpu.&lt;id&gt;.temp,
/// and temp.ram:&lt;index&gt; - the series SystemMetricsSource derives straight
/// from an ISensorProvider sensor list. fan.&lt;id&gt;.rpm/duty (IFanControlProvider)
/// and temp.storage:&lt;serial&gt; (ISmartHealthSource, keyed by drive serial) come
/// from separate providers whose ids do not correspond to any ISensorProvider
/// raw sensor id, so this deliberately has no entry for them.
/// </summary>
public static class HistoryIdMapping
{
    /// <summary>Raw id of the sensor SystemMetricsSource samples as cpu.temp, or
    /// null when cpuSensors has no Temperature reading at all.</summary>
    public static string? CpuTempSensorId(IReadOnlyList<HardwareSensor> cpuSensors) =>
        SystemMetricsSource.FindSensor(cpuSensors, "Temperature", "Package")?.Id;

    /// <summary>Raw sensor ids SystemMetricsSource samples for one GPU's load and
    /// temperature, keyed by the history id each feeds. gpuId is already
    /// MetricsHistory.SanitizeId-ed, matching the history vocabulary.</summary>
    public static IReadOnlyDictionary<string, string> GpuSensorIds(GpuReadout gpu)
    {
        var gpuId = MetricsHistory.SanitizeId(gpu.Id);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (SystemMetricsSource.FindSensor(gpu.Sensors, "Load", "Core") is { } load)
        {
            map[load.Id] = $"gpu.{gpuId}.load";
        }
        if (SystemMetricsSource.FindSensor(gpu.Sensors, "Temperature", "Core") is { } temp)
        {
            map[temp.Id] = $"gpu.{gpuId}.temp";
        }
        return map;
    }

    /// <summary>Raw sensor ids SystemMetricsSource samples as RAM temperature,
    /// keyed by the "temp.ram:&lt;index&gt;" history id each feeds. Reuses
    /// SystemMetricsSource.FindRamTempSensors so the index always matches
    /// ReadComponentTemps' encounter order.</summary>
    public static IReadOnlyDictionary<string, string> RamSensorIds(IReadOnlyList<HardwareSensor> memorySensors)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var ramSensors = SystemMetricsSource.FindRamTempSensors(memorySensors);
        for (var i = 0; i < ramSensors.Count; i++)
        {
            map[ramSensors[i].Id] = $"temp.ram:{i}";
        }
        return map;
    }
}
