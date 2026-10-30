using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: the same PanelDeviceRegistry.RenamePreset PUT /panel/devices/{id}/presets/{presetId} calls.</summary>
public sealed class RenamePanelPresetTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;

    public RenamePanelPresetTool(PanelDeviceRegistry registry) => _registry = registry;

    public string Name => "rename_panel_preset";
    public string Title => "Rename Panel Preset";

    public string Description =>
        "Renames one saved preset of a panel. The preset's look and layout do not change. " +
        "Accepts the preset name or its id from list_panel_presets.";

    public McpCapability Capability => McpCapability.Panels;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"panel\":{\"type\":\"string\",\"description\":\"" + PanelToolSupport.PanelArgDescription + "\"}," +
        "\"preset\":{\"type\":\"string\",\"description\":\"Preset name or id from list_panel_presets.\"}," +
        "\"name\":{\"type\":\"string\",\"minLength\":1,\"description\":\"New preset name.\"}" +
        "},\"required\":[\"panel\",\"preset\",\"name\"],\"additionalProperties\":false}";

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
        var current = _registry.ListPresets(record.Id) ?? new Models.Panel.PanelPresetsResponse();
        var preset = PanelToolSupport.ResolvePreset(current, McpArgs.StringArg(args, "preset"), out error);
        if (preset is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }

        var presets = _registry.RenamePreset(record.Id, preset.Id, name);
        if (presets is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Preset not found."));
        }
        var json = JsonSerializer.Serialize(PanelToolSupport.PresetsResult(record.Id, presets), AppJsonContext.Default.McpPanelPresetsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
