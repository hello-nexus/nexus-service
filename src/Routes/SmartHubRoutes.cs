using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Hyte.SmartHub;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>
/// HYTE SmartHub device-specific endpoints. The singleton <see cref="SmartHubHub"/>
/// holds live state; routes are thin read-throughs so the UI doesn't have to
/// subscribe to the full cooling broadcast to see hub-only fields like the
/// firmware version or the flash-persisted standalone setting.
/// </summary>
public static partial class DevicesRoutes
{
    private static void MapSmartHubEndpoints(WebApplication app)
    {
        // Singleton-style endpoint - nexus currently supports at most one SmartHub.
        app.MapGet("/devices/smarthub", (SmartHubHub hub, IConfigStore store) =>
        {
            var fans = new SmartHubFanResponse[hub.State.Fans.Length];
            for (var i = 0; i < fans.Length; i++)
            {
                var fan = hub.State.Fans[i];
                fans[i] = new SmartHubFanResponse
                {
                    Index = fan.Index,
                    Rpm = fan.Rpm,
                    Duty = fan.Duty,
                    SeenFan = fan.SeenFan,
                };
            }
            return Results.Ok(new SmartHubStateResponse
            {
                Connected = hub.IsConnected,
                DeviceId = hub.DeviceId,
                FirmwareVersion = hub.State.FirmwareVersion,
                Serial = hub.State.Serial,
                Fans = fans,
            });
        });

        // Read the flash-persisted standalone setting (LED animation + colour
        // + brightness + the fan duty the watchdog holds when no host is
        // driving). What the hub does when nexus isn't streaming.
        app.MapGet("/devices/smarthub/fw-setting", (SmartHubHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "SmartHub not connected" });
            if (!hub.ReadMcuSetting(out var s))
                return Results.Problem("Failed to read firmware setting from SmartHub.");
            return Results.Ok(new SmartHubFwSettingResponse
            {
                Animation = s.Animation,
                R = s.R,
                G = s.G,
                B = s.B,
                Brightness = s.Brightness,
                FanPercent = s.FanPercent,
            });
        });

        // Persist a new standalone setting to flash (FF CC 0C writes both the
        // flash defaults and the live MCU state, so strips/fans reflect it
        // immediately).
        app.MapPut("/devices/smarthub/fw-setting", (SmartHubFwSettingRequest body, SmartHubHub hub) =>
        {
            if (!hub.IsConnected)
                return Results.Conflict(new { error = "SmartHub not connected" });
            if (body.Animation < SmartHubProtocol.McuAnimationColor
                || body.Animation > SmartHubProtocol.McuAnimationRainbowGradient)
            {
                return Results.BadRequest(new { error = "animation must be 1 (Color), 2 (Rainbow), 3 (Breathe), or 4 (Rainbow Gradient)" });
            }
            var ok = hub.WriteMcuSetting(
                body.Animation,
                (byte)Math.Clamp(body.R, 0, 255),
                (byte)Math.Clamp(body.G, 0, 255),
                (byte)Math.Clamp(body.B, 0, 255),
                Math.Clamp(body.Brightness, 0, 100),
                Math.Clamp(body.FanPercent, 0, 100));
            if (!ok)
                return Results.Problem("Failed to write firmware setting to SmartHub.");
            return Results.Ok(ApiResponse.Ok());
        });

        // GET /devices/smarthub/composition - mirror state (no rings axis, ports fixed).
        app.MapGet("/devices/smarthub/composition", (SmartHubHub hub, IConfigStore store) =>
        {
            return Results.Ok(new SmartHubCompositionResponse
            {
                Connected = hub.IsConnected,
                Mirror = SmartHubLightingDeviceProvider.ReadMirror(store.Load(), hub.DeviceId),
                PortCount = SmartHubProtocol.ArgbPortCount,
            });
        });

        // PUT /devices/smarthub/composition - toggle mirror. Collapsing the four
        // ports into one device (or back) drops the per-zone state of whichever
        // ids disappear.
        app.MapPut("/devices/smarthub/composition", (
            SmartHubCompositionRequest body,
            SmartHubHub hub,
            IConfigStore store,
            SmartHubLightingDeviceProvider lighting,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var hubId = hub.DeviceId;
            if (!hub.IsConnected || string.IsNullOrEmpty(hubId))
                return Results.Conflict(new { error = "SmartHub not connected" });

            var oldIds = SmartHubZoneIds(store.Load(), hub);
            store.Update(s =>
            {
                var cur = s.Devices.LightingComposition.TryGetValue(hubId, out var c) && c is not null
                    ? c
                    : new HubCompositionSettings();
                s.Devices.LightingComposition[hubId] = new HubCompositionSettings
                {
                    Mirror = body.Mirror ?? cur.Mirror,
                    CombineRings = false,
                };
                var newIds = new HashSet<string>(SmartHubZoneIds(s, hub));
                var orphaned = new List<string>();
                foreach (var id in oldIds)
                {
                    if (!newIds.Contains(id)) orphaned.Add(id);
                }
                ZoneStateDrop.Drop(s, orphaned);
            });

            lighting.OnHubStateUpdated();
            bridge?.RequestTopologyRefresh();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Ok(ApiResponse.Ok());
        });
    }

    private static List<string> SmartHubZoneIds(NexusSettings settings, SmartHubHub hub)
    {
        var hubId = hub.DeviceId;
        var ids = new List<string>();
        if (SmartHubLightingDeviceProvider.ReadMirror(settings, hubId))
        {
            ids.Add(SmartHubLightingDeviceProvider.MirrorId(hubId));
        }
        else
        {
            foreach (var port in hub.State.Ports)
            {
                ids.Add($"{hubId}:port{port.Channel}");
            }
        }
        return ids;
    }
}

/// <summary>Shape returned by /devices/smarthub. AOT-registered in <see cref="Serialization.AppJsonContext"/>.</summary>
public sealed class SmartHubStateResponse
{
    public bool Connected { get; set; }
    public string DeviceId { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public string Serial { get; set; } = "";
    public SmartHubFanResponse[] Fans { get; set; } = Array.Empty<SmartHubFanResponse>();
}

/// <summary>One PWM-fan port in <see cref="SmartHubStateResponse"/>.</summary>
public sealed class SmartHubFanResponse
{
    public int Index { get; set; }
    public int Rpm { get; set; }
    public int Duty { get; set; }
    public bool SeenFan { get; set; }
}

/// <summary>Shape returned by GET /devices/smarthub/fw-setting.</summary>
public sealed class SmartHubFwSettingResponse
{
    /// <summary>1=Color, 2=Rainbow, 3=Breathe, 4=Rainbow Gradient.</summary>
    public int Animation { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public int Brightness { get; set; }
    public int FanPercent { get; set; }
}

/// <summary>Body shape for PUT /devices/smarthub/fw-setting.</summary>
public sealed class SmartHubFwSettingRequest
{
    public int Animation { get; set; }
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public int Brightness { get; set; }
    public int FanPercent { get; set; }
}

/// <summary>Shape returned by GET /devices/smarthub/composition.</summary>
public sealed class SmartHubCompositionResponse
{
    public bool Connected { get; set; }
    public bool Mirror { get; set; }
    public int PortCount { get; set; }
}

/// <summary>Body for PUT /devices/smarthub/composition.</summary>
public sealed class SmartHubCompositionRequest
{
    public bool? Mirror { get; set; }
}
