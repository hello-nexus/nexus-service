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
        app.MapGet("/devices/lianli/state", (LianLiHubSet hubs, IConfigStore store, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "hub")] string? hubQuery) =>
        {
            var hub = ResolveHub(hubs, hubQuery);
            if (hub is null)
            {
                return Results.NotFound(ApiResponse.Fail("unknown hub"));
            }
            var s = store.Load();
            var state = hub.State;
            var resp = new LianLiStateResponse
            {
                IsConnected = hub.IsConnected,
                ModelName = hub.ModelName,
                MaxFansPerPort = hub.Profile.MaxFans,
                FirmwareVersion = state.FirmwareVersion,
                Rpm = new[] { state.Rpm[0], state.Rpm[1], state.Rpm[2], state.Rpm[3] },
                Duty = new[] { state.Duty[0], state.Duty[1], state.Duty[2], state.Duty[3] },
                FansPerPort = FansPerPort(LianLiHubSet.FansOf(s.Devices, hub.DeviceId)),
            };
            return Results.Json(resp, AppJsonContext.Default.LianLiStateResponse);
        });

        // PUT /devices/lianli/fan-count - set fan count for one port (0..3), sends
        // SetQuantity to hub and persists the count so the lighting provider
        // allocates the correct LED frames.
        app.MapPut("/devices/lianli/fan-count", (
            LianLiFanCountRequest body,
            LianLiHubSet hubs,
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "hub")] string? hubQuery,
            IConfigStore store,
            LianLiLightingDeviceProvider lighting) =>
        {
            var hub = ResolveHub(hubs, hubQuery);
            if (hub is null)
            {
                return Results.NotFound(ApiResponse.Fail("unknown hub"));
            }
            if (body.Port < 0 || body.Port >= LianLiProtocol.PortCount)
            {
                return Results.BadRequest(ApiResponse.Fail("port must be 0..3"));
            }
            var maxFans = hub.Profile.MaxFans;
            if (body.Count < 0 || body.Count > maxFans)
            {
                return Results.BadRequest(ApiResponse.Fail($"count must be 0..{maxFans}"));
            }
            if (hub.IsConnected)
            {
                hub.SetQuantity(body.Port, body.Count);
            }
            store.Update(s => LianLiHubSet.Editable(s.Devices, hub.DeviceId).Fans.SetFans(body.Port, body.Count));
            lighting.OnHubStateUpdated();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // GET /devices/lianli/composition - mirror/combine state + per-port active flags.
        app.MapGet("/devices/lianli/composition", (LianLiHubSet hubs, IConfigStore store, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "hub")] string? hubQuery) =>
        {
            var hub = ResolveHub(hubs, hubQuery);
            if (hub is null)
            {
                return Results.NotFound(ApiResponse.Fail("unknown hub"));
            }
            var s = store.Load();
            var hubId = hub.DeviceId;
            var comp = LianLiZoneSupport.ReadComposition(s, hubId);
            var active = new bool[LianLiProtocol.PortCount];
            var fans = new int[LianLiProtocol.PortCount];
            for (var p = 0; p < LianLiProtocol.PortCount; p++)
            {
                fans[p] = LianLiZoneSupport.ClampFans(LianLiHubSet.FansOf(s.Devices, hubId).GetFans(p), hub.Profile);
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
            LianLiHubSet hubs,
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "hub")] string? hubQuery,
            IConfigStore store,
            LianLiLightingDeviceProvider lighting,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var hub = ResolveHub(hubs, hubQuery);
            if (hub is null)
            {
                return Results.NotFound(ApiResponse.Fail("unknown hub"));
            }
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
                        var hubFans = LianLiHubSet.Editable(s.Devices, hubId).Fans;
                        var cur = LianLiZoneSupport.ClampFans(hubFans.GetFans(p), profile);
                        if (on && cur == 0) hubFans.SetFans(p, profile.MaxFans);
                        else if (!on && cur > 0) hubFans.SetFans(p, 0);
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
                    hub.SetQuantity(p, LianLiZoneSupport.ClampFans(LianLiHubSet.FansOf(after.Devices, hubId).GetFans(p), profile));
                }
            }

            lighting.OnHubStateUpdated();
            bridge?.RequestTopologyRefresh();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // GET /devices/lianli/lighting - current settings + the attached family's mode catalog.
        app.MapGet("/devices/lianli/lighting", (LianLiHubSet hubs, IConfigStore store, Nexus.Service.Lighting.Zones.ZoneTopology topology, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "hub")] string? hubQuery) =>
        {
            var hub = ResolveHub(hubs, hubQuery);
            if (hub is null)
            {
                return Results.NotFound(ApiResponse.Fail("unknown hub"));
            }
            var s = store.Load();
            var ls = LianLiHubSet.LightingOf(s.Devices, hub.DeviceId);
            var hubFans = LianLiHubSet.FansOf(s.Devices, hub.DeviceId);
            var profile = hub.Profile;
            var family = profile.Family;
            var composed = LianLiZoneSupport.Compose(hub.DeviceId, profile, LianLiZoneSupport.ReadComposition(s, hub.DeviceId), hubFans);
            var mergeBlocked = LianLiLightingFrameWriter.AnyDeviceExcluded(composed, s);
            var catalog = ModeDtos(LianLiLightingModes.CatalogFor(family), profile, mergeBlocked);
            var ports = new LianLiPortLookDto?[LianLiProtocol.PortCount];
            var savedPorts = ls.Ports ?? [];
            for (var p = 0; p < ports.Length && p < savedPorts.Count; p++)
            {
                var look = savedPorts[p];
                if (look is null) continue;
                ports[p] = new LianLiPortLookDto { Whole = EffectDto(look.Whole)!, InnerRing = EffectDto(look.InnerRing), OuterRing = EffectDto(look.OuterRing) };
            }
            // Report the mode the writer commits: a persisted key outside this
            // family's catalog falls back to static there too.
            var effectiveMode = ls.Mode == "custom" || LianLiLightingModes.Find(family, ls.Mode) != null ? ls.Mode : "static";
            var effect = LianLiLightingModes.Find(family, ls.Mode == "custom" ? ls.EffectMode ?? "rainbowWave" : ls.Mode);
            var effectMode = effect != null && effect.Key != "custom" ? effect.Key : "static";
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
                ArgbSync = ls.ArgbSync,
                ArgbSyncSource = ls.ArgbSyncSource,
                ArgbSyncSupported = true,
                ArgbSyncSourcesSupported = profile.ArgbSyncVerified,
                ArgbSyncSources = ArgbSyncSources(topology, hub.DeviceId, profile, hubFans),
                RingModes = HasRingEffects(profile)
                    ? new LianLiRingModesDto
                    {
                        Inner = ModeDtos(LianLiLightingModes.RingCatalogFor(family, outer: false), profile, mergeBlocked: true),
                        Outer = ModeDtos(LianLiLightingModes.RingCatalogFor(family, outer: true), profile, mergeBlocked: true),
                    }
                    : null,
                InnerRing = EffectDto(ls.InnerRing),
                OuterRing = EffectDto(ls.OuterRing),
                Ports = ports,
                MergeOrder = LianLiProtocol.ValidMergeOrder(ls.MergeOrder),
            }, AppJsonContext.Default.LianLiLightingResponse);
        });

        // PUT /devices/lianli/lighting - patch mode/speed/direction/brightness/colors.
        app.MapPut("/devices/lianli/lighting", (
            LianLiLightingRequest body,
            LianLiHubSet hubs,
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "hub")] string? hubQuery,
            IConfigStore store,
            Nexus.Service.Lighting.Zones.ZoneTopology topology,
            LianLiLightingDeviceProvider lighting,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var hub = ResolveHub(hubs, hubQuery);
            if (hub is null)
            {
                return Results.NotFound(ApiResponse.Fail("unknown hub"));
            }
            // Only a source being switched to must still be listed: turning sync
            // off has to work after the stored source dropped out of the list.
            var stored = LianLiHubSet.LightingOf(store.Load().Devices, hub.DeviceId).ArgbSyncSource;
            if (!string.IsNullOrEmpty(body.ArgbSyncSource) && body.ArgbSyncSource != stored && body.ArgbSync != false
                && Array.FindIndex(ArgbSyncSources(topology, hub.DeviceId, hub.Profile, LianLiHubSet.FansOf(store.Load().Devices, hub.DeviceId)), x => x.Id == body.ArgbSyncSource) < 0)
            {
                return Results.BadRequest(ApiResponse.Fail("ARGB sync source is not an addressable port"));
            }
            var profile = hub.Profile;
            var family = profile.Family;
            var targetError = ValidateTarget(body, profile, LianLiHubSet.LightingOf(store.Load().Devices, hub.DeviceId));
            if (targetError != null)
            {
                return Results.BadRequest(ApiResponse.Fail(targetError));
            }
            if (body.Port == null && body.Ring == null && body.Mode != null && body.Mode != "custom" && LianLiLightingModes.Find(family, body.Mode) == null)
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode key"));
            }
            store.Update(s =>
            {
                var ls = LianLiHubSet.Editable(s.Devices, hub.DeviceId).Lighting;
                if (body.MergeOrder != null) ls.MergeOrder = [.. body.MergeOrder];
                if (body.Port != null || body.Ring != null || body.SplitRings.HasValue)
                {
                    ApplyLook(ls, body, profile);
                    return;
                }
                if (body.Mode != null)
                {
                    ls.Mode = body.Mode;
                    if (body.Mode != "custom") ls.EffectMode = body.Mode;
                }
                if (body.Speed.HasValue) ls.Speed = Math.Clamp(body.Speed.Value, 0, 4);
                if (body.Direction.HasValue) ls.Direction = Math.Clamp(body.Direction.Value, 0, 1);
                if (body.Brightness.HasValue) ls.Brightness = Math.Clamp(body.Brightness.Value, 0, 4);
                if (body.Merge.HasValue) ls.Merge = body.Merge.Value;
                if (body.ArgbSyncSource != null) ls.ArgbSyncSource = body.ArgbSyncSource.Length > 0 ? body.ArgbSyncSource : null;
                if (body.ArgbSync.HasValue) ls.ArgbSync = body.ArgbSync.Value;
                if (body.Colors != null)
                {
                    var modeInfo = LianLiLightingModes.Find(family, ls.Mode);
                    var maxColors = modeInfo?.ColorsMax ?? 0;
                    var count = Math.Min(body.Colors.Length, maxColors);
                    ls.Colors = new List<string>(count);
                    for (var i = 0; i < count; i++)
                    {
                        ls.Colors.Add(body.Colors[i]);
                    }
                }
            });
            if (body.ArgbSync.HasValue) lighting.OnHubStateUpdated();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

    }

    // The hub a request names; the first hub when it names none.
    private static LianLiHub? ResolveHub(LianLiHubSet hubs, string? hubQuery) =>
        string.IsNullOrEmpty(hubQuery) ? hubs.Primary
        : hubQuery.Contains(':', StringComparison.Ordinal) ? null
        : hubs.Owner(hubQuery);

    // Rings take their own animations on a two-ring hub whose family has per-ring effects.
    private static bool HasRingEffects(in LianLiFanProfile profile) =>
        profile.ChannelsPerPort == 2 && LianLiLightingModes.RingCatalogFor(profile.Family, outer: false).Count > 0;

    private static LianLiModeInfoDto[] ModeDtos(IReadOnlyList<LianLiModeInfo> modes, in LianLiFanProfile profile, bool mergeBlocked)
    {
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
                DefaultColors = [.. m.DefaultColors],
                Mergeable = m.MergesOn(profile) && !mergeBlocked,
            };
        }
        return catalog;
    }

    private static LianLiEffectDto? EffectDto(LianLiEffectSettings? e) => e is null ? null : new LianLiEffectDto
    {
        Mode = e.Mode,
        Speed = e.Speed,
        Direction = e.Direction,
        Brightness = e.Brightness,
        Colors = [.. e.Colors],
    };

    // Why a port, ring or merge-order patch cannot apply; null when it can.
    private static string? ValidateTarget(LianLiLightingRequest body, in LianLiFanProfile profile, LianLiLightingSettings ls)
    {
        if (body.MergeOrder != null)
        {
            var valid = LianLiProtocol.ValidMergeOrder(body.MergeOrder);
            if (body.MergeOrder.Length != valid.Length || !body.MergeOrder.AsSpan().SequenceEqual(valid)) return "merge order must name every port once";
        }
        if (body.Port is int port && (port < 0 || port >= LianLiProtocol.PortCount)) return $"port must be 0..{LianLiProtocol.PortCount - 1}";
        // A look edit saves only the look, so hub-wide fields alongside it would be dropped.
        if ((body.Port != null || body.Ring != null || body.SplitRings.HasValue)
            && (body.Merge.HasValue || body.ArgbSync.HasValue || body.ArgbSyncSource != null))
        {
            return "a port or ring edit cannot carry hub settings";
        }
        if (body.ResetPort == true && body.Port == null) return "resetPort needs a port";
        if (body.Ring != null && body.Ring != "inner" && body.Ring != "outer") return "ring must be inner or outer";
        if ((body.Ring != null || body.SplitRings == true) && !HasRingEffects(profile)) return "this hub has no per-ring effects";
        if (body.Ring != null && body.SplitRings != true)
        {
            // A port without its own look starts from the hub's, rings included.
            var saved = ls.Ports ?? [];
            var own = body.Port is int p && p < saved.Count ? saved[p] : null;
            var (inner, outer) = own is null ? (ls.InnerRing, ls.OuterRing) : (own.InnerRing, own.OuterRing);
            if (inner is null || outer is null) return "rings are not separate";
        }
        if (body.Mode == null || (body.Port == null && body.Ring == null)) return null;
        var known = body.Ring != null
            ? LianLiLightingModes.FindRing(profile.Family, body.Ring == "outer", body.Mode) != null
            : body.Mode != "custom" && LianLiLightingModes.Find(profile.Family, body.Mode) != null;
        return known ? null : "unknown mode key";
    }

    // Applies a port, ring or split patch. A port's first edit starts its look
    // from the hub's; splitting seeds each ring from the whole-fan mode.
    private static void ApplyLook(LianLiLightingSettings ls, LianLiLightingRequest body, in LianLiFanProfile profile)
    {
        var family = profile.Family;
        LianLiPortLighting? look = null;
        if (body.Port is int port)
        {
            var ports = new List<LianLiPortLighting?>(ls.Ports ?? []);
            while (ports.Count < LianLiProtocol.PortCount) ports.Add(null);
            if (body.ResetPort == true)
            {
                ports[port] = null;
                ls.Ports = ports;
                return;
            }
            look = ports[port] ?? new LianLiPortLighting
            {
                Whole = HubWhole(ls, family),
                InnerRing = ls.InnerRing?.Clone(),
                OuterRing = ls.OuterRing?.Clone(),
            };
            ports[port] = look;
            ls.Ports = ports;
        }
        var whole = look?.Whole;
        if (body.SplitRings is bool split)
        {
            var source = whole ?? HubWhole(ls, family);
            var inner = split ? SeedRing(source, family, outer: false) : null;
            var outer = split ? SeedRing(source, family, outer: true) : null;
            if (look is null)
            {
                ls.InnerRing = inner;
                ls.OuterRing = outer;
            }
            else
            {
                look.InnerRing = inner;
                look.OuterRing = outer;
            }
        }
        LianLiEffectSettings? target;
        Func<string, LianLiModeInfo?> find;
        if (body.Ring != null)
        {
            var isOuter = body.Ring == "outer";
            target = isOuter ? (look is null ? ls.OuterRing : look.OuterRing) : (look is null ? ls.InnerRing : look.InnerRing);
            find = key => LianLiLightingModes.FindRing(family, isOuter, key);
        }
        else
        {
            target = whole;
            find = key => LianLiLightingModes.Find(family, key);
        }
        if (target is null) return;
        if (body.Mode != null) target.Mode = body.Mode;
        if (body.Speed.HasValue) target.Speed = Math.Clamp(body.Speed.Value, 0, 4);
        if (body.Direction.HasValue) target.Direction = Math.Clamp(body.Direction.Value, 0, 1);
        if (body.Brightness.HasValue) target.Brightness = Math.Clamp(body.Brightness.Value, 0, 4);
        if (body.Colors != null)
        {
            var max = find(target.Mode)?.ColorsMax ?? 0;
            target.Colors = [.. body.Colors.AsSpan(0, Math.Min(body.Colors.Length, max))];
        }
    }

    // The hub's whole-fan animation as an effect: the one it plays, or returns to from the Lighting page.
    private static LianLiEffectSettings HubWhole(LianLiLightingSettings ls, LianLiFanFamily family)
    {
        var key = ls.Mode == "custom" ? ls.EffectMode : ls.Mode;
        var mode = LianLiLightingModes.Find(family, key);
        return new LianLiEffectSettings
        {
            Mode = mode is null || mode == LianLiLightingModes.Custom ? "static" : mode.Key,
            Speed = ls.Speed,
            Direction = ls.Direction,
            Brightness = ls.Brightness,
            Colors = new List<string>(ls.Colors),
        };
    }

    // A ring starts on the whole-fan mode when its catalog has it, else on its first mode.
    private static LianLiEffectSettings SeedRing(LianLiEffectSettings whole, LianLiFanFamily family, bool outer)
    {
        var mode = LianLiLightingModes.FindRing(family, outer, whole.Mode) ?? LianLiLightingModes.RingCatalogFor(family, outer)[0];
        var ring = whole.Clone();
        ring.Mode = mode.Key;
        if (ring.Colors.Count > mode.ColorsMax) ring.Colors = ring.Colors.GetRange(0, mode.ColorsMax);
        return ring;
    }

    private static int[] FansPerPort(LianLiSettings fans)
    {
        var counts = new int[LianLiProtocol.PortCount];
        for (var p = 0; p < counts.Length; p++) counts[p] = fans.GetFans(p);
        return counts;
    }

    // Cards that can drive the hub's ARGB input: one addressable port each (the
    // chain route's own test) long enough for the hub's longest fan chain,
    // since every port plays the input from its first LED.
    private static LianLiArgbSourceDto[] ArgbSyncSources(Nexus.Service.Lighting.Zones.ZoneTopology topology, string hubId, in LianLiFanProfile profile, LianLiSettings fans)
    {
        if (!profile.ArgbSyncVerified) return Array.Empty<LianLiArgbSourceDto>();
        var ledsPerFan = profile.InnerLedsPerFan + profile.OuterLedsPerFan;
        var longest = 1;
        for (var p = 0; p < LianLiProtocol.PortCount; p++) longest = Math.Max(longest, fans.GetFans(p));
        var sources = new List<LianLiArgbSourceDto>();
        foreach (var structure in topology.AllStructures())
        {
            if (!structure.Partitionable || structure.Segments.Count != 1 || !structure.Segments[0].Resizable) continue;
            var max = structure.Segments[0].MaxLedCount;
            if (max > 0 && max < longest * ledsPerFan) continue;
            if (structure.DeviceId == hubId || structure.DeviceId.StartsWith(hubId + ":", StringComparison.Ordinal)) continue;
            sources.Add(new LianLiArgbSourceDto { Id = structure.DeviceId, Name = structure.Name });
        }
        return sources.ToArray();
    }
}

