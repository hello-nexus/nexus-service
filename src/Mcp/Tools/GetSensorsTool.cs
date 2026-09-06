using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.History;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: the full sensor list for one hardware device.</summary>
public sealed class GetSensorsTool : IMcpTool
{
    private readonly ISensorProvider _sensors;

    public GetSensorsTool(ISensorProvider sensors) => _sensors = sensors;

    public string Name => "get_sensors";
    public string Title => "Sensors";

    public string Description =>
        "Returns the full sensor list (current value, min, max, average, units) for one hardware " +
        "device: cpu, gpu, memory, motherboard, or storage. Call this after get_system_overview when " +
        "the summary numbers aren't enough - for example to read every CPU core clock, every GPU fan, " +
        "or a specific storage drive's SMART data (pass its id as 'drive'). A sensor's historyId, when " +
        "present, is the same id query_sensor_history and get_history_summary use for it, and either " +
        "that id or the sensor's own 'id' works as query_sensor_history's sensorId argument; historyId " +
        "is null for a sensor with no tracked history (every fan and storage sensor).";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"device\":{\"type\":\"string\",\"enum\":[\"cpu\",\"gpu\",\"memory\",\"motherboard\",\"storage\"]}," +
        "\"drive\":{\"type\":\"string\",\"description\":\"Storage drive id from a prior get_sensors(device=storage) call. Only used when device is storage.\"}" +
        "},\"required\":[\"device\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var device = McpArgs.StringArg(args, "device");
        if (string.IsNullOrEmpty(device))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "'device' is required. Expected one of: cpu, gpu, memory, motherboard, storage."));
        }

        List<HardwareSensor> sensors;
        switch (device)
        {
            case "cpu":
                sensors = new List<HardwareSensor>(_sensors.GetCpuSensors());
                TagCpuHistoryIds(sensors);
                break;
            case "gpu":
                sensors = ResolveGpuSensors();
                break;
            case "memory":
                sensors = new List<HardwareSensor>(_sensors.GetMemorySensors());
                TagRamHistoryIds(sensors);
                break;
            case "motherboard":
                // Fan RPM/duty history ids come from IFanControlProvider, a
                // separate provider whose ids do not correspond to these raw
                // LHM sensor ids - see HistoryIdMapping's doc comment.
                sensors = new List<HardwareSensor>(_sensors.GetMotherboardSensors());
                break;
            case "storage":
                var storageResult = ResolveStorageSensors(args);
                if (storageResult.Error is not null)
                {
                    return Task.FromResult(McpToolExecutionResult.Error(storageResult.Error));
                }
                // Storage history ids are keyed by drive serial (ISmartHealthSource),
                // not any of these raw SMART/LHM sensor ids - see HistoryIdMapping.
                sensors = storageResult.Sensors!;
                break;
            default:
                return Task.FromResult(McpToolExecutionResult.Error(
                    $"Unknown device '{device}'. Expected one of: cpu, gpu, memory, motherboard, storage."));
        }

        var result = new McpSensorsResult { Device = device, Sensors = sensors };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSensorsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    private static void TagCpuHistoryIds(List<HardwareSensor> cpuSensors)
    {
        var picked = HistoryIdMapping.CpuTempSensorId(cpuSensors);
        if (picked is null)
        {
            return;
        }
        foreach (var s in cpuSensors)
        {
            if (s.Id == picked)
            {
                s.HistoryId = "cpu.temp";
            }
        }
    }

    private static void TagRamHistoryIds(List<HardwareSensor> memorySensors)
    {
        var map = HistoryIdMapping.RamSensorIds(memorySensors);
        foreach (var s in memorySensors)
        {
            if (map.TryGetValue(s.Id, out var historyId))
            {
                s.HistoryId = historyId;
            }
        }
    }

    private List<HardwareSensor> ResolveGpuSensors()
    {
        var merged = new List<HardwareSensor>();
        foreach (var gpu in _sensors.GetGpus())
        {
            var map = HistoryIdMapping.GpuSensorIds(gpu);
            foreach (var s in gpu.Sensors)
            {
                if (map.TryGetValue(s.Id, out var historyId))
                {
                    s.HistoryId = historyId;
                }
                merged.Add(s);
            }
        }
        return merged;
    }

    private (List<HardwareSensor>? Sensors, string? Error) ResolveStorageSensors(JsonElement? args)
    {
        var components = _sensors.GetStorageComponents(includeSmart: true);
        var drive = McpArgs.StringArg(args, "drive");
        if (string.IsNullOrEmpty(drive))
        {
            var merged = new List<HardwareSensor>();
            foreach (var component in components.Values)
            {
                merged.AddRange(component.Sensors);
            }
            return (merged, null);
        }

        if (components.TryGetValue(drive, out var comp))
        {
            return (new List<HardwareSensor>(comp.Sensors), null);
        }

        var known = string.Join(", ", components.Keys);
        return (null, $"Unknown drive '{drive}'. Known drives: {(known.Length == 0 ? "(none detected)" : known)}");
    }
}
