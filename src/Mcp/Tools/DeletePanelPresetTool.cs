using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: the same PanelDeviceRegistry.DeletePreset DELETE /panel/devices/{id}/presets/{presetId} calls.</summary>
public sealed class DeletePanelPresetTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;

    public DeletePanelPresetTool(PanelDeviceRegistry registry) => _registry = registry;

    public string Name => "delete_panel_preset";
    public string Title => "Delete Panel Preset";

    public string Description =>
        "Deletes one saved preset of a panel. This cannot be undone. Deleting the loaded preset leaves " +
        "the panel showing what it shows now, with no preset loaded. Use only when the user asks to " +
        "delete a preset. Accepts the preset name or its id from list_panel_presets.";

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

        var presets = _registry.DeletePreset(record.Id, preset.Id);
        if (presets is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Panel not found."));
        }
        var json = JsonSerializer.Serialize(PanelToolSupport.PresetsResult(record.Id, presets), AppJsonContext.Default.McpPanelPresetsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
