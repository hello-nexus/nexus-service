using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Monitoring.Events;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Write tool: adds a timeline marker exactly as POST /monitoring/events does,
/// through the same IMonitoringEventStore.Append call with kind fixed to
/// custom, so the registered decorator broadcasts it on the monitoring/events
/// topic the same way. Kind is not caller-selectable: app-open/uac-escalation/
/// usb-attach/usb-detach are MonitoringEventCollector's own system markers.
/// </summary>
public sealed class AddMonitoringEventTool : IMcpTool
{
    internal const int MaxDetailLength = MonitoringHistoryRoutes.MaxCustomEventLabelLength * 4;

    private readonly IMonitoringEventStore _store;

    public AddMonitoringEventTool(IMonitoringEventStore store) => _store = store;

    public string Name => "add_monitoring_event";
    public string Title => "Add Monitoring Event";

    public string Description =>
        "Adds a marker to the monitoring dashboard timeline, the same as the timeline's own add-event " +
        "control. Always recorded as a custom event; label and detail beyond " +
        $"{MonitoringHistoryRoutes.MaxCustomEventLabelLength} and {MaxDetailLength} characters are " +
        "truncated. Use it right after changing cooling, lighting, or a profile so a later " +
        "get_temperature_history or query_events call can correlate against exactly when it happened.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"label\":{\"type\":\"string\",\"description\":\"Short marker label shown on the timeline.\"}," +
        "\"detail\":{\"type\":\"string\",\"description\":\"Optional longer detail text, truncated beyond " +
        $"{MaxDetailLength}" +
        " characters.\"}" +
        "},\"required\":[\"label\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var labelError = MonitoringHistoryRoutes.NormalizeCustomEventLabel(McpArgs.StringArg(args, "label"), out var label);
        if (labelError is not null)
        {
            return Task.FromResult(McpToolExecutionResult.Error(labelError));
        }

        var detail = McpArgs.StringArg(args, "detail");
        if (detail is { Length: > MaxDetailLength })
        {
            detail = detail[..MaxDetailLength];
        }

        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var created = _store.Append(t, MonitoringEventKinds.Custom, label, detail, custom: true);

        var result = new McpAddMonitoringEventResult { Id = created.Id, T = created.TUtcMs, Kind = created.Kind, Label = created.Label };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpAddMonitoringEventResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
