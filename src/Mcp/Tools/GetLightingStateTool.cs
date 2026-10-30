using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: sync mode, active effect, brightness, and static color.</summary>
public sealed class GetLightingStateTool : IMcpTool
{
    private readonly ILightingProvider _lighting;
    private readonly LightingEngine _engine;
    private readonly IConfigStore _store;

    public GetLightingStateTool(ILightingProvider lighting, LightingEngine engine, IConfigStore store)
    {
        _lighting = lighting;
        _engine = engine;
        _store = store;
    }

    public string Name => "get_lighting_state";
    public string Title => "Lighting State";

    public string Description =>
        "Returns the current lighting sync mode, active effect, global brightness, and static color. " +
        "Call this before proposing or explaining any lighting change so the change is grounded in " +
        "what is actually running.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var settings = _store.Load();
        var lighting = settings.Lighting;
        var color = lighting.StaticColor;
        var result = new McpLightingStateResult
        {
            Sync = _lighting.GetSync(),
            CurrentEffect = _engine.CurrentEffectName,
            GlobalBrightness = lighting.GlobalBrightness,
            MasterScalesDevices = settings.MasterScalesDevices,
            StaticColor = $"#{color.R:x2}{color.G:x2}{color.B:x2}",
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpLightingStateResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
