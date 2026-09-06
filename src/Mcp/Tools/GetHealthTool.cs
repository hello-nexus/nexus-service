using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only diagnostics tool: the aggregated health verdict backing GET /diagnostics/health.</summary>
public sealed class GetHealthTool : IMcpTool
{
    private readonly DiagnosticsHealthModel _model;
    private readonly FeatureGates _gates;

    public GetHealthTool(DiagnosticsHealthModel model, FeatureGates gates)
    {
        _model = model;
        _gates = gates;
    }

    public string Name => "get_health";
    public string Title => "Health Overview";

    public string Description =>
        "Returns a one-call overall health verdict (ok, watch, act, or unknown) plus a status and " +
        "reasons for each monitored component: storage drives, memory, GPU, cooling, and system. " +
        "Call this first for a question like 'is anything wrong with my PC' - only reach for " +
        "get_storage_health, get_gpu_health, or get_memory_info afterward if one component's reasons " +
        "need more detail than this gives.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var health = _gates.Diagnostics ? _model.BuildHealth() : new DiagnosticsHealthResponse { Enabled = false };
        var result = new McpHealthResult
        {
            Supported = health.Supported,
            Enabled = health.Enabled,
            Overall = health.Overall,
            Components = new List<HealthComponent>(health.Components),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpHealthResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
