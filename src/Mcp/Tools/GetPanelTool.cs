using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: one panel's look, full widget layout and background media, as the panel editor reads them.</summary>
public sealed class GetPanelTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;
    private readonly PanelBgLibrary _media;

    public GetPanelTool(PanelDeviceRegistry registry, PanelBgLibrary media)
    {
        _registry = registry;
        _media = media;
    }

    public string Name => "get_panel";
    public string Title => "Get Panel";

    public string Description =>
        "Returns one panel's look (theme, accent, background, widget style, font, gauge colours), its full " +
        "widget layout (pages of widgets with id, type, size, col, row and config), its page grid when " +
        "known (a HYTE Y70 portrait grid is 4 columns x 16 rows) and the background media it can show. " +
        "Call this before update_panel_look, set_panel_layout or update_panel_widget so edits start from " +
        "the current state. " + PanelToolSupport.MonitoringSlotHelp;

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

        var grid = PanelToolSupport.GridOf(record);
        var result = new McpPanelDetailResult
        {
            Id = record.Id,
            Name = record.DisplayName,
            Surface = PanelToolSupport.SurfaceOf(record),
            Family = record.Capabilities?.Family,
            Orientation = record.Capabilities?.Orientation,
            Grid = grid is { } g ? new McpPanelGrid { Cols = g.Cols, Rows = g.Rows } : null,
            Look = PanelToolSupport.LookOf(record),
            Layout = PanelToolSupport.EffectiveLayout(record),
            BackgroundMedia = _media.ListItems(record.Id)
                .Select(i => new McpPanelMediaItem { Id = i.Id, Name = i.Name, Type = i.Type })
                .ToList(),
            ActivePresetId = _registry.ListPresets(record.Id)?.ActiveId,
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpPanelDetailResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
