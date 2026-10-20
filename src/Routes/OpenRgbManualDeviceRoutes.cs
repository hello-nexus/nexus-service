using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    /// <summary>
    /// Registrations for hardware the bundled daemon cannot detect on its own:
    /// QMK-OpenRGB keyboards (found only by the vid/pid they were flashed with)
    /// and E1.31 / WLED devices (a bare IP). Both are what OpenRGB's own GUI
    /// stores, so the import adopts an existing install's lists wholesale.
    ///
    /// Every mutation bounces the RGB subprocess: the daemon reads its config
    /// once at launch, so a registration is inert until it restarts.
    /// </summary>
    private static void MapOpenRgbManualDeviceEndpoints(WebApplication app)
    {
        app.MapGet("/devices/openrgb/zones", (OpenRgbLightingDeviceProvider provider) =>
        {
            var response = new OpenRgbZonesResponse { Devices = provider.GetSizableZones() };
            return Results.Json(response, AppJsonContext.Default.OpenRgbZonesResponse);
        });

        app.MapPost("/devices/openrgb/zones/size", (SetOpenRgbZoneSizeBody body, OpenRgbLightingDeviceProvider provider, Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            var error = provider.TrySetOpenRgbZoneSize(body.DeviceId, body.ZoneIndex, body.LedCount);
            if (error is not null)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapGet("/devices/openrgb/manual-devices", (IConfigStore store) =>
        {
            var devices = store.Load().Devices.OpenRgbManualDevices;
            var sourcePath = OpenRgbConfigImport.DefaultSourcePath();
            var response = new OpenRgbManualDevicesResponse
            {
                ImportSourcePath = sourcePath,
                ImportSourceAvailable = System.IO.File.Exists(sourcePath),
            };
            foreach (var e in devices.Qmk)
            {
                response.Qmk.Add(new QmkDeviceDto { Name = e.Name, UsbVid = e.UsbVid, UsbPid = e.UsbPid });
            }
            foreach (var e in devices.E131)
            {
                response.E131.Add(new E131DeviceDto
                {
                    Name = e.Name, Ip = e.Ip, NumLeds = e.NumLeds,
                    StartUniverse = e.StartUniverse, StartChannel = e.StartChannel,
                    KeepaliveTime = e.KeepaliveTime, UniverseSize = e.UniverseSize,
                });
            }
            return Results.Json(response, AppJsonContext.Default.OpenRgbManualDevicesResponse);
        });

        app.MapPost("/devices/openrgb/manual-devices/qmk", (AddQmkDeviceBody body, IConfigStore store, RgbBridge? bridge) =>
        {
            if (string.IsNullOrWhiteSpace(body.UsbVid) || string.IsNullOrWhiteSpace(body.UsbPid))
            {
                return Results.BadRequest(ApiResponse.Fail("usb_vid and usb_pid are required"));
            }
            var vid = OpenRgbManualDeviceConfig.NormalizeHex(body.UsbVid);
            var pid = OpenRgbManualDeviceConfig.NormalizeHex(body.UsbPid);
            if (!OpenRgbManualDeviceConfig.IsValidHexId(vid) || !OpenRgbManualDeviceConfig.IsValidHexId(pid))
            {
                return Results.BadRequest(ApiResponse.Fail("usb_vid and usb_pid must be hex, e.g. 3434 and 0660"));
            }
            var name = body.Name ?? "";
            var changed = false;
            store.Update(s =>
            {
                var list = s.Devices.OpenRgbManualDevices.Qmk;
                foreach (var e in list)
                {
                    if (string.Equals(e.UsbVid, vid, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.UsbPid, pid, StringComparison.OrdinalIgnoreCase))
                    {
                        changed = e.Name != name;
                        e.Name = name;
                        return;
                    }
                }
                list.Add(new QmkOpenRgbDeviceEntry { Name = name, UsbVid = vid, UsbPid = pid });
                changed = true;
            });
            if (changed) bridge?.BounceForManualDevices();
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/devices/openrgb/manual-devices/e131", (AddE131DeviceBody body, IConfigStore store, RgbBridge? bridge) =>
        {
            if (string.IsNullOrWhiteSpace(body.Ip))
            {
                return Results.BadRequest(ApiResponse.Fail("ip is required"));
            }
            var entry = OpenRgbManualDeviceConfig.Sanitize(new E131DeviceEntry
            {
                Name = body.Name ?? "", Ip = body.Ip, NumLeds = body.NumLeds,
                StartUniverse = body.StartUniverse, StartChannel = body.StartChannel,
                KeepaliveTime = body.KeepaliveTime, UniverseSize = body.UniverseSize,
            });
            var changed = false;
            store.Update(s =>
            {
                var list = s.Devices.OpenRgbManualDevices.E131;
                for (var i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (string.Equals(e.Ip, entry.Ip, StringComparison.OrdinalIgnoreCase) && e.StartUniverse == entry.StartUniverse)
                    {
                        changed = e.Name != entry.Name || e.NumLeds != entry.NumLeds
                            || e.StartChannel != entry.StartChannel || e.KeepaliveTime != entry.KeepaliveTime
                            || e.UniverseSize != entry.UniverseSize;
                        list[i] = entry;
                        return;
                    }
                }
                list.Add(entry);
                changed = true;
            });
            if (changed) bridge?.BounceForManualDevices();
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/devices/openrgb/manual-devices/remove", (RemoveManualDeviceBody body, IConfigStore store, RgbBridge? bridge) =>
        {
            var key = body.Key ?? "";
            var key2 = body.Key2 ?? "";
            var removed = 0;
            store.Update(s =>
            {
                var devices = s.Devices.OpenRgbManualDevices;
                if (string.Equals(body.Kind, "qmk", StringComparison.OrdinalIgnoreCase))
                {
                    var vid = OpenRgbManualDeviceConfig.NormalizeHex(key);
                    var pid = OpenRgbManualDeviceConfig.NormalizeHex(key2);
                    removed = devices.Qmk.RemoveAll(e =>
                        string.Equals(e.UsbVid, vid, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.UsbPid, pid, StringComparison.OrdinalIgnoreCase));
                }
                else if (string.Equals(body.Kind, "e131", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(key2, out var universe);
                    removed = devices.E131.RemoveAll(e =>
                        string.Equals(e.Ip, key, StringComparison.OrdinalIgnoreCase)
                        && (key2.Length == 0 || e.StartUniverse == universe));
                }
            });
            if (removed > 0) bridge?.BounceForManualDevices();
            return Results.Ok(ApiResponse.Ok());
        });

        app.MapPost("/devices/openrgb/manual-devices/import", (ImportOpenRgbConfigBody body, IConfigStore store, RgbBridge? bridge) =>
        {
            var path = OpenRgbConfigImport.DefaultSourcePath();
            if (!string.IsNullOrWhiteSpace(body.Path))
            {
                // An override picks a different OpenRGB install, not an
                // arbitrary file: the service runs elevated, so a free-form
                // path would read anything it is pointed at.
                if (!string.Equals(System.IO.Path.GetFileName(body.Path), OpenRgbConfigImport.SourceFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(ApiResponse.Fail($"path must end in {OpenRgbConfigImport.SourceFileName}"));
                }
                path = body.Path;
            }
            var imported = OpenRgbConfigImport.Read(path);
            var added = 0;
            if (imported.Found)
            {
                store.Update(s => added = OpenRgbConfigImport.Merge(s.Devices.OpenRgbManualDevices, imported));
                if (added > 0) bridge?.BounceForManualDevices();
            }
            return Results.Json(new ImportOpenRgbConfigResponse
            {
                SourceFound = imported.Found,
                Path = path,
                Added = added,
                QmkSeen = imported.Qmk.Count,
                E131Seen = imported.E131.Count,
            }, AppJsonContext.Default.ImportOpenRgbConfigResponse);
        });
    }

}
