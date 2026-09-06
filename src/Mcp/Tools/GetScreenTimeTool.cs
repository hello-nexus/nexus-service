using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: per-app focus time, in one of four modes
/// depending on which arguments are given.</summary>
public sealed class GetScreenTimeTool : IMcpTool
{
    // Screen time has no retention prune (see BinaryScreenTimeStore's doc), so
    // an unbounded from/to would walk every day segment ever recorded.
    internal const int MaxRangeDays = 366;

    private readonly IScreenTimeStore _store;
    private readonly IConfigStore _config;

    public GetScreenTimeTool(IScreenTimeStore store, IConfigStore config)
    {
        _store = store;
        _config = config;
    }

    public string Name => "get_screen_time";
    public string Title => "Screen Time";

    public string Description =>
        "Returns per-app focus time in one of four modes: 'date' alone for that day's per-app " +
        "breakdown, 'date' with 'hour' for that hour's per-app usage, 'from'+'to' for per-day totals " +
        "across a range, or 'app'+'from'+'to' for one app's daily history over a range. A from/to range " +
        $"cannot span more than {MaxRangeDays} days. trackingEnabled reflects the local screen-time " +
        "tracking switch.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"date\":{\"type\":\"string\",\"description\":\"yyyy-MM-dd. Alone: that day's per-app breakdown. With 'hour': that hour's per-app usage.\"}," +
        "\"hour\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":23,\"description\":\"Local hour, 0-23. Requires 'date'.\"}," +
        "\"from\":{\"type\":\"string\",\"description\":\"yyyy-MM-dd range start. With 'to' alone: per-day totals. With 'app': that app's daily history. Range capped at " +
        $"{MaxRangeDays}" +
        " days.\"}," +
        "\"to\":{\"type\":\"string\",\"description\":\"yyyy-MM-dd range end.\"}," +
        "\"app\":{\"type\":\"string\",\"description\":\"App name from get_top_apps. Requires 'from' and 'to'.\"}" +
        "},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var dateArg = McpArgs.StringArg(args, "date");
        var hourArg = McpArgs.IntArg(args, "hour");
        var fromArg = McpArgs.StringArg(args, "from");
        var toArg = McpArgs.StringArg(args, "to");
        var appArg = McpArgs.StringArg(args, "app");
        var trackingEnabled = _config.Load().ScreenTime?.TrackingEnabled ?? true;

        if (!string.IsNullOrEmpty(appArg))
        {
            if (string.IsNullOrEmpty(fromArg) || string.IsNullOrEmpty(toArg))
            {
                return Task.FromResult(McpToolExecutionResult.Error("'app' requires 'from' and 'to' (yyyy-MM-dd)."));
            }
            var rangeError = ValidateRange(fromArg, toArg, out var f, out var t);
            if (rangeError is not null)
            {
                return Task.FromResult(McpToolExecutionResult.Error(rangeError));
            }
            var history = _store.GetAppHistory(appArg, f, t);
            return Task.FromResult(Ok(new McpScreenTimeResult { TrackingEnabled = trackingEnabled, Mode = "app", App = history }));
        }

        if (!string.IsNullOrEmpty(dateArg))
        {
            if (!DateOnly.TryParse(dateArg, out var d))
            {
                return Task.FromResult(McpToolExecutionResult.Error("'date' must be a valid date (yyyy-MM-dd)."));
            }
            if (hourArg is { } hour)
            {
                if (hour < 0 || hour > 23)
                {
                    return Task.FromResult(McpToolExecutionResult.Error("'hour' must be between 0 and 23."));
                }
                var hourUsage = new List<AppUsage>(_store.GetHourUsage(d, hour));
                return Task.FromResult(Ok(new McpScreenTimeResult { TrackingEnabled = trackingEnabled, Mode = "hour", Hour = hourUsage }));
            }
            var day = _store.GetDay(d);
            return Task.FromResult(Ok(new McpScreenTimeResult { TrackingEnabled = trackingEnabled, Mode = "day", Day = day }));
        }

        if (!string.IsNullOrEmpty(fromArg) && !string.IsNullOrEmpty(toArg))
        {
            var rangeError = ValidateRange(fromArg, toArg, out var f, out var t);
            if (rangeError is not null)
            {
                return Task.FromResult(McpToolExecutionResult.Error(rangeError));
            }
            var range = new List<DayTotal>(_store.GetRange(f, t));
            return Task.FromResult(Ok(new McpScreenTimeResult { TrackingEnabled = trackingEnabled, Mode = "range", Range = range }));
        }

        return Task.FromResult(McpToolExecutionResult.Error(
            "Provide 'date' (optionally with 'hour'), 'from' and 'to', or 'app' with 'from' and 'to'."));
    }

    private static string? ValidateRange(string? fromArg, string? toArg, out DateOnly from, out DateOnly to)
    {
        from = default;
        to = default;
        if (!DateOnly.TryParse(fromArg, out from) || !DateOnly.TryParse(toArg, out to))
        {
            return "'from' and 'to' must be valid dates (yyyy-MM-dd).";
        }
        if (to.DayNumber - from.DayNumber > MaxRangeDays)
        {
            return $"'from' to 'to' cannot span more than {MaxRangeDays} days.";
        }
        return null;
    }

    private static McpToolExecutionResult Ok(McpScreenTimeResult result) =>
        McpToolExecutionResult.Ok(JsonSerializer.Serialize(result, AppJsonContext.Default.McpScreenTimeResult));
}
