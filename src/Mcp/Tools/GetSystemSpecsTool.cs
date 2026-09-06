using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only diagnostics tool: the shareable rig identity backing GET /system/specs, plus boot time and uptime.</summary>
public sealed class GetSystemSpecsTool : IMcpTool
{
    private readonly SystemSpecsCollector _specs;

    public GetSystemSpecsTool(SystemSpecsCollector specs) => _specs = specs;

    public string Name => "get_system_specs";
    public string Title => "System Specs";

    public string Description =>
        "Returns the PC's hardware/software identity as display strings: name, OS build, CPU, " +
        "motherboard, memory, storage, GPU, monitor, sound card, network adapter, plus when the OS " +
        "booted and how long it has been up. Call this for 'what's my rig' or 'how long has this PC " +
        "been running' questions - it does not report live load or temperature, use " +
        "get_system_overview or get_sensors for that.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public async Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var specs = await _specs.GetAsync(ct).ConfigureAwait(false);
        var uptimeMs = Environment.TickCount64;

        var result = new McpSystemSpecsResult
        {
            PcName = specs.PcName,
            OsBuild = specs.OsBuild,
            Processor = specs.Processor,
            Motherboard = specs.Motherboard,
            Memory = specs.Memory,
            Storage = specs.Storage,
            GraphicsCard = specs.GraphicsCard,
            Monitor = specs.Monitor,
            SoundCard = specs.SoundCard,
            NetworkCard = specs.NetworkCard,
            BootTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - uptimeMs,
            UptimeSeconds = uptimeMs / 1000.0,
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpSystemSpecsResult);
        return McpToolExecutionResult.Ok(json);
    }
}
