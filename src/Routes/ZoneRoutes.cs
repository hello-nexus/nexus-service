using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    /// <summary>
    /// Zones model surface: device structure (segments + current partition),
    /// partition save/reset, and the device-scoped LED map the new editor
    /// uses exclusively. Partition changes drop the device's per-zone prefs,
    /// canvas layouts, and applied per-zone mappings (fresh defaults), then
    /// rebuild engine frames and broadcast.
    /// </summary>
    private static void MapZoneEndpoints(WebApplication app)
    {
        app.MapGet("/devices/lighting-devices/{deviceId}/structure", (string deviceId,
            ZoneTopology topology,
            System.Collections.Generic.IEnumerable<IComposableHubSource> composables,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
            {
                return Results.Json(new DeviceStructureResponse { Error = true, Msg = "unknown device" },
                    AppJsonContext.Default.DeviceStructureResponse);
            }
            var settings = store.Load();
            var response = BuildStructureResponse(structure, settings, topology, composables);
            return Results.Json(response, AppJsonContext.Default.DeviceStructureResponse);
        });

        app.MapPost("/devices/lighting-devices/{deviceId}/zones", (string deviceId, SaveZonePartitionBody body,
            ZoneTopology topology,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
                return ApiResponse.Fail("unknown device");

            if (!structure.Partitionable)
                return ApiResponse.Fail("device does not support zone partitions");

            var normalized = ZoneResolution.NormalizeDefs(structure, body.Zones);
            var validation = ZonePartitionValidator.Validate(structure.Segments, normalized);
            if (!validation.Ok)
                return ApiResponse.Fail(string.Join("; ", validation.Errors));

            var settings = store.Load();
            var oldZoneIds = new List<string>();
            foreach (var zone in topology.ZonesFor(structure, settings))
                oldZoneIds.Add(zone.Id);

            store.Update(s =>
            {
                s.Devices.ZonePartitions[deviceId] = normalized;
                // The user is describing the zones by hand now, so whatever
                // chain wrote the previous ones no longer describes the port.
                ZoneResolution.DropChains(s, structure);
                ZoneStateDrop.Drop(s, oldZoneIds);
            });
            bridge?.RequestTopologyRefresh();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        app.MapDelete("/devices/lighting-devices/{deviceId}/zones", (string deviceId,
            ZoneTopology topology,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
                return ApiResponse.Fail("unknown device");

            if (!structure.Partitionable)
                return ApiResponse.Fail("device does not support zone partitions");

            var settings = store.Load();
            var hasChain = false;
            for (int i = 0; i < structure.Segments.Count && !hasChain; i++)
                hasChain = settings.Devices.PortChains.ContainsKey(ZoneResolution.ChainKey(deviceId, i));
            if (!settings.Devices.ZonePartitions.ContainsKey(deviceId) && !hasChain)
                return ApiResponse.Ok();

            var oldZoneIds = new List<string>();
            foreach (var zone in topology.ZonesFor(structure, settings))
                oldZoneIds.Add(zone.Id);

            store.Update(s =>
            {
                s.Devices.ZonePartitions.Remove(deviceId);
                ZoneResolution.DropChains(s, structure);
                ZoneStateDrop.Drop(s, oldZoneIds);
            });
            bridge?.RequestTopologyRefresh();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        // Device-scoped LED map: the whole LED space per segment with
        // resolved positions, override markers, and zone membership. The new
        // editor reads this exclusively; UVs are zone-local (one canvas rect
        // per zone), matching what each card's engine frame samples.
        // defaults=true previews the factory layout without persisting
        // anything (the SPA's Reset preview).
        app.MapGet("/devices/lighting-devices/{deviceId}/device-map", (string deviceId, bool? defaults,
            ZoneTopology topology,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
            {
                return Results.Json(new DeviceMapResponse { Error = true, Msg = "unknown device" },
                    AppJsonContext.Default.DeviceMapResponse);
            }
            var settings = store.Load();
            if (defaults == true)
                settings = DeviceMapDefaultsFacade(settings);
            var response = BuildDeviceMapResponse(structure, settings, topology);
            return Results.Json(response, AppJsonContext.Default.DeviceMapResponse);
        });

        // Device-scoped LED map save: segment-local override list replaces
        // the device's stored overrides wholesale.
        app.MapPost("/devices/lighting-devices/{deviceId}/device-map", (string deviceId, SaveDeviceMapBody body,
            ZoneTopology topology,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
                return ApiResponse.Fail("unknown device");

            var sanitized = new List<SegmentLedOverride>(body.Overrides.Count);
            foreach (var o in body.Overrides)
            {
                if (o.Segment < 0 || o.Segment >= structure.Segments.Count)
                    continue;
                if (o.LedIndex < 0 || o.LedIndex >= structure.Segments[o.Segment].LedCount)
                    continue;
                sanitized.Add(o);
            }
            store.Update(s =>
            {
                if (sanitized.Count > 0)
                    s.Devices.DeviceLedOverrides[deviceId] = sanitized;
                else
                    s.Devices.DeviceLedOverrides.Remove(deviceId);
                if (body.AspectRatio > 0)
                    s.Devices.DeviceAspectRatios[deviceId] = body.AspectRatio;
            });

            foreach (var zone in topology.ZonesFor(structure, store.Load()))
                topology.RefreshCardFrame(zone.Id);
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });

        // Device-scoped LED map reset: drop the device's stored overrides and
        // canvas aspect ratio so the resolver falls back to factory defaults.
        // Partition, prefs, layouts, and applied mappings stay untouched.
        app.MapDelete("/devices/lighting-devices/{deviceId}/device-map", (string deviceId,
            ZoneTopology topology,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
                return ApiResponse.Fail("unknown device");

            store.Update(s => ClearDeviceMapState(s, deviceId));

            foreach (var zone in topology.ZonesFor(structure, store.Load()))
                topology.RefreshCardFrame(zone.Id);
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            return ApiResponse.Ok();
        });
    }

    /// <summary>
    /// Factory-defaults view of the settings for the device-map GET: keep the
    /// port chains, partition, wired LED counts, and canvas aspect ratio
    /// (they describe the hardware as wired and the editor canvas shape,
    /// matching the per-card led-map defaults preview) but drop the override
    /// and applied-mapping layers so the resolver yields provider defaults.
    /// </summary>
    internal static NexusSettings DeviceMapDefaultsFacade(NexusSettings settings) => new()
    {
        Devices = new DevicesSettings
        {
            PortChains = settings.Devices.PortChains,
            ZonePartitions = settings.Devices.ZonePartitions,
            ZoneLedCounts = settings.Devices.ZoneLedCounts,
            DeviceAspectRatios = settings.Devices.DeviceAspectRatios,
        },
    };

    /// <summary>
    /// The device-map DELETE's settings mutation: remove the device's LED
    /// overrides and aspect ratio, nothing else.
    /// </summary>
    internal static void ClearDeviceMapState(NexusSettings settings, string deviceId)
    {
        settings.Devices.DeviceLedOverrides.Remove(deviceId);
        settings.Devices.DeviceAspectRatios.Remove(deviceId);
    }

    /// <summary>
    /// Structure response for one device, resolved against the given
    /// settings snapshot: segments, resolved zones, and (when chainable) the
    /// chain's product list. Shared by the structure GET and the chain
    /// preview, which resolves it against a detached settings copy.
    /// </summary>
    internal static DeviceStructureResponse BuildStructureResponse(DeviceStructure structure, NexusSettings settings,
        ZoneTopology topology, System.Collections.Generic.IEnumerable<IComposableHubSource> composables)
    {
        var zones = topology.ZonesFor(structure, settings);
        var response = new DeviceStructureResponse
        {
            Id = structure.DeviceId,
            Name = structure.Name,
            DeviceKey = structure.DeviceKey,
            IsDefaultPartition = zones.Count == 0 || zones[0].IsDefault,
            HubComposition = DescribeHubComposition(composables, structure.DeviceId),
            // One resizable segment means an ARGB port: the zones are the
            // user's chain, not a firmware-fixed layout.
            Chainable = structure.Partitionable
                && structure.Segments.Count == 1
                && structure.Segments[0].Resizable,
        };
        if (response.Chainable)
        {
            settings.Devices.PortChains.TryGetValue(
                Nexus.Service.Lighting.Zones.ZoneResolution.ChainKey(structure.DeviceId, 0), out var chain);
            for (int i = 0; i < zones.Count; i++)
            {
                // The chain and the resolved zones are written together, so
                // they line up; a shorter chain means the zones came from
                // somewhere else and every zone reads as custom.
                var link = chain is not null && i < chain.Count ? chain[i] : null;
                // An unchained port still reports one row, so the editor
                // renders the same list whether or not a chain exists; it
                // reads as a generic strip the user can resize.
                var key = link?.Key ?? Nexus.Service.Lighting.Mappings.GenericChainArtifacts.StripKey;
                response.Chain.Add(new ChainEntryDto
                {
                    Key = key,
                    Name = zones[i].RawName,
                    LedCount = zones[i].LedCount,
                    EditableCount = Nexus.Service.Lighting.Mappings.GenericChainArtifacts.IsGeneric(key),
                });
            }
        }
        foreach (var seg in structure.Segments)
        {
            response.Segments.Add(new StructureSegmentDto
            {
                Index = seg.Index,
                Name = seg.Name,
                LedCount = seg.LedCount,
                Resizable = seg.Resizable,
                MaxLedCount = seg.MaxLedCount,
                ZoneType = seg.ZoneType,
            });
        }
        foreach (var zone in zones)
        {
            // RawName, not the full card name: the editor's zone rail
            // already shows the device, so chips carry just the zone
            // ("Keys", "Digital LED 1", the user-given name).
            var dto = new StructureZoneDto { Id = zone.Id, Name = zone.RawName };
            foreach (var slice in zone.Slices)
                dto.Slices.Add(new ZoneSlice { Segment = slice.Segment, Start = slice.Start, Count = slice.Count });
            response.Zones.Add(dto);
        }
        return response;
    }

    /// <summary>
    /// Device-scoped LED map for one device, resolved against the given
    /// settings snapshot: per-segment LEDs with position, zone membership,
    /// and override markers. Shared by the device-map GET and the chain
    /// preview, which resolves it against a detached settings copy.
    /// </summary>
    internal static DeviceMapResponse BuildDeviceMapResponse(DeviceStructure structure, NexusSettings settings, ZoneTopology topology)
    {
        var zones = topology.ZonesFor(structure, settings);

        // Per-zone resolved layouts and contexts, then scattered back
        // into segment space through each zone's slices.
        var layouts = new Dictionary<string, Nexus.Service.Lighting.Mappings.ResolvedLedLayout>(zones.Count);
        var contexts = new List<(ResolvedZone Zone, ZoneOverrideContext Ctx)>(zones.Count);
        foreach (var zone in zones)
        {
            contexts.Add((zone, ZoneResolution.ContextOf(structure, zone)));
            var resolution = topology.ResolveZone(structure, zone, settings);
            layouts[zone.Id] = resolution.Layout;
        }

        settings.Devices.DeviceLedOverrides.TryGetValue(structure.DeviceId, out var overrides);
        var response = new DeviceMapResponse
        {
            Id = structure.DeviceId,
            Name = structure.Name,
            IsDefaultPartition = zones.Count == 0 || zones[0].IsDefault,
            AspectRatio = settings.Devices.DeviceAspectRatios.TryGetValue(structure.DeviceId, out var ratio) ? ratio : 0f,
        };

        foreach (var seg in structure.Segments)
        {
            var segDto = new DeviceMapSegmentDto
            {
                Index = seg.Index,
                Name = seg.Name,
                LedCount = seg.LedCount,
                Resizable = seg.Resizable,
                ZoneType = seg.ZoneType,
            };
            for (int local = 0; local < seg.LedCount; local++)
            {
                var led = new DeviceMapLedDto { Index = local, Name = $"LED {local}" };
                foreach (var (zone, ctx) in contexts)
                {
                    var zoneLocal = ctx.MapFromSegment(seg.Index, local);
                    if (zoneLocal < 0)
                        continue;
                    led.ZoneId = zone.Id;
                    if (layouts.TryGetValue(zone.Id, out var layout) && zoneLocal < layout.LedCount)
                    {
                        led.U = zoneLocal < layout.U.Length ? layout.U[zoneLocal] : 0f;
                        led.V = zoneLocal < layout.V.Length ? layout.V[zoneLocal] : 0f;
                        led.IsCustom = layout.CustomLeds.Contains(zoneLocal);
                        led.Disabled = layout.Disabled is { } flags && zoneLocal < flags.Length && flags[zoneLocal];
                    }
                    break;
                }
                if (overrides is not null)
                {
                    foreach (var o in overrides)
                    {
                        if (o.Segment == seg.Index && o.LedIndex == local)
                        { led.IsCustom = true; break; }
                    }
                }
                segDto.Leds.Add(led);
            }
            response.Segments.Add(segDto);
        }
        return response;
    }

    /// <summary>First composable hub that owns <paramref name="deviceId"/>, mapped to the editor DTO; null when none does.</summary>
    private static Nexus.Service.Models.Devices.HubCompositionDto? DescribeHubComposition(
        System.Collections.Generic.IEnumerable<IComposableHubSource> composables, string deviceId)
    {
        foreach (var hub in composables)
        {
            var info = hub.DescribeComposition(deviceId);
            if (info is null) continue;
            return new Nexus.Service.Models.Devices.HubCompositionDto
            {
                HubId = info.HubId,
                HubKind = info.HubKind,
                PortCount = info.PortCount,
                HasRingsAxis = info.HasRingsAxis,
                HasPortToggle = info.HasPortToggle,
                HasMirror = info.HasMirror,
                Mirror = info.Mirror,
                CombineRings = info.CombineRings,
                ActivePorts = info.ActivePorts,
            };
        }
        return null;
    }
}