public sealed class LianLiStateResponse
{
    public bool IsConnected { get; set; }
    public string ModelName { get; set; } = "";
    /// <summary>Fans the attached family chains on one port.</summary>
    public int MaxFansPerPort { get; set; }
    public string FirmwareVersion { get; set; } = "";
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
    /// <summary>Palette the mode starts from, as #RRGGBB.</summary>
    public string[] DefaultColors { get; set; } = Array.Empty<string>();
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
    public bool ArgbSync { get; set; }
    public bool ArgbSyncSupported { get; set; }
    /// <summary>The hub's input layout is known, so a Nexus card can drive the header; otherwise sync hands the fans to the motherboard alone.</summary>
    public bool ArgbSyncSourcesSupported { get; set; }
    public string? ArgbSyncSource { get; set; }
    public LianLiArgbSourceDto[] ArgbSyncSources { get; set; } = Array.Empty<LianLiArgbSourceDto>();
    /// <summary>Each ring's own catalog; null on a hub without per-ring effects.</summary>
    public LianLiRingModesDto? RingModes { get; set; }
    public LianLiEffectDto? InnerRing { get; set; }
    public LianLiEffectDto? OuterRing { get; set; }
    /// <summary>Per-port looks by port; null plays the hub's.</summary>
    public LianLiPortLookDto?[] Ports { get; set; } = Array.Empty<LianLiPortLookDto?>();
    public int[] MergeOrder { get; set; } = Array.Empty<int>();
}

