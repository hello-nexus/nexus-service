using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Conflicts;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only diagnostics tool: third-party RGB/fan/peripheral apps currently competing with Nexus for hardware control.</summary>
public sealed class GetConflictsTool : IMcpTool
{
    private readonly ConflictWatcher _watcher;

    public GetConflictsTool(ConflictWatcher watcher) => _watcher = watcher;

    public string Name => "get_conflicts";
    public string Title => "Hardware Control Conflicts";

    public string Description =>
        "Returns third-party RGB, fan-control, or peripheral apps currently running that compete with " +
        "Nexus for the same hardware (id, display name, category, and what it conflicts with), plus " +
        "AnyConflict as a quick yes/no. Call this when lighting or fan changes are not taking effect, " +
        "or a device is missing/behaving oddly, before assuming a Nexus bug.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var items = _watcher.GetConflicts().Select(c => new McpConflictItem
        {
            Id = c.Id,
            DisplayName = c.DisplayName,
            Category = c.Category,
            ConflictsWith = DescribeConflict(c.Category),
        }).ToList();

        // A loaded Chroma SDK DLL is distinct from ConflictAppCatalog's razer-synapse entry (a running process); GetState() no-ops off Windows so this is safe unconditionally.
        if (GameSyncShimInstaller.GetState().SynapseConflict)
        {
            items.Add(new McpConflictItem
            {
                Id = "razer-chroma-shim",
                DisplayName = "Razer Synapse (Chroma SDK)",
                Category = "lighting",
                ConflictsWith = "Nexus Game Sync's Chroma capture shim - a real Razer Chroma SDK DLL occupies at least one shim slot Nexus needs.",
            });
        }

        var result = new McpConflictsResult { AnyConflict = items.Count > 0, Conflicts = items };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpConflictsResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    private static string DescribeConflict(string category) => category switch
    {
        "lighting" => "RGB lighting control",
        "cooling" => "fan speed control",
        "peripherals" => "peripheral device control",
        "monitoring" => "sensor monitoring overlay",
        _ => "hardware control",
    };
}
