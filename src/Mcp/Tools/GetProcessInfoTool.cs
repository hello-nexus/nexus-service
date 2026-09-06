using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: file, version, and signature detail for a
/// process, plus how long Nexus has seen it running.</summary>
public sealed class GetProcessInfoTool : IMcpTool
{
    private readonly ProcessMonitor _processes;
    private readonly IProcessDetailProvider _detail;
    private readonly ProcessFirstSeenCache _firstSeen;

    public GetProcessInfoTool(ProcessMonitor processes, IProcessDetailProvider detail, ProcessFirstSeenCache firstSeen)
    {
        _processes = processes;
        _detail = detail;
        _firstSeen = firstSeen;
    }

    public string Name => "get_process_info";
    public string Title => "Process Info";

    public string Description =>
        "Returns file, version, and Authenticode signature detail for a process by exact name: path, " +
        "instance count, started at, description, version, company, publisher, signed, sha256, " +
        "created/modified times, and first seen. Call get_top_apps first to get the exact process name.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"name\":{\"type\":\"string\",\"description\":\"Process name from get_top_apps.\"}" +
        "},\"required\":[\"name\"],\"additionalProperties\":false}";

    public async Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var name = McpArgs.StringArg(args, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return McpToolExecutionResult.Error("'name' is required. Call get_top_apps first to get the exact process name.");
        }

        var response = await ProcessDetailRoutes.ResolveProcessInfoAsync(name, _processes, _detail, _firstSeen, ct).ConfigureAwait(false);
        if (response is null)
        {
            return McpToolExecutionResult.Error(
                $"No running or recently seen process named '{name}'. Call get_top_apps first to get the exact process name.");
        }

        var json = JsonSerializer.Serialize(response, AppJsonContext.Default.ProcessInfoResponse);
        return McpToolExecutionResult.Ok(json);
    }
}
