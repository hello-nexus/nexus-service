using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: fan channels, temperature sources, curves, and the active preset.</summary>
public sealed class GetCoolingStateTool : IMcpTool
{
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly ThermalGuardController? _guard;

    public GetCoolingStateTool(IFanControlProvider fans, IConfigStore store, ThermalGuardController? guard = null)
    {
        _fans = fans;
        _store = store;
        _guard = guard;
    }

    public string Name => "get_cooling_state";
    public string Title => "Cooling State";

    public string Description =>
        "Returns every fan channel, temperature source, configured fan curve, the active cooling " +
        "preset, and the global speed modifier. Call this before proposing or explaining any cooling " +
        "change so the change is grounded in the machine's actual current state.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var cooling = _store.Load().Cooling;
        var result = new McpCoolingStateResult
        {
            FanChannels = new List<FanChannel>(_fans.GetFanChannels()),
            TemperatureSources = new List<TemperatureSource>(_fans.GetTemperatureSources()),
            Curves = cooling.Curves.Select(McpCurveMapper.ToWireCurve).ToList(),
            ActivePreset = cooling.ActivePreset,
            GlobalSpeedModifier = cooling.GlobalSpeedModifier,
            ThermalGuard = _guard?.GetState(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpCoolingStateResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
