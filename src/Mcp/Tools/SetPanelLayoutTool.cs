using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Write tool: replaces a panel's pages of widgets through PanelDeviceRegistry.Patch, the same call
/// POST /panel/devices/{id} makes, after checking the layout against the widget catalog and grid.
/// </summary>
public sealed class SetPanelLayoutTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;
    private readonly MultiplexHub _hub;

    public SetPanelLayoutTool(PanelDeviceRegistry registry, MultiplexHub hub)
    {
        _registry = registry;
        _hub = hub;
    }

    public string Name => "set_panel_layout";
    public string Title => "Set Panel Layout";

    public string Description =>
        "Replaces every page of widgets on a panel: use it to add, remove, move or resize widgets. Start " +
        "from get_panel's layout and send back the full pages list; a widget left out is removed. Each " +
        "widget needs type, size (\"<cols>x<rows>\" grid cells, or 2x2round) and col/row of its top-left " +
        "cell (0-based); keep a widget's id to keep its settings, omit id for a new widget. Widgets on a " +
        "page must not overlap and must fit the grid (a HYTE Y70 in portrait is 4 columns x 16 rows, so " +
        "four 4x4 widgets fill a page). At most 10 pages. Common types and sizes: clock, media, weather, " +
        "calendar, monitoring and gallery (2x2, 2x4, 4x2, 4x4, 2x2round), cooling and stocks (2x2, 2x4, " +
        "4x2, 4x4), lighting (2x2, 4x2, 4x4), deck (2x2, 4x1, 4x2, 4x4), mixer and steam (4x4); an invalid " +
        "size returns the sizes the type offers. Deck keys that open files, send hotkeys, type text or " +
        "play audio can only be added from the Nexus app. To change one widget's settings use " +
        "update_panel_widget instead.";

    public McpCapability Capability => McpCapability.Panels;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"panel\":{\"type\":\"string\",\"description\":\"" + PanelToolSupport.PanelArgDescription + "\"}," +
        "\"pages\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":10,\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"id\":{\"type\":\"string\",\"description\":\"Keep an existing page id; omit for a new page.\"}," +
        "\"label\":{\"type\":\"string\"}," +
        "\"widgets\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"id\":{\"type\":\"string\",\"description\":\"Keep an existing widget id; omit for a new widget.\"}," +
        "\"type\":{\"type\":\"string\",\"description\":\"Widget type, e.g. clock, monitoring, media, or app:<id> for a marketplace widget.\"}," +
        "\"size\":{\"type\":\"string\",\"description\":\"<cols>x<rows>, e.g. 4x4, or 2x2round.\"}," +
        "\"col\":{\"type\":\"integer\",\"minimum\":0},\"row\":{\"type\":\"integer\",\"minimum\":0}," +
        "\"isImmersive\":{\"type\":\"boolean\"},\"locked\":{\"type\":\"boolean\"}," +
        "\"config\":{\"type\":\"object\",\"description\":\"Widget settings; keep get_panel's config for an existing widget.\"}" +
        "},\"required\":[\"type\",\"size\",\"col\",\"row\"]}}" +
        "},\"required\":[\"widgets\"]}}," +
        "\"activePageId\":{\"type\":\"string\",\"description\":\"Page the panel shows; defaults to the current one, or the first page.\"}," +
        "\"immersiveOnLoadWidgetId\":{\"type\":[\"string\",\"null\"],\"description\":\"Widget on the first page the panel opens full-screen; null for none.\"}" +
        "},\"required\":[\"panel\",\"pages\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var record = PanelToolSupport.Resolve(_registry, McpArgs.StringArg(args, "panel"), out var error);
        if (record is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }
        if (!McpArgs.TryGetArray(args, "pages", out var pagesJson))
        {
            return Task.FromResult(McpToolExecutionResult.Error("'pages' is required: the full list of pages, as get_panel returns them."));
        }

        if (MissingWidgetFields(pagesJson) is { } missing)
        {
            return Task.FromResult(McpToolExecutionResult.Error(missing));
        }

        List<PanelPageDto>? pages;
        try
        {
            pages = JsonSerializer.Deserialize(pagesJson, AppJsonContext.Default.ListPanelPageDto);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(McpToolExecutionResult.Error($"'pages' could not be read: {ex.Message}"));
        }
        if (pages is null || pages.Any(p => p is null || p.Widgets is null || p.Widgets.Any(w => w is null)))
        {
            return Task.FromResult(McpToolExecutionResult.Error("'pages' must be a list of pages, each with a widgets list."));
        }

        var current = PanelToolSupport.EffectiveLayout(record);
        var layout = PanelPresets.CloneLayout(current)!;
        layout.LayoutSchemaVersion = 2;
        layout.Pages = pages;
        var assigned = new List<string>();
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Id))
            {
                page.Id = Guid.NewGuid().ToString();
                assigned.Add(page.Id);
            }
            foreach (var widget in page.Widgets)
            {
                if (string.IsNullOrWhiteSpace(widget.Id))
                {
                    widget.Id = Guid.NewGuid().ToString();
                    assigned.Add(widget.Id);
                }
            }
        }

        var activePage = McpArgs.StringArg(args, "activePageId");
        if (activePage is not null && pages.All(p => p.Id != activePage))
        {
            return Task.FromResult(McpToolExecutionResult.Error($"activePageId '{activePage}' names no page in 'pages'."));
        }
        layout.ActivePageId = activePage
            ?? (pages.Any(p => p.Id == current.ActivePageId) ? current.ActivePageId : pages.FirstOrDefault()?.Id);
        if (args!.Value.TryGetProperty("immersiveOnLoadWidgetId", out var immersive))
        {
            layout.ImmersiveOnLoadWidgetId = immersive.ValueKind == JsonValueKind.String ? immersive.GetString() : null;
        }
        else if (pages.Count == 0 || pages[0].Widgets.All(w => w.Id != layout.ImmersiveOnLoadWidgetId))
        {
            layout.ImmersiveOnLoadWidgetId = null;
        }

        var problems = PanelToolSupport.Validate(layout, record);
        if (problems.Count > 0)
        {
            return Task.FromResult(McpToolExecutionResult.Error("The layout was not saved. " + string.Join(" ", problems)));
        }
        if (DeckLayoutPolicy.IntroducesPrivilegedActions(layout, current))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "The layout was not saved: it adds or changes deck keys that open files or folders, send hotkeys, " +
                "type text, play audio or launch an app by path. Add those keys from the Nexus app."));
        }

        var updated = _registry.Patch(record.Id, new PanelDevicePatch { Layout = layout });
        if (updated is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Panel not found."));
        }
        PanelTopics.BroadcastPanelDevice(_hub, record.Id);

        var result = new McpPanelLayoutResult
        {
            Panel = record.Id,
            Pages = layout.Pages.Count,
            Widgets = layout.Pages.Sum(p => p.Widgets.Count),
            AssignedIds = assigned,
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpPanelLayoutResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    // PanelWidgetDto defaults Size to 2x2 and Type to "", so a field left out would pass
    // validation as a real value; require them on the wire instead.
    private static string? MissingWidgetFields(JsonElement pages)
    {
        var p = 0;
        foreach (var page in pages.EnumerateArray())
        {
            if (page.ValueKind == JsonValueKind.Object
                && page.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
            {
                var w = 0;
                foreach (var widget in widgets.EnumerateArray())
                {
                    foreach (var field in new[] { "type", "size" })
                    {
                        if (widget.ValueKind != JsonValueKind.Object
                            || !widget.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                        {
                            return $"page {p} widget {w}: '{field}' is required.";
                        }
                    }
                    w++;
                }
            }
            p++;
        }
        return null;
    }
}
