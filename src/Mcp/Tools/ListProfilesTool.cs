using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: every saved settings profile with the active one marked.</summary>
public sealed class ListProfilesTool : IMcpTool
{
    private readonly ProfileManager _profiles;

    public ListProfilesTool(ProfileManager profiles) => _profiles = profiles;

    public string Name => "list_profiles";
    public string Title => "List Profiles";

    public string Description =>
        "Lists every saved settings profile (id, name, and whether it is currently active) - " +
        "read-only, so it works even when profile switching is turned off. Call this before " +
        "apply_profile when the user names a profile you have not confirmed exists.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var manifest = _profiles.GetManifest();
        var result = new McpProfileListResult
        {
            Profiles = manifest.Profiles.Select(p => new McpProfileSummary
            {
                Id = p.Id,
                Name = p.Name,
                Active = p.Id == manifest.ActiveProfileId,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpProfileListResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
