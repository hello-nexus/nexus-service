using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: the same master brightness level POST /lighting/global-brightness sets.</summary>
public sealed class SetBrightnessTool : IMcpTool
{
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;

    public SetBrightnessTool(IConfigStore store, MultiplexHub hub)
    {
        _store = store;
        _hub = hub;
    }

    public string Name => "set_brightness";
    public string Title => "Set Lighting Brightness";

    public string Description =>
        "Sets the master brightness cap applied to every synced RGB device, as a percentage. Use " +
        "when the user asks to dim, brighten, or set a specific brightness for the lighting.";

    public McpCapability Capability => McpCapability.Lighting;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"percent\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100}" +
        "},\"required\":[\"percent\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var percent = McpArgs.NumberArg(args, "percent");
        if (percent is null || !double.IsFinite(percent.Value) || percent.Value < 0 || percent.Value > 100)
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "'percent' is required and must be a number between 0 and 100."));
        }

        var clamped = Math.Clamp((float)(percent.Value / 100.0), 0f, 1f);
        _store.Update(s => s.Lighting.GlobalBrightness = clamped);
        PanelTopics.BroadcastLighting(_hub);

        var result = new McpSetBrightnessResult { Percent = (int)Math.Round(percent.Value) };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSetBrightnessResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
