using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapLianLiEndpoints(WebApplication app)
    {
        // GET /devices/lianli/state - connection, RPM, duty, and fan count per port.
        app.MapGet("/devices/lianli/state", (LianLiHub hub, IConfigStore store) =>
        {
            var s = store.Load();
            var state = hub.State;
            var resp = new LianLiStateResponse
            {
                IsConnected = hub.IsConnected,
                ModelName = hub.ModelName,
                Rpm = new[] { state.Rpm[0], state.Rpm[1], state.Rpm[2], state.Rpm[3] },
                Duty = new[] { state.Duty[0], state.Duty[1], state.Duty[2], state.Duty[3] },
                FansPerPort = new[]
                {
                    s.Devices.LianLi.Port0Fans,
                    s.Devices.LianLi.Port1Fans,
                    s.Devices.LianLi.Port2Fans,
                    s.Devices.LianLi.Port3Fans,
                },
            };
            return Results.Json(resp, AppJsonContext.Default.LianLiStateResponse);
        });

        // PUT /devices/lianli/fan-count - set fan count for one port (0..3), sends
        // SetQuantity to hub and persists the count so the lighting provider
        // allocates the correct LED frames.
        app.MapPut("/devices/lianli/fan-count", (
            LianLiFanCountRequest body,
            LianLiHub hub,
            IConfigStore store,
            LianLiLightingDeviceProvider lighting) =>
        {
            if (body.Port < 0 || body.Port >= LianLiProtocol.PortCount)
            {
                return Results.BadRequest(ApiResponse.Fail("port must be 0..3"));
            }
            if (body.Count < 0 || body.Count > 4)
            {
                return Results.BadRequest(ApiResponse.Fail("count must be 0..4"));
            }
            if (hub.IsConnected)
            {
                hub.SetQuantity(body.Port, body.Count);
            }
            store.Update(s => s.Devices.LianLi.SetFans(body.Port, body.Count));
            lighting.OnHubStateUpdated();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // GET /devices/lianli/composition - mirror/combine state + per-port active flags.
        app.MapGet("/devices/lianli/composition", (LianLiHub hub, IConfigStore store) =>
        {
            var s = store.Load();
            var hubId = hub.DeviceId;
            var comp = LianLiZoneSupport.ReadComposition(s, hubId);
            var active = new bool[LianLiProtocol.PortCount];
            var fans = new int[LianLiProtocol.PortCount];
            for (var p = 0; p < LianLiProtocol.PortCount; p++)
            {
                fans[p] = LianLiZoneSupport.ClampFans(s.Devices.LianLi.GetFans(p));
                active[p] = fans[p] > 0;
            }
            return Results.Json(new LianLiCompositionResponse
            {
                Connected = hub.IsConnected,
                Mirror = comp.Mirror,
                CombineRings = comp.CombineRings,
                PortCount = LianLiProtocol.PortCount,
                ActivePorts = active,
                FansPerPort = fans,
            }, AppJsonContext.Default.LianLiCompositionResponse);
        });

        // PUT /devices/lianli/composition - patch combine-rings (and, for back-
        // compat, per-port on-off mapping to fan count 0 vs max; Lian Li never
        // mirrors, so any mirror in the body is ignored). Recomposing the device
        // set drops the per-zone state of devices that disappear; a combine change
        // clears the affected devices' custom partitions so zone resolution uses
        // the new default zones (a stale partition desyncs zone ids from the cards).
        app.MapPut("/devices/lianli/composition", (
            LianLiCompositionRequest body,
            LianLiHub hub,
            IConfigStore store,
            LianLiLightingDeviceProvider lighting,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var hubId = hub.DeviceId;
            var profile = hub.Profile;
            var oldIds = LianLiZoneSupport.ZoneIds(store.Load(), hubId, profile);

            store.Update(s =>
            {
                var comp = LianLiZoneSupport.ReadComposition(s, hubId);
                var nextCombine = body.CombineRings ?? comp.CombineRings;
                var structural = nextCombine != comp.CombineRings;
                s.Devices.LightingComposition[hubId] = new HubCompositionSettings
                {
                    Mirror = false,
                    CombineRings = nextCombine,
                };
                if (body.Ports is not null)
                {
                    for (var p = 0; p < LianLiProtocol.PortCount && p < body.Ports.Length; p++)
                    {
                        var on = body.Ports[p];
                        var cur = LianLiZoneSupport.ClampFans(s.Devices.LianLi.GetFans(p));
                        if (on && cur == 0) s.Devices.LianLi.SetFans(p, LianLiProtocol.MaxFansPerPort);
                        else if (!on && cur > 0) s.Devices.LianLi.SetFans(p, 0);
                    }
                }

                var newIds = new HashSet<string>(LianLiZoneSupport.ZoneIds(s, hubId, profile));
                var orphaned = new List<string>();
                foreach (var id in oldIds)
                {
                    if (!newIds.Contains(id)) orphaned.Add(id);
                }
                ZoneStateDrop.Drop(s, orphaned);

                if (structural)
                {
                    ZoneStateDrop.Drop(s, oldIds);
                    foreach (var did in LianLiZoneSupport.DeviceIds(s, hubId, profile))
                    {
                        s.Devices.ZonePartitions.Remove(did);
                    }
                }
            });

            if (body.Ports is not null && hub.IsConnected)
            {
                var after = store.Load();
                for (var p = 0; p < LianLiProtocol.PortCount && p < body.Ports.Length; p++)
                {
                    hub.SetQuantity(p, LianLiZoneSupport.ClampFans(after.Devices.LianLi.GetFans(p)));
                }
            }

            lighting.OnHubStateUpdated();
            bridge?.RequestTopologyRefresh();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // GET /devices/lianli/lighting - current settings + the attached family's mode catalog.
        app.MapGet("/devices/lianli/lighting", (LianLiHub hub, IConfigStore store) =>
        {
            var s = store.Load();
            var ls = s.Devices.LianLiLighting;
            var profile = hub.Profile;
            var family = profile.Family;
            var composed = LianLiZoneSupport.Compose(hub.DeviceId, profile, LianLiZoneSupport.ReadComposition(s, hub.DeviceId), s.Devices.LianLi);
            var mergeBlocked = LianLiLightingFrameWriter.AnyDeviceExcluded(composed, s);
            var modes = LianLiLightingModes.CatalogFor(family);
            var catalog = new LianLiModeInfoDto[modes.Count];
            for (var i = 0; i < modes.Count; i++)
            {
                var m = modes[i];
                catalog[i] = new LianLiModeInfoDto
                {
                    Key = m.Key,
                    Label = m.Label,
                    HasSpeed = m.HasSpeed,
                    HasDirection = m.HasDirection,
                    HasBrightness = m.HasBrightness,
                    ColorsMin = m.ColorsMin,
                    ColorsMax = m.ColorsMax,
                    Mergeable = m.MergesOn(profile) && !mergeBlocked,
                };
            }
            // Report the mode the writer commits: a persisted key outside this
            // family's catalog falls back to static there too.
            var persisted = LianLiLightingModes.Find(ls.Mode);
            var effectiveMode = persisted != null && !persisted.SupportedBy(family) ? "static" : ls.Mode;
            var effect = LianLiLightingModes.Find(ls.Mode == "custom" ? ls.EffectMode ?? "rainbowWave" : ls.Mode);
            var effectMode = effect != null && effect.Key != "custom" && effect.SupportedBy(family) ? effect.Key : "static";
            return Results.Json(new LianLiLightingResponse
            {
                Mode = effectiveMode,
                EffectMode = effectMode,
                Speed = ls.Speed,
                Direction = ls.Direction,
                Brightness = ls.Brightness,
                Colors = ls.Colors.ToArray(),
                Merge = ls.Merge,
                Modes = catalog,
            }, AppJsonContext.Default.LianLiLightingResponse);
        });

        // PUT /devices/lianli/lighting - patch mode/speed/direction/brightness/colors.
        app.MapPut("/devices/lianli/lighting", (
            LianLiLightingRequest body,
            LianLiHub hub,
            IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            if (body.Mode != null)
            {
                var info = LianLiLightingModes.Find(body.Mode);
                if (info == null || !info.SupportedBy(hub.Profile.Family))
                {
                    return Results.BadRequest(ApiResponse.Fail("unknown mode key"));
                }
            }
            store.Update(s =>
            {
                var ls = s.Devices.LianLiLighting;
                if (body.Mode != null)
                {
                    ls.Mode = body.Mode;
                    if (body.Mode != "custom") ls.EffectMode = body.Mode;
                }
                if (body.Speed.HasValue) ls.Speed = Math.Clamp(body.Speed.Value, 0, 4);
                if (body.Direction.HasValue) ls.Direction = Math.Clamp(body.Direction.Value, 0, 1);
                if (body.Brightness.HasValue) ls.Brightness = Math.Clamp(body.Brightness.Value, 0, 4);
                if (body.Merge.HasValue) ls.Merge = body.Merge.Value;
                if (body.Colors != null)
                {
                    var modeKey = ls.Mode;
                    var modeInfo = LianLiLightingModes.Find(modeKey);
                    var maxColors = modeInfo?.ColorsMax ?? 0;
                    var count = Math.Min(body.Colors.Length, maxColors);
                    ls.Colors = new List<string>(count);
                    for (var i = 0; i < count; i++)
                    {
                        ls.Colors.Add(body.Colors[i]);
                    }
                }
            });
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

    }
}

public sealed class LianLiStateResponse
{
    public bool IsConnected { get; set; }
    public string ModelName { get; set; } = "";
    public int[] Rpm { get; set; } = Array.Empty<int>();
    public int[] Duty { get; set; } = Array.Empty<int>();
    public int[] FansPerPort { get; set; } = Array.Empty<int>();
}

public sealed class LianLiFanCountRequest
{
    public int Port { get; set; }
    public int Count { get; set; }
}

/// <summary>Shape returned by GET /devices/lianli/composition.</summary>
public sealed class LianLiCompositionResponse
{
    public bool Connected { get; set; }
    public bool Mirror { get; set; }
    public bool CombineRings { get; set; }
    public int PortCount { get; set; }
    public bool[] ActivePorts { get; set; } = Array.Empty<bool>();
    public int[] FansPerPort { get; set; } = Array.Empty<int>();
}

/// <summary>Body for PUT /devices/lianli/composition; each field is a patch (null = unchanged).</summary>
public sealed class LianLiCompositionRequest
{
    public bool? CombineRings { get; set; }
    /// <summary>Per-port on/off; index = port. True restores fans to max, false sets 0.</summary>
    public bool[]? Ports { get; set; }
}

public sealed class LianLiModeInfoDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool HasSpeed { get; set; }
    public bool HasDirection { get; set; }
    public bool HasBrightness { get; set; }
    public int ColorsMin { get; set; }
    public int ColorsMax { get; set; }
    public bool Mergeable { get; set; }
}

public sealed class LianLiLightingResponse
{
    public string Mode { get; set; } = "";
    /// <summary>The mode the device returns to when Lighting page control is turned off.</summary>
    public string EffectMode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    public bool Merge { get; set; }
    public LianLiModeInfoDto[] Modes { get; set; } = Array.Empty<LianLiModeInfoDto>();
}

public sealed class LianLiLightingRequest
{
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
    public bool? Merge { get; set; }
}

