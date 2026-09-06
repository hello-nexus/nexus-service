using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Production IMetricsSource: CPU/memory from IPerformanceProvider, network
/// byte rates from NetworkRateReader, disk byte rates from DiskRateReader,
/// CPU temperature and per-GPU load/temperature via FindSensor over
/// ISensorProvider.GetCpuSensors/GetGpus (also the picker
/// Nexus.Service.Mcp.History.HistoryIdMapping reuses for get_sensors'
/// historyId field), per-channel fan RPM/duty from
/// IFanControlProvider.GetFanChannels, and per-drive/per-DIMM temperature
/// from ISmartHealthSource / ISensorProvider.GetMemorySensors - the same
/// always-on channel state CurveEngine reads at its own 1Hz tick (fans) or
/// that diagnostics already polls on its own cache (SMART), so this adds no
/// new hardware polling cadence.
///
/// Each source below is independently try/caught so one failing read (e.g.
/// a GPU driver hiccup) nulls only its own MetricSample fields rather than
/// dropping the whole tick.
/// </summary>
public sealed class SystemMetricsSource : IMetricsSource
{
    private readonly IPerformanceProvider _performance;
    private readonly ISensorProvider _sensors;
    private readonly IFanControlProvider _fans;
    private readonly NetworkRateReader _network;
    private readonly DiskRateReader _disk;
    private readonly ISmartHealthSource _smart;

    private string? _cpuName;

    public SystemMetricsSource(
        IPerformanceProvider performance, ISensorProvider sensors, IFanControlProvider fans,
        NetworkRateReader network, DiskRateReader disk, ISmartHealthSource smart)
    {
        _performance = performance;
        _sensors = sensors;
        _fans = fans;
        _network = network;
        _disk = disk;
        _smart = smart;
    }

    public async Task<MetricSample> SampleAsync(long tsSec, CancellationToken ct)
    {
        double? cpu = null;
        double? memory = null;
        try
        {
            var perf = await _performance.SampleAsync(ct).ConfigureAwait(false);
            cpu = perf.Cpu;
            memory = perf.Memory;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] performance read failed: {ex.Message}");
        }

        double? netIn = null;
        double? netOut = null;
        try
        {
            var rate = _network.Read();
            netIn = rate.InBytesPerSec;
            netOut = rate.OutBytesPerSec;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] network read failed: {ex.Message}");
        }

        double? diskRead = null;
        double? diskWrite = null;
        try
        {
            var diskRate = _disk.Read();
            diskRead = diskRate.ReadBytesPerSec;
            diskWrite = diskRate.WriteBytesPerSec;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] disk read failed: {ex.Message}");
        }

        double? cpuTemp = null;
        try
        {
            cpuTemp = FindSensor(_sensors.GetCpuSensors(), "Temperature", "Package")?.Value;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] cpu temp read failed: {ex.Message}");
        }

        IReadOnlyList<GpuReading> gpus = Array.Empty<GpuReading>();
        try
        {
            gpus = ReadGpus();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] gpu read failed: {ex.Message}");
        }

        IReadOnlyList<FanReading> fans = Array.Empty<FanReading>();
        try
        {
            fans = ReadFans();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] fan read failed: {ex.Message}");
        }

        IReadOnlyList<ComponentTempReading> componentTemps = Array.Empty<ComponentTempReading>();
        try
        {
            componentTemps = ReadComponentTemps();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-source] component temp read failed: {ex.Message}");
        }

        return new MetricSample(
            tsSec, cpu, memory, netIn, netOut, cpuTemp, gpus, fans, ResolveCpuName(), componentTemps,
            diskRead, diskWrite);
    }

    // Resolved once and kept for the process lifetime: the CPU model never
    // changes without a reboot.
    private string ResolveCpuName()
    {
        if (_cpuName is not null)
        {
            return _cpuName;
        }
        try
        {
            var model = _sensors.GetCpuModel();
            _cpuName = string.IsNullOrWhiteSpace(model) ? "CPU" : model;
        }
        catch
        {
            _cpuName = "CPU";
        }
        return _cpuName;
    }

    // Storage temperature via SmartHealthMonitor's own 10-minute cache (cheap
    // to call every tick); RAM temperature via ISensorProvider.GetMemorySensors,
    // filtered to sensors whose name mentions dimm/memory - most boards expose
    // no memory-class temperature sensor at all, so an empty result here is
    // normal.
    private IReadOnlyList<ComponentTempReading> ReadComponentTemps()
    {
        var result = new List<ComponentTempReading>();

        var smartSnap = _smart.Snapshot();
        if (smartSnap.Supported)
        {
            foreach (var drive in smartSnap.Drives)
            {
                if (drive.TemperatureC is { } driveC)
                {
                    result.Add(new ComponentTempReading(drive.Id, "storage", drive.Name, driveC));
                }
            }
        }

        var ramSensors = FindRamTempSensors(_sensors.GetMemorySensors());
        for (var i = 0; i < ramSensors.Count; i++)
        {
            result.Add(new ComponentTempReading($"ram:{i}", "ram", ramSensors[i].Name, ramSensors[i].Value));
        }

        return result;
    }

    // Shared with Nexus.Service.Mcp.History.HistoryIdMapping.RamSensorIds so its
    // temp.ram:<index> mapping never drifts from what this sampler records.
    internal static IReadOnlyList<HardwareSensor> FindRamTempSensors(IReadOnlyList<HardwareSensor> memorySensors)
    {
        var result = new List<HardwareSensor>();
        foreach (var sensor in memorySensors)
        {
            if (!string.Equals(sensor.Type, "Temperature", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!sensor.Name.Contains("dimm", StringComparison.OrdinalIgnoreCase)
                && !sensor.Name.Contains("memory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(sensor);
        }
        return result;
    }

    private IReadOnlyList<GpuReading> ReadGpus()
    {
        var readouts = _sensors.GetGpus();
        if (readouts.Count == 0)
        {
            return Array.Empty<GpuReading>();
        }

        var result = new List<GpuReading>(readouts.Count);
        foreach (var g in readouts)
        {
            var load = FindSensor(g.Sensors, "Load", "Core")?.Value;
            var temp = FindSensor(g.Sensors, "Temperature", "Core")?.Value;
            result.Add(new GpuReading(MetricsHistory.SanitizeId(g.Id), g.Name, g.AdapterLuid, load, temp));
        }
        return result;
    }

    private IReadOnlyList<FanReading> ReadFans()
    {
        var channels = _fans.GetFanChannels();
        if (channels.Count == 0)
        {
            return Array.Empty<FanReading>();
        }

        var result = new List<FanReading>(channels.Count);
        foreach (var ch in channels)
        {
            result.Add(new FanReading(
                MetricsHistory.SanitizeId(ch.Id), ch.Name, ch.RpmUnavailable ? null : ch.Rpm, ch.DutyPercent));
        }
        return result;
    }

    // Nexus.Service.Mcp.History.HistoryIdMapping reuses this exact match order so get_sensors' historyId stays in sync with sampling.
    internal static HardwareSensor? FindSensor(IReadOnlyList<HardwareSensor> sensors, string type, string nameContains)
    {
        HardwareSensor? fallback = null;
        foreach (var s in sensors)
        {
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            fallback ??= s;
            if (s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return fallback;
    }
}
