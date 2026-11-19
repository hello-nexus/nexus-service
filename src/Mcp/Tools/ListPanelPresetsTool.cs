using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: the saved presets of one panel, the same list GET /panel/devices/{id}/presets returns.</summary>
public sealed class ListPanelPresetsTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;

    public ListPanelPresetsTool(PanelDeviceRegistry registry) => _registry = registry;

    public string Name => "list_panel_presets";
    public string Title => "List Panel Presets";

    public string Description =>
        "Lists the saved presets of one panel (id, name, and whether it is the loaded one). A preset is a " +
        "named snapshot of the panel's whole look and layout. Read-only, so it works even when panel " +
        "editing is turned off. Call this before activate_panel_preset, rename_panel_preset or " +
        "delete_panel_preset when the user names a preset you have not confirmed exists.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"panel\":{\"type\":\"string\",\"description\":\"" + PanelToolSupport.PanelArgDescription + "\"}" +
        "},\"required\":[\"panel\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var record = PanelToolSupport.Resolve(_registry, McpArgs.StringArg(args, "panel"), out var error);
        if (record is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }
        var presets = _registry.ListPresets(record.Id) ?? new Models.Panel.PanelPresetsResponse();
        var json = JsonSerializer.Serialize(PanelToolSupport.PresetsResult(record.Id, presets), AppJsonContext.Default.McpPanelPresetsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
