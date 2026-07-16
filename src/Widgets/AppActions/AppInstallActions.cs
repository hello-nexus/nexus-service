using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// Generic host-actions for apps that declare a driver/install block.
/// Reads the caller's own app id from the injected "__appId" arg.
/// </summary>
public static class AppInstallActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("app.installStatus", async (services, args, ct) =>
        {
            var appId = AppActionHelpers.Str(args, "__appId") ?? "";
            var appRegistry = services.GetRequiredService<AppRegistry>();
            var toolManager = services.GetRequiredService<ExternalToolManager>();
            var adbRegistry = services.GetRequiredService<IAdbDeviceRegistry>();

            // A blocked driver is reported as having no install block at all, so the
            // app page offers no button for a path the service will refuse.
            if (!appRegistry.TryGet(appId, out var entry) || entry.Manifest.Driver is null
                || services.GetRequiredService<DriverExePolicy>().IsBlocked(entry.Manifest.Driver))
            {
                var empty = new AppInstallStatusDto { HasInstall = false, State = "notrunning" };
                var emptyJson = JsonSerializer.Serialize(empty, AppJsonContext.Default.AppInstallStatusDto);
                using var emptyDoc = JsonDocument.Parse(emptyJson);
                return emptyDoc.RootElement.Clone();
            }

            var driver = entry.Manifest.Driver;
            var isAndroid = string.Equals(driver.Target, "android-adb", StringComparison.OrdinalIgnoreCase);
            var targetStr = isAndroid ? "android-adb" : "host-exe";

            IAdbDeviceTarget? androidDevice = null;
            bool devicePresent;
            if (isAndroid)
            {
                if (!string.IsNullOrEmpty(driver.Package))
                {
                    androidDevice = adbRegistry.TryGet(driver.Package!);
                }
                devicePresent = androidDevice is not null;
            }
            else
            {
                var usb = services.GetRequiredService<IUsbEnumerator>();
                devicePresent = DriverToolSpecFactory.DevicePresent(driver, usb);
            }

            var variant = FirstValue(driver.Variants) ?? "default";
            var spec = DriverToolSpecFactory.Build(driver, variant, entry.RootPath);

            var toolStatus = toolManager.GetStatus(driver.ToolId, devicePresent);
            var stateStr = toolStatus switch
            {
                ToolStatus.Running => "running",
                ToolStatus.Failed => "failed",
                ToolStatus.NoDevice => "nodevice",
                _ => "notrunning",
            };

            ToolVersion? latestEntry = null;
            try
            {
                latestEntry = await toolManager.GetLatestAsync(spec, ct);
            }
            catch { /* manifest fetch failed; leave null */ }

            var latestVersion = latestEntry?.Version;

            // For android-adb, read installed versionName and versionCode from the device.
            string? installedVersion = null;
            int installedVersionCode = -1;
            if (isAndroid && androidDevice is not null && !string.IsNullOrEmpty(driver.Package))
            {
                try
                {
                    var dumpsys = await androidDevice.ShellAsync($"dumpsys package {driver.Package}", ct);
                    installedVersion = AdbHelpers.ParseVersionName(dumpsys);
                    installedVersionCode = AdbHelpers.ParseVersionCode(dumpsys);
                }
                catch { /* leave null/defaults */ }
            }

            // updateAvailable: installed on device and a newer versionCode exists in the manifest.
            bool updateAvailable = false;
            if (isAndroid)
            {
                updateAvailable = installedVersionCode >= 0
                    && latestEntry?.VersionCode is int publishedCode
                    && publishedCode > installedVersionCode;
            }
            else
            {
                // host-exe: no installed-version read; treat not-running + known-version as update available.
                updateAvailable = toolStatus != ToolStatus.Running && latestVersion is not null;
            }

            var dto = new AppInstallStatusDto
            {
                HasInstall = true,
                Target = targetStr,
                DevicePresent = devicePresent,
                Installed = isAndroid ? installedVersionCode >= 0 : toolStatus == ToolStatus.Running,
                InstalledVersion = installedVersion,
                LatestVersion = latestVersion,
                UpdateAvailable = updateAvailable,
                State = stateStr,
            };

            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.AppInstallStatusDto);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        });

        registry.Register("app.install", async (services, args, ct) =>
        {
            var appId = AppActionHelpers.Str(args, "__appId") ?? "";
            var appRegistry = services.GetRequiredService<AppRegistry>();
            var toolManager = services.GetRequiredService<ExternalToolManager>();

            if (!appRegistry.TryGet(appId, out var entry) || entry.Manifest.Driver is null)
            {
                var noBlock = new AppInstallTriggerDto { Started = false, Reason = "no-install-block" };
                var noBlockJson = JsonSerializer.Serialize(noBlock, AppJsonContext.Default.AppInstallTriggerDto);
                using var noBlockDoc = JsonDocument.Parse(noBlockJson);
                return noBlockDoc.RootElement.Clone();
            }

            var driver = entry.Manifest.Driver;

            if (services.GetRequiredService<DriverExePolicy>().IsBlocked(driver))
            {
                var blocked = new AppInstallTriggerDto { Started = false, Reason = "driver-exe-disabled" };
                var blockedJson = JsonSerializer.Serialize(blocked, AppJsonContext.Default.AppInstallTriggerDto);
                using var blockedDoc = JsonDocument.Parse(blockedJson);
                return blockedDoc.RootElement.Clone();
            }

            // The device's Nexus Control gate governs this driver too, and the
            // auto-launch worker would terminate anything started behind its back
            // within a tick - so refuse rather than start a process that is killed
            // seconds later while it holds the device.
            if (driver.DeviceId is not null
                && !services.GetRequiredService<DeviceControlGate>().IsEnabled(driver.DeviceId))
            {
                var gated = new AppInstallTriggerDto { Started = false, Reason = "nexus-control-off" };
                var gatedJson = JsonSerializer.Serialize(gated, AppJsonContext.Default.AppInstallTriggerDto);
                using var gatedDoc = JsonDocument.Parse(gatedJson);
                return gatedDoc.RootElement.Clone();
            }

            var variant = FirstValue(driver.Variants) ?? "default";
            var spec = DriverToolSpecFactory.Build(driver, variant, entry.RootPath);

            await toolManager.LaunchAsync(spec, ct);

            var dto = new AppInstallTriggerDto { Started = true, Reason = null };
            var dtoJson = JsonSerializer.Serialize(dto, AppJsonContext.Default.AppInstallTriggerDto);
            using var dtoDoc = JsonDocument.Parse(dtoJson);
            return dtoDoc.RootElement.Clone();
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "app.installStatus", "app.install" };

    private static string? FirstValue(Dictionary<string, string> dict)
    {
        foreach (var v in dict.Values)
        {
            return v;
        }
        return null;
    }
}
