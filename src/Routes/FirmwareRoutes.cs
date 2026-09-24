using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Firmware Updates endpoints. v1 is read-only: it reports, per connected
/// supported device, the version the device is running vs the newest version
/// bundled in this build (<see cref="BundledFirmwareCatalog"/>). The flash
/// action lands with the dfu-util flasher.
/// </summary>
public static partial class DevicesRoutes
{
    private static void MapFirmwareEndpoints(WebApplication app)
    {
        // Only devices that are (a) connected and (b) have a bundled firmware
        // image are returned - no blank rows for devices we can't offer an
        // update for. CurrentVersion may still be empty when the device hasn't
        // reported its version yet; UpdateAvailable stays false in that case.
        app.MapGet("/devices/firmware/status", async (
            DeviceManager dm,
            BundledFirmwareCatalog catalog,
            FirmwareFlasher flasher,
            ApkFlasher apkFlasher,
            IAdbDeviceRegistry adbRegistry,
            CancellationToken ct) =>
        {
            var result = new List<FirmwareStatusItem>();
            foreach (var d in dm.GetAll())
            {
                if (!d.Connected) continue;

                // FirmwareType is the catalog key: Id for most devices, the
                // connected variant ("q60"/"q80") for Q-series.
                var available = catalog.GetLatestVersion(d.FirmwareType);
                if (string.IsNullOrEmpty(available)) continue;

                result.Add(new FirmwareStatusItem
                {
                    DeviceType = d.Id,
                    FirmwareType = d.FirmwareType,
                    Name = d.Name,
                    Category = d.Category,
                    CurrentVersion = d.FirmwareVersion,
                    AvailableVersion = available,
                    UpdateAvailable = BundledFirmwareCatalog.IsNewer(available, d.FirmwareVersion),
                    AvailableVersions = catalog.GetAvailableVersions(d.FirmwareType).ToList(),
#if DEV_TOOLS
                    // Cross-branch / downgrade images for the dev-only picker.
                    // Absent from release builds so the UI can't offer them.
                    DevImages = flasher.FlashableImages(d.FirmwareType).ToList(),
#else
                    DevImages = new List<FlashableImage>(),
#endif
                });
            }

            // Panel app entry: qshell APK on the connected Q-series display.
            var panelDevice = adbRegistry.TryGet("com.hellonexus.qshell");
            if (panelDevice is not null)
            {
                var qhandler = dm.GetAll().FirstOrDefault(d => d.Id == "qseries");
                var panelName = qhandler?.FirmwareType switch
                {
                    QSeriesCoolerProtocol.VariantQ60 => "Q60 Panel App",
                    QSeriesCoolerProtocol.VariantQ80 => "Q80 Panel App",
                    _ => "Q-series Panel App",
                };
                try
                {
                    var dumpsys = await panelDevice.ShellAsync("dumpsys package com.hellonexus.qshell", ct);
                    var currentVersion = AdbHelpers.ParseVersionName(dumpsys) ?? "";
                    var installedCode = AdbHelpers.ParseVersionCode(dumpsys);
                    var latestEntry = await apkFlasher.GetLatestCachedAsync(ct);
                    var availableVersion = latestEntry?.Version ?? "";
                    // ExternalToolManager swallows every fetch failure to null, so a null
                    // entry means "could not check", not "nothing newer" - the two need
                    // different UI, since only the first leaves no version to install.
                    var availableUnknown = latestEntry is null;
                    // installedCode is -1 when qshell is absent (a 2.0->3.0 panel still on the
                    // OEM launcher): offer the first install. Otherwise gate on a newer build.
                    var updateAvailable = latestEntry?.VersionCode is int pub && (installedCode < 0 || pub > installedCode);
                    result.Add(new FirmwareStatusItem
                    {
                        DeviceType = ApkFlasher.DeviceTypeKey,
                        FirmwareType = ApkFlasher.DeviceTypeKey,
                        Name = panelName,
                        Category = "display",
                        CurrentVersion = currentVersion,
                        AvailableVersion = availableVersion,
                        UpdateAvailable = updateAvailable,
                        AvailableUnknown = availableUnknown,
                        AvailableVersions = string.IsNullOrEmpty(availableVersion) ? new() : new() { availableVersion },
                        DevImages = new(),
                    });
                }
                catch
                {
                    // The manifest fetch reports failure as null, not an exception, so
                    // reaching here means the device read (or cancellation) failed: omit
                    // the entry and let the page report the panel as disconnected.
                }
            }

            return result;
        });

        // Start a flash (async). Body: { deviceType: <catalog key>, version }.
        // deviceType is the firmware-catalog key (the connected variant), i.e.
        // FirmwareStatusItem.FirmwareType - NOT the display id.
        app.MapPost("/devices/firmware/flash", (FlashRequest body, FirmwareFlasher flasher, ApkFlasher apkFlasher) =>
        {
            if (body.DeviceType == ApkFlasher.DeviceTypeKey)
            {
                if (apkFlasher.TryStart(body.DeviceType, body.Version, out var apkError))
                {
                    return Results.Json(new FlashStartResponse { Started = true }, AppJsonContext.Default.FlashStartResponse);
                }
                return Results.Json(new FlashStartResponse { Error = true, Msg = apkError, Started = false },
                    AppJsonContext.Default.FlashStartResponse, statusCode: StatusCodes.Status409Conflict);
            }

            if (flasher.TryStart(body.DeviceType, body.Version, out var error))
            {
                return Results.Json(new FlashStartResponse { Started = true }, AppJsonContext.Default.FlashStartResponse);
            }
            return Results.Json(new FlashStartResponse { Error = true, Msg = error, Started = false },
                AppJsonContext.Default.FlashStartResponse, statusCode: StatusCodes.Status409Conflict);
        });

        // Poll flash progress. Global server-side state, so it survives the UI
        // navigating between tabs. Both FirmwareFlasher and ApkFlasher write
        // to the same FlashGate.Status object, so this endpoint covers both.
        app.MapGet("/devices/firmware/flash/status", (FirmwareFlasher flasher) => flasher.Status);
    }
}
