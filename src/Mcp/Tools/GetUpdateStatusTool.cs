using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Serialization;
using Nexus.Service.Update;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only diagnostics tool: Nexus app update status backing GET /update/status, plus per-device firmware status backing GET /devices/firmware/status.</summary>
public sealed class GetUpdateStatusTool : IMcpTool
{
    private readonly UpdateService _update;
    private readonly DeviceManager _devices;
    private readonly BundledFirmwareCatalog _catalog;

    public GetUpdateStatusTool(UpdateService update, DeviceManager devices, BundledFirmwareCatalog catalog)
    {
        _update = update;
        _devices = devices;
        _catalog = catalog;
    }

    public string Name => "get_update_status";
    public string Title => "Update Status";

    public string Description =>
        "Returns the Nexus app's own update state (current and available version, whether an update " +
        "is available, the last check error if any, and the update mode) plus firmware status for " +
        "every connected device that has a bundled firmware image (device, current version, available " +
        "version, whether an update is available). Call this when asked whether Nexus or a device's " +
        "firmware is up to date.";

    public McpCapability Capability => McpCapability.Telemetry;
    public bool ReadOnly => true;
    public string InputSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var status = _update.Status;
        var devices = new List<McpFirmwareStatus>();
        foreach (var d in _devices.GetAll())
        {
            if (!d.Connected)
            {
                continue;
            }

            var available = _catalog.GetLatestVersion(d.FirmwareType);
            if (string.IsNullOrEmpty(available))
            {
                continue;
            }

            devices.Add(new McpFirmwareStatus
            {
                Device = d.Name,
                CurrentVersion = d.FirmwareVersion,
                AvailableVersion = available,
                UpdateAvailable = BundledFirmwareCatalog.IsNewer(available, d.FirmwareVersion),
            });
        }

        var result = new McpUpdateStatusResult
        {
            CurrentVersion = status.CurrentVersion,
            AvailableVersion = status.LatestVersion,
            UpdateAvailable = status.UpdateAvailable,
            LastCheckError = status.LastCheckError,
            UpdateMode = status.UpdateMode,
            Devices = devices,
        };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpUpdateStatusResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
