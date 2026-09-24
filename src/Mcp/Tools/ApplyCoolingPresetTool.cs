using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: applies a built-in cooling preset to every unlocked fan channel.</summary>
public sealed class ApplyCoolingPresetTool : IMcpTool
{
    // Matches the day-one contract enum. FanProfiles.GetBuiltInProfiles also
    // lists "custom", which is a derived state, not something this tool applies.
    private static readonly string[] ValidPresets = { "off", "silent", "balanced", "turbo", "max" };

    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly FeatureGates _gates;

    public ApplyCoolingPresetTool(IFanControlProvider fans, IConfigStore store, MultiplexHub hub, FeatureGates? gates = null)
    {
        _fans = fans;
        _store = store;
        _hub = hub;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public string Name => "apply_cooling_preset";
    public string Title => "Apply Cooling Preset";

    public string Description =>
        "Applies a built-in cooling preset (off, silent, balanced, turbo, max) to every unlocked fan " +
        "channel. Use when the user asks to make the PC quiet, cool it down, release fans to hardware " +
        "control, or switch to a named cooling mode. Locked fans and fans on their own custom curve " +
        "are left alone.";

    public McpCapability Capability => McpCapability.Cooling;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"preset\":{\"type\":\"string\",\"enum\":[\"off\",\"silent\",\"balanced\",\"turbo\",\"max\"]}" +
        "},\"required\":[\"preset\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        if (!_gates.Cooling)
        {
            return Task.FromResult(McpToolExecutionResult.Error("Cooling is disabled in Settings."));
        }
        var preset = McpArgs.StringArg(args, "preset");
        if (string.IsNullOrEmpty(preset) || !ValidPresets.Contains(preset, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                $"'preset' must be one of: {string.Join(", ", ValidPresets)}."));
        }

        var applied = FanProfiles.Apply(preset, _fans, _store);
        PanelTopics.BroadcastCooling(_hub);

        var result = new McpApplyCoolingPresetResult { Applied = applied };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpApplyCoolingPresetResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
