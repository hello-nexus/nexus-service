using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Conflicts;
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
            ConflictsWith = c.IsSetting ? "a Windows setting (not an app) that drives the same lighting devices" : DescribeConflict(c.Category),
        }).ToList();

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
