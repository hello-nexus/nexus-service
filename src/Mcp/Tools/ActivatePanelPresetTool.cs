using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: the same PanelDeviceRegistry.ActivatePreset POST /panel/devices/{id}/presets/{presetId}/activate calls.</summary>
public sealed class ActivatePanelPresetTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;
    private readonly MultiplexHub _hub;

    public ActivatePanelPresetTool(PanelDeviceRegistry registry, MultiplexHub hub)
    {
        _registry = registry;
        _hub = hub;
    }

    public string Name => "activate_panel_preset";
    public string Title => "Activate Panel Preset";

    public string Description =>
        "Loads one saved preset onto a panel: its look and layout replace what the panel shows. The " +
        "panel's current state is first saved back into the preset that was loaded, so nothing is lost. " +
        "Use when the user asks to switch a panel to a named preset. Accepts the preset name or its id " +
        "from list_panel_presets.";

    public McpCapability Capability => McpCapability.Panels;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"panel\":{\"type\":\"string\",\"description\":\"" + PanelToolSupport.PanelArgDescription + "\"}," +
        "\"preset\":{\"type\":\"string\",\"description\":\"Preset name or id from list_panel_presets.\"}" +
        "},\"required\":[\"panel\",\"preset\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var record = PanelToolSupport.Resolve(_registry, McpArgs.StringArg(args, "panel"), out var error);
        if (record is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }
        var current = _registry.ListPresets(record.Id) ?? new Models.Panel.PanelPresetsResponse();
        var preset = PanelToolSupport.ResolvePreset(current, McpArgs.StringArg(args, "preset"), out error);
        if (preset is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }

        var presets = _registry.ActivatePreset(record.Id, preset.Id);
        if (presets is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Preset not found."));
        }
        PanelTopics.BroadcastPanelDevice(_hub, record.Id);

        var json = JsonSerializer.Serialize(PanelToolSupport.PresetsResult(record.Id, presets), AppJsonContext.Default.McpPanelPresetsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
