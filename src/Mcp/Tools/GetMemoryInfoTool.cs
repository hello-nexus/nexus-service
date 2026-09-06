using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only diagnostics tool: installed RAM module layout backing GET /diagnostics/memory.</summary>
public sealed class GetMemoryInfoTool : IMcpTool
{
    private readonly MemoryDiagnosticOrchestrator _memDiag;

    public GetMemoryInfoTool(MemoryDiagnosticOrchestrator memDiag) => _memDiag = memDiag;

    public string Name => "get_memory_info";
    public string Title => "Memory Info";

    public string Description =>
        "Returns installed RAM modules (slot, size, speed, manufacturer, part number), whether XMP is " +
        "likely active, and the result of the last Windows Memory Diagnostic run if one was scheduled. " +
        "Windows only; Supported is false elsewhere. Call this for questions about RAM capacity, " +
        "speed, or a suspected memory-stability issue; call get_health first if the question is just " +
        "'is anything wrong'.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var info = MemoryInfoProvider.GetSnapshot();
        var result = new McpMemoryInfoResult
        {
            Supported = info.Supported,
            Modules = new List<MemoryModuleInfo>(info.Modules),
            XmpLikelyActive = info.XmpLikelyActive,
            LastTest = _memDiag.LastResult(),
            TestScheduled = _memDiag.IsScheduled(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpMemoryInfoResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
