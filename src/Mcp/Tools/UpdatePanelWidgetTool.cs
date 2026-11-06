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
/// Write tool: changes one widget's settings, size or position in place and saves the layout through
/// PanelDeviceRegistry.Patch, so an agent need not resend the whole layout for a small edit.
/// </summary>
public sealed class UpdatePanelWidgetTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;
    private readonly MultiplexHub _hub;

    public UpdatePanelWidgetTool(PanelDeviceRegistry registry, MultiplexHub hub)
    {
        _registry = registry;
        _hub = hub;
    }

    public string Name => "update_panel_widget";
    public string Title => "Update Panel Widget";

    public string Description =>
        "Changes one widget on a panel: merges keys into its config (a key set to null is removed), and " +
        "optionally moves or resizes it. The widget id comes from get_panel. The rest of the layout stays " +
        "as it is, and the result must still fit the grid without overlapping another widget. " +
        PanelToolSupport.MonitoringSlotHelp + " To add or remove widgets use set_panel_layout.";

    public McpCapability Capability => McpCapability.Panels;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"panel\":{\"type\":\"string\",\"description\":\"" + PanelToolSupport.PanelArgDescription + "\"}," +
        "\"widget\":{\"type\":\"string\",\"description\":\"Widget id from get_panel.\"}," +
        "\"config\":{\"type\":\"object\",\"description\":\"Config keys to set; null removes a key.\"}," +
        "\"size\":{\"type\":\"string\",\"description\":\"New size, <cols>x<rows> or 2x2round.\"}," +
        "\"col\":{\"type\":\"integer\",\"minimum\":0}," +
        "\"row\":{\"type\":\"integer\",\"minimum\":0}" +
        "},\"required\":[\"panel\",\"widget\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var record = PanelToolSupport.Resolve(_registry, McpArgs.StringArg(args, "panel"), out var error);
        if (record is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(error));
        }

        var current = PanelToolSupport.EffectiveLayout(record);
        var layout = PanelPresets.CloneLayout(current)!;
        var widgetId = McpArgs.StringArg(args, "widget");
        var widget = layout.Pages.SelectMany(p => p.Widgets).FirstOrDefault(w => w.Id == widgetId);
        if (widget is null)
        {
            var known = string.Join(", ", layout.Pages.SelectMany(p => p.Widgets).Select(w => $"{w.Id} ({w.Type})"));
            return Task.FromResult(McpToolExecutionResult.Error(
                $"Unknown widget '{widgetId}' on panel '{record.DisplayName}'. Widgets: {(known.Length == 0 ? "(none)" : known)}."));
        }

        var a = args!.Value;
        var touched = false;
        if (a.TryGetProperty("config", out var config))
        {
            if (config.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(McpToolExecutionResult.Error("'config' must be an object of keys to set."));
            }
            widget.Config ??= new Dictionary<string, JsonElement>();
            foreach (var entry in config.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Null)
                {
                    widget.Config.Remove(entry.Name);
                }
                else
                {
                    widget.Config[entry.Name] = entry.Value.Clone();
                }
            }
            touched = true;
        }
        if (McpArgs.StringArg(args, "size") is { } size)
        {
            widget.Size = size;
            touched = true;
        }
        foreach (var (name, set) in new (string, System.Action<int>)[] { ("col", c => widget.Col = c), ("row", r => widget.Row = r) })
        {
            if (!a.TryGetProperty(name, out var value))
            {
                continue;
            }
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var cell))
            {
                return Task.FromResult(McpToolExecutionResult.Error($"'{name}' must be a whole number."));
            }
            set(cell);
            touched = true;
        }
        if (!touched)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Send config, size, col or row to change."));
        }

        var problems = PanelToolSupport.Validate(layout, record);
        if (problems.Count > 0)
        {
            return Task.FromResult(McpToolExecutionResult.Error("The widget was not changed. " + string.Join(" ", problems)));
        }
        if (DeckLayoutPolicy.IntroducesPrivilegedActions(layout, current))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "The widget was not changed: it adds or changes deck keys that open files or folders, send hotkeys, " +
                "type text, play audio or launch an app by path. Add those keys from the Nexus app."));
        }

        var updated = _registry.Patch(record.Id, new PanelDevicePatch { Layout = layout });
        if (updated is null)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Panel not found."));
        }
        PanelTopics.BroadcastPanelDevice(_hub, record.Id);

        var result = new McpPanelWidgetResult { Panel = record.Id, Widget = widget };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpPanelWidgetResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
