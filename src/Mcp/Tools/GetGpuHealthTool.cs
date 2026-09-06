using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only diagnostics tool: per-GPU health backing GET /diagnostics/gpu.</summary>
public sealed class GetGpuHealthTool : IMcpTool
{
    private readonly GpuHealthMonitor _gpu;
    private readonly EventLogMonitor _events;

    public GetGpuHealthTool(GpuHealthMonitor gpu, EventLogMonitor events)
    {
        _gpu = gpu;
        _events = events;
    }

    public string Name => "get_gpu_health";
    public string Title => "GPU Health";

    public string Description =>
        "Returns per-GPU health: name, driver version, temperature, power draw, active hardware " +
        "throttle reasons, and the count of TDR (Timeout Detection and Recovery, i.e. a driver-reset " +
        "crash) events in the last 30 days. NVIDIA only (needs NVML); Supported is false elsewhere. " +
        "Call get_health first to see if the GPU needs attention; call this for the detail behind " +
        "that verdict.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var counts = _events.CountsSince(TimeSpan.FromDays(30));
        var tdr = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceTdr);
        var snapshot = _gpu.Snapshot();

        var result = new McpGpuHealthResult
        {
            Supported = snapshot.Supported,
            Gpus = snapshot.Gpus.Select(g => new McpGpuInfo
            {
                Name = g.Name,
                DriverVersion = g.DriverVersion,
                TemperatureC = g.TemperatureC,
                PowerW = g.PowerW,
                Throttle = g.Throttle,
                RecentTdrCount = tdr,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpGpuHealthResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
