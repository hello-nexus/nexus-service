using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: the same PanelDeviceRegistry.CreatePreset POST /panel/devices/{id}/presets calls.</summary>
public sealed class CreatePanelPresetTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;

    public CreatePanelPresetTool(PanelDeviceRegistry registry) => _registry = registry;

    public string Name => "create_panel_preset";
    public string Title => "Create Panel Preset";

    public string Description =>
        "Saves the panel's current look and layout as a new named preset and makes it the loaded one. " +
        "Later edits then change that preset. Use before a large redesign so the user can switch back, " +
        $"or when the user asks to save the panel as a preset. A panel holds at most {PanelPresets.Cap} presets.";

    public McpCapability Capability => McpCapability.Panels;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"panel\":{\"type\":\"string\",\"description\":\"" + PanelToolSupport.PanelArgDescription + "\"}," +
        "\"name\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Name for the new preset.\"}" +
        "},\"required\":[\"panel\",\"name\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var record = PanelToolSupport.Resolve(_registry, McpArgs.StringArg(args, "panel"), out var error);
        if (record is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }
        var name = (McpArgs.StringArg(args, "name") ?? "").Trim();
        if (name.Length == 0)
        {
            return Task.FromResult(McpToolExecutionResult.Error("'name' is required."));
        }

        var presets = _registry.CreatePreset(record.Id, name, out var capped);
        if (capped)
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                $"The panel already holds {PanelPresets.Cap} presets. Delete one with delete_panel_preset first."));
        }
        if (presets is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Panel not found."));
        }

        var json = JsonSerializer.Serialize(PanelToolSupport.PresetsResult(record.Id, presets), AppJsonContext.Default.McpPanelPresetsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
