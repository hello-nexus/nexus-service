using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only diagnostics tool: per-drive SMART health backing GET /diagnostics/smart.</summary>
public sealed class GetStorageHealthTool : IMcpTool
{
    private readonly SmartHealthMonitor _smart;

    public GetStorageHealthTool(SmartHealthMonitor smart) => _smart = smart;

    public string Name => "get_storage_health";
    public string Title => "Storage Health";

    public string Description =>
        "Returns per-drive SMART health: temperature, power-on hours, power cycles, a good/caution/" +
        "warning/bad status with reasons, flagged attributes, and for NVMe drives a wear/error block. " +
        "WearPercentUsed is rated write endurance consumed (0 is new, 100+ means fully worn) - it is " +
        "the opposite direction from HealthPercent (vendor life remaining, higher is healthier); do " +
        "not read WearPercentUsed as a remaining-life figure. Call get_health first to see if any " +
        "drive needs attention; call this for the SMART detail behind that verdict, or get_sensors " +
        "(device=storage) for the complete raw sensor list.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"refresh\":{\"type\":\"boolean\",\"description\":\"Force a fresh SMART poll instead of using the cached snapshot (cached for up to 10 minutes).\"}," +
        "\"drive\":{\"type\":\"string\",\"description\":\"Drive id from a prior get_storage_health or get_sensors(device=storage) call. Omit to return every drive.\"}" +
        "},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        if (McpArgs.BoolArg(args, "refresh"))
        {
            _smart.ForceRefresh();
        }

        var snapshot = _smart.Snapshot();
        var driveId = McpArgs.StringArg(args, "drive");

        IEnumerable<SmartDriveInfo> drives = snapshot.Drives;
        if (!string.IsNullOrEmpty(driveId))
        {
            var match = snapshot.Drives.FirstOrDefault(d => d.Id == driveId);
            if (match is null)
            {
                var known = string.Join(", ", snapshot.Drives.Select(d => d.Id));
                return Task.FromResult(McpToolExecutionResult.Error(
                    $"Unknown drive '{driveId}'. Known drives: {(known.Length == 0 ? "(none detected)" : known)}"));
            }
            drives = new[] { match };
        }

        var result = new McpStorageHealthResult
        {
            Supported = snapshot.Supported,
            Drives = drives.Select(MapDrive).ToList(),
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpStorageHealthResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }

    private static McpStorageDrive MapDrive(SmartDriveInfo d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Bus = d.Bus,
        SizeBytes = d.SizeBytes,
        TemperatureC = d.TemperatureC,
        PowerOnHours = d.PowerOnHours,
        PowerCycles = d.PowerCycles,
        HealthPercent = d.HealthPercent,
        Status = d.Status,
        StatusReasons = new List<string>(d.StatusReasons),
        Attributes = new List<SmartAttributeWire>(d.Attributes),
        Nvme = d.Nvme is null ? null : new McpNvmeHealth
        {
            CriticalWarning = d.Nvme.CriticalWarning,
            AvailableSpare = d.Nvme.AvailableSpare,
            SpareThreshold = d.Nvme.SpareThreshold,
            WearPercentUsed = d.Nvme.PercentageUsed,
            MediaErrors = d.Nvme.MediaErrors,
            ErrorLogEntries = d.Nvme.ErrorLogEntries,
            UnsafeShutdowns = d.Nvme.UnsafeShutdowns,
            DataReadBytes = d.Nvme.DataUnitsReadBytes,
            DataWrittenBytes = d.Nvme.DataUnitsWrittenBytes,
        },
    };
}
