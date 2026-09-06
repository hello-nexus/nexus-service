using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Write tool: starts a background fan calibration pass through the same
/// CalibrationRunner.Start call POST /cooling/calibrate uses. Calibration runs
/// asynchronously; this only reports whether it started.
/// </summary>
public sealed class CalibrateFansTool : IMcpTool
{
    private readonly IFanControlProvider _fans;
    private readonly CalibrationRunner _runner;
    private readonly FeatureGates _gates;

    public CalibrateFansTool(IFanControlProvider fans, CalibrationRunner runner, FeatureGates? gates = null)
    {
        _fans = fans;
        _runner = runner;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public string Name => "calibrate_fans";
    public string Title => "Calibrate Fans";

    public string Description =>
        "Starts a background calibration pass that learns each fan's min/max RPM and duty response " +
        "curve, either every fan or one channel. Runs asynchronously and errors if a calibration is " +
        "already running; call get_cooling_state once it finishes to see the results.";

    public McpCapability Capability => McpCapability.Cooling;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"fan\":{\"type\":\"string\",\"description\":\"Fan channel id from get_cooling_state to calibrate just one channel. Omit to calibrate every fan.\"}" +
        "},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        if (!_gates.Cooling)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Cooling is disabled in Settings."));
        }

        var fanArg = McpArgs.StringArg(args, "fan");
        var fanIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(fanArg))
        {
            var validIds = _fans.GetFanChannels().Select(c => c.Id).ToList();
            if (!validIds.Contains(fanArg, StringComparer.Ordinal))
            {
                var known = validIds.Count == 0 ? "(none detected)" : string.Join(", ", validIds);
                return Task.FromResult(McpToolExecutionResult.Error($"Unknown fan channel id '{fanArg}'. Known channel ids: {known}."));
            }
            fanIds.Add(fanArg);
        }

        var started = _runner.Start(_fans, fanIds);
        if (!started)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Calibration is already running. Call get_cooling_state to check progress."));
        }

        var result = new McpCalibrateFansResult
        {
            Started = true,
            Fan = fanArg,
            Message = "Calibration started. Call get_cooling_state once it finishes to see the results.",
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpCalibrateFansResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