public sealed class LianLiEffectDto
{
    public string Mode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
}

public sealed class LianLiPortLookDto
{
    public LianLiEffectDto Whole { get; set; } = new();
    public LianLiEffectDto? InnerRing { get; set; }
    public LianLiEffectDto? OuterRing { get; set; }
}

public sealed class LianLiRingModesDto
{
    public LianLiModeInfoDto[] Inner { get; set; } = Array.Empty<LianLiModeInfoDto>();
    public LianLiModeInfoDto[] Outer { get; set; } = Array.Empty<LianLiModeInfoDto>();
}

public sealed class LianLiArgbSourceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class LianLiLightingRequest
{
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
    public bool? Merge { get; set; }
    public bool? ArgbSync { get; set; }
    /// <summary>Lighting card driving the hub's ARGB input; empty clears it.</summary>
    public string? ArgbSyncSource { get; set; }
    /// <summary>The port the effect fields edit; the hub when null.</summary>
    public int? Port { get; set; }
    /// <summary>"inner" or "outer": the ring the effect fields edit; the whole fan when null.</summary>
    public string? Ring { get; set; }
    public bool? SplitRings { get; set; }
    /// <summary>Drops the port's own look so it plays the hub's again.</summary>
    public bool? ResetPort { get; set; }
    public int[]? MergeOrder { get; set; }
}

