using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: crash, BSOD, WHEA, and GPU TDR incidents,
/// grouped and game-decorated exactly like GET /diagnostics/incidents.</summary>
public sealed class GetIncidentsTool : IMcpTool
{
    internal const int DefaultDays = 7;
    internal const int MaxDays = 30;

    private readonly EventLogMonitor _events;
    private readonly SteamGameLibraryCache _steamCache;

    public GetIncidentsTool(EventLogMonitor events, SteamGameLibraryCache steamCache)
    {
        _events = events;
        _steamCache = steamCache;
    }

    public string Name => "get_incidents";
    public string Title => "Diagnostic Incidents";

    public string Description =>
        "Returns crash, BSOD, WHEA, and GPU TDR incidents over a window in days (default " +
        $"{DefaultDays}, max {MaxDays}), newest first, with repeat occurrences of the same incident " +
        "collapsed into one row carrying a repeat count and first/last seen times. supported is false " +
        "on a host with no event log to read. Use to explain recent stability problems or check " +
        "whether a crash correlates with something get_temperature_history or query_events shows.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"days\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"How far back to look, in days. " +
        $"Default {DefaultDays}, capped at {MaxDays}." +
        "\"}" +
        "},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var requested = McpArgs.IntArg(args, "days");
        var days = requested is { } d && d > 0 ? Math.Min(d, MaxDays) : DefaultDays;

        var response = DiagnosticsHealthRoutes.BuildIncidentsResponse(
            _events, _steamCache, days, group: true, includeGpuDriver: false);
        var json = JsonSerializer.Serialize(response, AppJsonContext.Default.IncidentsResponse);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
