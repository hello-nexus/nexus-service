using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only telemetry tool: every registered panel device (HYTE Y70, Q-series, phones, promoted monitors, cooler LCDs).</summary>
public sealed class ListPanelsTool : IMcpTool
{
    private readonly PanelDeviceRegistry _registry;

    public ListPanelsTool(PanelDeviceRegistry registry) => _registry = registry;

    public string Name => "list_panels";
    public string Title => "List Panels";

    public string Description =>
        "Lists every panel screen Nexus drives (a HYTE Y70 case screen, Q-series and cooler LCDs, paired " +
        "phones, monitors turned into panels): id, name, surface, widget count and active preset. " +
        "Read-only, so it works even when panel editing is turned off. Call this first to get the panel " +
        "id the other panel tools take.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var result = new McpPanelListResult();
        foreach (var record in _registry.List())
        {
            var layout = PanelToolSupport.EffectiveLayout(record);
            var presets = _registry.ListPresets(record.Id);
            result.Panels.Add(new McpPanelSummary
            {
                Id = record.Id,
                Name = record.DisplayName,
                Surface = PanelToolSupport.SurfaceOf(record),
                Family = record.Capabilities?.Family,
                Pages = layout.Pages.Count,
                Widgets = layout.Pages.Sum(p => p.Widgets.Count),
                ActivePreset = presets?.Presets.FirstOrDefault(p => p.Id == presets.ActiveId)?.Name,
                LastSeenAt = record.LastSeenAt,
            });
        }
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpPanelListResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
