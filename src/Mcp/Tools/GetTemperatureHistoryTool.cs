using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: temperature buckets tiered by window
/// length (TemperatureInsights.TierWidthMinutesFor) per component, each
/// annotated with the slot's dominant foreground app.</summary>
public sealed class GetTemperatureHistoryTool : IMcpTool
{
    internal const int DefaultHours = 24;
    // Mirrors GET /diagnostics/temperatures' own cap so this tool can never
    // return a wider window than the REST route allows.
    internal const int MaxHours = DiagnosticsHealthRoutes.MaxTemperatureHours;

    private readonly IMetricsHistoryStore _store;
    private readonly IScreenTimeStore _screenTime;

    public GetTemperatureHistoryTool(IMetricsHistoryStore store, IScreenTimeStore screenTime)
    {
        _store = store;
        _screenTime = screenTime;
    }

    public string Name => "get_temperature_history";
    public string Title => "Temperature History";

    public string Description =>
        "Returns temperature history for one component or every component, bucketed at a width that " +
        "widens as the requested window grows, over a window in hours (default " +
        $"{DefaultHours}, capped at {MaxHours / 24} days) or a single calendar 'date'. Each bucket " +
        "carries the average and peak temperature plus the dominant foreground app running at that " +
        "time, for correlating heat with what was running. Call get_incidents to check whether a hot " +
        "stretch lines up with a crash.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"id\":{\"type\":\"string\",\"description\":\"Component id: cpu, gpu:<id>, storage:<serial>, ram:<id>. Omit to return every component.\"}," +
        "\"hours\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"How far back to look, in hours. " +
        $"Default {DefaultHours}, capped at {MaxHours}. Ignored when 'date' is given." +
        "\"}," +
        "\"date\":{\"type\":\"string\",\"description\":\"yyyy-MM-dd. Returns that single calendar day instead of 'hours'.\"}" +
        "},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var id = McpArgs.StringArg(args, "id");
        var dateArg = McpArgs.StringArg(args, "date");

        long fromUtcMs;
        long toUtcMs;
        int tierWidthMinutes;
        if (!string.IsNullOrWhiteSpace(dateArg))
        {
            if (!TemperatureDayWindow.TryResolve(
                    dateArg, DateTimeOffset.UtcNow, TimeZoneInfo.Local, TemperatureInsights.RetentionDays,
                    out fromUtcMs, out toUtcMs, out var dateError))
            {
                return Task.FromResult(McpToolExecutionResult.Error(dateError!));
            }
            tierWidthMinutes = TemperatureInsights.TierWidthMinutesFor(24);
        }
        else
        {
            var requestedHours = McpArgs.IntArg(args, "hours");
            var hours = requestedHours is { } h && h > 0 ? Math.Min(h, MaxHours) : DefaultHours;
            toUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            fromUtcMs = toUtcMs - hours * 3_600_000L;
            tierWidthMinutes = TemperatureInsights.TierWidthMinutesFor(hours);
        }

        var rows = _store.QueryTemperatureBuckets(fromUtcMs, toUtcMs);
        if (!string.IsNullOrEmpty(id))
        {
            var known = rows.Select(r => r.ComponentId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!known.Contains(id, StringComparer.Ordinal))
            {
                var list = known.Count == 0 ? "(none reporting in this window)" : string.Join(", ", known);
                return Task.FromResult(McpToolExecutionResult.Error($"Unknown component id '{id}'. Reporting components: {list}."));
            }
            rows = rows.Where(r => r.ComponentId == id).ToList();
        }

        var widthMs = tierWidthMinutes * 60_000L;
        var appByBucket = BuildDominantAppOverlay(fromUtcMs, toUtcMs, widthMs);

        var series = rows
            .GroupBy(r => r.ComponentId)
            .Select(g =>
            {
                var ordered = g.OrderBy(r => r.BucketUtcMs).ToList();
                var last = ordered[^1];
                var merged = TemperatureInsights.MergeToWidth(ordered, widthMs);
                var decimated = TemperatureInsights.Decimate(merged, DiagnosticsHealthRoutes.MaxPointsPerSeries);
                return new McpTemperatureSeries
                {
                    Id = g.Key,
                    Kind = last.Kind,
                    Name = last.Name,
                    Points = decimated.Select(p => new McpTemperaturePoint
                    {
                        T = p.T,
                        Avg = Math.Round(p.Avg, 1),
                        Max = Math.Round(p.Max, 1),
                        DominantApp = appByBucket.GetValueOrDefault(p.T),
                    }).ToList(),
                };
            })
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        var result = new McpTemperatureHistoryResult { BucketMinutes = tierWidthMinutes, Series = series };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpTemperatureHistoryResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    private Dictionary<long, string?> BuildDominantAppOverlay(long fromUtcMs, long toUtcMs, long widthMs)
    {
        try
        {
            var sessions = _screenTime.QuerySessions(fromUtcMs, toUtcMs);
            var buckets = ScreenTimeUsage.Build(sessions, fromUtcMs, toUtcMs, widthMs);
            return buckets.ToDictionary(b => b.StartUtcMs, b => b.Apps.Count > 0 ? (string?)b.Apps[0].AppName : null);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[mcp-temperature-history] app overlay failed: {ex.Message}");
            return new Dictionary<long, string?>();
        }
    }
}
