using System;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    /// <summary>
    /// Community mapping flow per lighting device: browse the registry
    /// (proxied through the service's disk cache so the SPA never talks to
    /// the cloud directly), apply/revert, publish, and .nexusmap
    /// export/import. The first-seen auto-apply path lives in
    /// <see cref="Nexus.Service.Lighting.Mappings.MappingAutoApplyService"/>.
    /// </summary>
    private static void MapMappingEndpoints(WebApplication app)
    {
        // Ranked community mappings for this device + local apply state.
        app.MapGet("/devices/lighting-devices/{id}/mappings", async (string id, bool? refresh,
            MappingApplyService mappings,
            MappingCloudClient cloud,
            Nexus.Service.Persistence.IConfigStore store,
            CancellationToken ct) =>
        {
            var card = mappings.FindCard(id);
            if (card is null)
            {
                return Results.Json(new DeviceMappingsResponse { Error = true, Msg = "unknown device" },
                    AppJsonContext.Default.DeviceMappingsResponse);
            }

            var settings = store.Load();
            settings.Devices.AppliedMappings.TryGetValue(id, out var applied);
            var response = new DeviceMappingsResponse
            {
                DeviceKey = card.DeviceKey,
                AutoApplyDeclined = settings.Devices.MappingAutoApplyDeclined.Contains(id),
                Applied = applied is null ? null : new AppliedMappingSummary
                {
                    MappingId = applied.MappingId,
                    Name = applied.Name,
                    Source = applied.Source,
                    ContentHash = applied.ContentHash,
                    AutoApplied = applied.AutoApplied,
                    AppliedAtMs = applied.AppliedAt.ToUnixTimeMilliseconds(),
                },
            };
            if (card.DeviceKey.Length > 0)
            {
                var list = await cloud.GetMappingsAsync(card.DeviceKey, refresh == true, ct).ConfigureAwait(false);
                response.Items = list.Items;
                response.Offline = list.Offline;
            }
            return Results.Json(response, AppJsonContext.Default.DeviceMappingsResponse);
        });

        // The pre-built product catalog: everything a user can hang off an ARGB
        // header. Served straight from the embedded resource - no registry, no
        // cache, no network - because a header cannot report what is wired to
        // it and the picker is the only way for the user to say.
        app.MapGet("/devices/lighting-devices/mappings/catalog", (string? q, string? type, int? limit) =>
        {
            var items = BuiltInMappingsCatalog.Search(q, type, limit ?? 50, out var matched);
            var response = new BuiltInMappingsResponse
            {
                Items = items,
                Total = matched,
            };
            return Results.Json(response, AppJsonContext.Default.BuiltInMappingsResponse);
        });

        // Cache-only availability counts for the device-card badge. Never
        // touches the network: counts come from the disk cache the list
        // proxy and auto-apply worker keep warm.
        app.MapGet("/devices/lighting-devices/mappings/available", (
            Nexus.Service.Devices.ILightingDeviceProvider ld,
            MappingCloudClient cloud,
            Nexus.Service.Persistence.IConfigStore store) =>
        {
            var response = new MappingsAvailableResponse();
            var applied = store.Load().Devices.AppliedMappings;
            foreach (var card in ld.GetAll().Devices)
            {
                // The badge advertises layouts the user has not engaged
                // with yet; devices already running a mapping stay quiet.
                if (card.DeviceKey.Length == 0 || applied.ContainsKey(card.Id))
                    continue;
                var count = cloud.CachedMappingCount(card.DeviceKey);
                if (count > 0)
                    response.Counts[card.Id] = count;
            }
            return Results.Json(response, AppJsonContext.Default.MappingsAvailableResponse);
        });

        // Apply a community mapping by registry id (must be in the cached list).
        app.MapPost("/devices/lighting-devices/{id}/mappings/apply", async (string id, ApplyMappingBody body,
            MappingApplyService mappings,
            MappingCloudClient cloud,
            CancellationToken ct) =>
        {
            var card = mappings.FindCard(id);
            if (card is null || card.DeviceKey.Length == 0)
                return ApiResponse.Fail("unknown device");
            var list = await cloud.GetMappingsAsync(card.DeviceKey, forceRefresh: false, ct).ConfigureAwait(false);
            CommunityMapping? item = null;
            foreach (var candidate in list.Items)
            {
                if (candidate.Id == body.MappingId)
                { item = candidate; break; }
            }
            if (item?.Payload is null)
                return ApiResponse.Fail("mapping not found");
            return mappings.Apply(id, item.Payload, item.Id, MappingApplyService.SourceCommunity, auto: false, item.ContentHash) switch
            {
                MappingApplyService.ApplyOutcome.Applied => ApiResponse.Ok(),
                MappingApplyService.ApplyOutcome.Invalid => ApiResponse.Fail("mapping failed validation"),
                _ => ApiResponse.Fail("unknown device"),
            };
        });

        // Assign a pre-built mapping by product key. The user's own edits keep
        // layering on top (DeviceLedOverrides / LedGroups) and are never folded
        // back into the artifact, so re-assigning always restores the shipped
        // layout rather than whatever the last edit left behind.
        app.MapPost("/devices/lighting-devices/{id}/mappings/assign", (string id, AssignMappingBody body,
            MappingApplyService mappings) =>
        {
            var artifact = BuiltInMappingsCatalog.Find(body.Key ?? "");
            if (artifact is null)
                return ApiResponse.Fail("unknown mapping");
            return mappings.Apply(id, artifact, body.Key, MappingApplyService.SourceBuiltIn, auto: false) switch
            {
                MappingApplyService.ApplyOutcome.Applied => ApiResponse.Ok(),
                MappingApplyService.ApplyOutcome.Invalid => ApiResponse.Fail("mapping failed validation"),
                _ => ApiResponse.Fail("unknown device"),
            };
        });

        // Wire an ordered product chain to one ARGB port: three fans in series
        // become three cards, mixed types allowed. The chain owns both the
        // port's LED count (the sum of its products) and its partition (one
        // zone per product), written together so the count can never drift out
        // from under the slices - which is the drift ZonePartitionValidator's
        // rule 2 exists to prevent, and why that rule lifts for a chained port.
        app.MapPost("/devices/lighting-devices/{deviceId}/mappings/chain", (string deviceId, SetChainBody body,
            Nexus.Service.Lighting.Zones.ZoneTopology topology,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
            Nexus.Service.Lighting.NollieLightingDeviceProvider nollie,
            Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
                return Results.Json(ApiResponse.Fail("unknown device"), AppJsonContext.Default.ApiResponse);
            if (!structure.Partitionable)
                return Results.Json(ApiResponse.Fail("device does not support zone partitions"), AppJsonContext.Default.ApiResponse);
            // A port is one device with one segment, so there is nothing to
            // address: the chain always tiles segment 0.
            const int segment = 0;
            if (structure.Segments.Count != 1)
                return Results.Json(ApiResponse.Fail("device is not a single addressable port"), AppJsonContext.Default.ApiResponse);
            if (!structure.Segments[segment].Resizable)
                return Results.Json(ApiResponse.Fail("segment is not an addressable port"), AppJsonContext.Default.ApiResponse);

            var plan = TryBuildChainPlan(structure, segment, body.Entries ?? new(), out var planError);
            if (plan is null)
                return Results.Json(ApiResponse.Fail(planError!), AppJsonContext.Default.ApiResponse);

            var chainKey = Nexus.Service.Lighting.Zones.ZoneResolution.ChainKey(deviceId, segment);
            // Resize is keyed by the card that owns the segment. On a port
            // device that is the device id itself; the fallback covers a
            // structure whose defaults were not authored per segment.
            var defaultCardId = structure.DefaultZones.Count > 0
                ? structure.DefaultZones[0].Id
                : deviceId;
            var oldZoneIds = new List<string>();
            foreach (var zone in topology.ZonesFor(structure, store.Load()))
                oldZoneIds.Add(zone.Id);

            var response = new SetChainResponse();
            store.Update(s =>
            {
                // Per-zone state belongs to the product in a slot, not the
                // slot: a link that only moved along the chain keeps its name,
                // brightness, canvas rect and control state, while a slot whose
                // product changed starts fresh. The client says which by
                // echoing each link's old ordinal. Read before the drop below,
                // which is what would otherwise reset every surviving link.
                var carried = CarriedChainState(s, deviceId, plan.Origins);
                foreach (var zoneId in oldZoneIds)
                {
                    // The port's own rename lives under the device id, which is
                    // also its unchained default zone id; only the slots go.
                    if (zoneId != deviceId) s.Lighting.DeviceNames.Remove(zoneId);
                }
                Nexus.Service.Lighting.Zones.ZoneStateDrop.Drop(s, oldZoneIds);
                RestoreChainState(s, carried);
                if (plan.Entries.Count == 0)
                {
                    // Clearing: the port goes back to one whole-segment zone
                    // sized by whatever the user last set.
                    s.Devices.PortChains.Remove(chainKey);
                    s.Devices.ZonePartitions.Remove(deviceId);
                    return;
                }

                s.Devices.PortChains[chainKey] = plan.Entries;
                s.Devices.ZoneLedCounts[defaultCardId] = plan.Total;
                s.Devices.ZonePartitions[deviceId] = plan.Defs;

                // Assign each product to the zone it just created. Written here
                // rather than through MappingApplyService because those cards do
                // not exist until this partition lands.
                foreach (var (ordinal, artifact) in plan.Applied)
                {
                    var zoneId = Nexus.Service.Lighting.Zones.ZoneResolution.CustomZoneId(deviceId, ordinal);
                    response.ZoneIds.Add(zoneId);
                    s.Devices.AppliedMappings[zoneId] = Nexus.Service.Lighting.Zones.PortChainWriter.AppliedRef(artifact);
                }
            });

            if (plan.Entries.Count > 0)
            {
                // ZoneLedCounts was written directly above rather than through
                // SetZoneLedCount, so what follows from a count (the legacy 1CH
                // controller's LED-count handshake, the standalone settings
                // whose MOS bit tracks the GPU harness) needs a separate push.
                nollie.PushLedCountHandshakeFor(defaultCardId);
            }
            bridge?.RequestTopologyRefresh();
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
            response.LedCount = plan.Total;
            return Results.Json(response, AppJsonContext.Default.SetChainResponse);
        });

        // Preview what the chain POST would produce, writing nothing: the same
        // validation as the POST, then the structure and device-map builders run
        // against a detached settings copy with the chain overlaid, so the
        // editor can stage a chain edit and only commit it on Save.
        app.MapPost("/devices/lighting-devices/{deviceId}/mappings/chain/preview", (string deviceId, SetChainBody body,
            Nexus.Service.Lighting.Zones.ZoneTopology topology,
            Nexus.Service.Persistence.IConfigStore store,
            System.Collections.Generic.IEnumerable<Nexus.Service.Lighting.Zones.IComposableHubSource> composables) =>
        {
            var structure = topology.FindStructure(deviceId);
            if (structure is null)
                return Results.Json(new ChainPreviewResponse { Error = true, Msg = "unknown device" }, AppJsonContext.Default.ChainPreviewResponse);
            if (!structure.Partitionable)
                return Results.Json(new ChainPreviewResponse { Error = true, Msg = "device does not support zone partitions" }, AppJsonContext.Default.ChainPreviewResponse);
            const int segment = 0;
            if (structure.Segments.Count != 1)
                return Results.Json(new ChainPreviewResponse { Error = true, Msg = "device is not a single addressable port" }, AppJsonContext.Default.ChainPreviewResponse);
            if (!structure.Segments[segment].Resizable)
                return Results.Json(new ChainPreviewResponse { Error = true, Msg = "segment is not an addressable port" }, AppJsonContext.Default.ChainPreviewResponse);

            var plan = TryBuildChainPlan(structure, segment, body.Entries ?? new(), out var planError);
            if (plan is null)
                return Results.Json(new ChainPreviewResponse { Error = true, Msg = planError! }, AppJsonContext.Default.ChainPreviewResponse);

            var chainKey = Nexus.Service.Lighting.Zones.ZoneResolution.ChainKey(deviceId, segment);
            var defaultCardId = structure.DefaultZones.Count > 0
                ? structure.DefaultZones[0].Id
                : deviceId;

            var previewSettings = BuildChainPreviewSettings(store.Load(), deviceId, chainKey, defaultCardId, plan);
            // Clearing does not touch the persisted LED count, so the segment
            // stays as-is; only a non-empty chain resizes it to the total.
            var previewStructure = plan.Entries.Count == 0
                ? structure
                : ChainPreviewStructure(structure, segment, plan.Total);

            var response = new ChainPreviewResponse
            {
                Structure = BuildStructureResponse(previewStructure, previewSettings, topology, composables),
                Map = BuildDeviceMapResponse(previewStructure, previewSettings, topology),
            };
            return Results.Json(response, AppJsonContext.Default.ChainPreviewResponse);
        });

        // Revert the applied mapping. reason: undo (auto-apply veto) | switched | reset.
        app.MapDelete("/devices/lighting-devices/{id}/mapping", (string id, string? reason,
            MappingApplyService mappings) =>
        {
            var normalized = reason is "undo" or "switched" or "reset" ? reason : "reset";
            return mappings.Revert(id, normalized)
                ? ApiResponse.Ok()
                : ApiResponse.Fail("no mapping applied");
        });

        // Import a .nexusmap artifact (validated like any other source).
        app.MapPost("/devices/lighting-devices/{id}/mappings/import", (string id, MappingArtifact artifact,
            MappingApplyService mappings) =>
            mappings.Apply(id, artifact, mappingId: null, source: MappingApplyService.SourceFile, auto: false) switch
            {
                MappingApplyService.ApplyOutcome.Applied => ApiResponse.Ok(),
                MappingApplyService.ApplyOutcome.Invalid => ApiResponse.Fail("artifact failed validation"),
                _ => ApiResponse.Fail("unknown device"),
            });

        // Export the device's current resolved layout as a .nexusmap artifact.
        app.MapGet("/devices/lighting-devices/{id}/mapping/export", (string id,
            MappingApplyService mappings) =>
        {
            var artifact = mappings.Export(id);
            return Results.Json(new ExportMappingResponse
            {
                Error = artifact is null,
                Msg = artifact is null ? "unknown device" : "Ok",
                Artifact = artifact,
            }, AppJsonContext.Default.ExportMappingResponse);
        });

        // Publish the current layout to the registry. Always explicit. No
        // user-authored text travels: the public name is derived from the
        // device itself, so there is nothing to sanitize and nothing to
        // moderate beyond geometry.
        app.MapPost("/devices/lighting-devices/{id}/mappings/publish", async (string id,
            MappingApplyService mappings,
            MappingCloudClient cloud,
            CancellationToken ct) =>
        {
            var card = mappings.FindCard(id);
            if (card is null)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "unknown device" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            var name = card.Name.Trim();
            if (name.Length == 0)
                name = card.DeviceKey;
            if (name.Length > MappingSchema.MaxNameLength)
                name = name.Substring(0, MappingSchema.MaxNameLength);
            var artifact = mappings.Export(id, name);
            if (artifact is null || artifact.Device.Key.Length == 0)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "device cannot be fingerprinted" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            var lint = MappingLint.Validate(artifact);
            if (!lint.Ok)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "layout failed validation" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            var published = await cloud.PublishAsync(artifact, name, description: null, authorName: null, ct).ConfigureAwait(false);
            if (published is null)
            {
                return Results.Json(new PublishMappingResponse { Error = true, Msg = "publish failed or anonymous data is disabled" },
                    AppJsonContext.Default.PublishMappingResponse);
            }
            return Results.Json(published, AppJsonContext.Default.PublishMappingResponse);
        });
    }

    /// <summary>
    /// A chain request resolved against a device's single addressable segment:
    /// the persisted entries, the zone partition they tile into that segment,
    /// each zone's ordinal and product artifact, and the chain's total LED
    /// count. Entries is empty for a request that clears the chain. Shared by
    /// the chain POST and its preview so a preview can never accept something
    /// the POST would then reject.
    /// </summary>
    private sealed class ChainPlan
    {
        public required List<Nexus.Service.Persistence.ChainEntry> Entries { get; init; }
        public required List<Nexus.Service.Persistence.ZoneDef> Defs { get; init; }
        public required List<(int Ordinal, MappingArtifact Artifact)> Applied { get; init; }
        public required int Total { get; init; }
        /// <summary>Per new ordinal, the ordinal it held in the chain on disk, or null when the link is new. Not persisted - it only moves per-zone state across the write.</summary>
        public required List<int?> Origins { get; init; }
    }

    /// <summary>
    /// Validates a chain request against a device's structure and resolves it
    /// to a <see cref="ChainPlan"/>, or fails with the same message the chain
    /// POST returns for that failure. Every guard here is a guard the POST
    /// also runs, so a preview and the save it precedes never disagree.
    /// </summary>
    private static ChainPlan? TryBuildChainPlan(Nexus.Service.Lighting.Zones.DeviceStructure structure, int segment,
        List<SetChainEntry> requested, out string? error)
    {
        var links = new List<(Nexus.Service.Persistence.ChainEntry Entry, MappingArtifact? Artifact)>(requested.Count);
        var total = 0;
        foreach (var want in requested)
        {
            if (string.IsNullOrEmpty(want.Key))
            {
                error = "chain entry needs a key";
                return null;
            }

            if (GenericChainArtifacts.IsGeneric(want.Key))
            {
                // A generic's geometry follows the count, so the count is
                // the client's to set here and only here.
                if (want.LedCount <= 0 || want.LedCount > GenericChainArtifacts.MaxArtifactLedCount)
                {
                    error = "generic zone led count out of range";
                    return null;
                }
                var generic = GenericChainArtifacts.Build(want.Key, want.LedCount);
                if (generic is null)
                {
                    error = $"could not build {want.Key}";
                    return null;
                }
                links.Add((new Nexus.Service.Persistence.ChainEntry { Key = want.Key, LedCount = want.LedCount }, generic));
                total += want.LedCount;
                continue;
            }

            var artifact = BuiltInMappingsCatalog.Find(want.Key);
            if (artifact is null)
            {
                error = $"unknown mapping {want.Key}";
                return null;
            }
            var lint = MappingLint.Validate(artifact);
            if (!lint.Ok)
            {
                error = $"{want.Key} failed validation";
                return null;
            }
            var count = Nexus.Service.Lighting.Zones.PortChainWriter.ProductLedCount(artifact);
            if (count <= 0)
            {
                error = $"{want.Key} has no LEDs to chain";
                return null;
            }
            // The product's own count wins; a client cannot resize a product.
            links.Add((new Nexus.Service.Persistence.ChainEntry { Key = want.Key, LedCount = count }, artifact));
            total += count;
        }

        // The port's own ceiling wins where it advertises one: past it the
        // chain would persist and render while the writer dropped the tail.
        var portMax = structure.Segments[segment].MaxLedCount;
        var ceiling = portMax > 0 ? Math.Min(portMax, GenericChainArtifacts.MaxArtifactLedCount) : GenericChainArtifacts.MaxArtifactLedCount;
        if (total > ceiling)
        {
            error = $"chain needs {total} LEDs; this port carries {ceiling}";
            return null;
        }

        var defs = new List<Nexus.Service.Persistence.ZoneDef>();
        var applied = new List<(int Ordinal, MappingArtifact Artifact)>();
        for (int seg = 0; seg < structure.Segments.Count; seg++)
        {
            if (seg != segment)
            {
                defs.Add(new Nexus.Service.Persistence.ZoneDef
                {
                    Name = structure.Segments[seg].Name,
                    Slices = { new Nexus.Service.Persistence.ZoneSlice
                        { Segment = seg, Start = 0, Count = structure.Segments[seg].LedCount } },
                });
                continue;
            }
            var start = 0;
            for (int i = 0; i < links.Count; i++)
            {
                var (entry, artifact) = links[i];
                var count = entry.LedCount;
                // No ordinal suffix: three identical fans read as
                // three "QX Fan" chips, and their position in the chain
                // is what tells them apart.
                defs.Add(new Nexus.Service.Persistence.ZoneDef
                {
                    Name = Nexus.Service.Lighting.Zones.PortChainWriter.ZoneName(artifact!),
                    Slices = { new Nexus.Service.Persistence.ZoneSlice
                        { Segment = seg, Start = start, Count = count } },
                });
                applied.Add((defs.Count - 1, artifact!));
                start += count;
            }
        }

        error = null;
        return new ChainPlan
        {
            Entries = links.ConvertAll(l => l.Entry),
            Defs = defs,
            Applied = applied,
            Total = total,
            Origins = requested.ConvertAll(r => r.FromOrdinal),
        };
    }

    /// <summary>Per-slot state a moved chain link takes with it. Applied mappings are excluded: the route rewrites one per ordinal straight after.</summary>
    private sealed class CarriedSlot
    {
        public required string ZoneId { get; init; }
        public string? Name { get; init; }
        public Nexus.Service.Persistence.LightingDevicePreference? Pref { get; init; }
        public Nexus.Service.Persistence.DeviceLayout? Layout { get; init; }
        public List<Nexus.Service.Lighting.Mappings.MappingGroup>? Groups { get; init; }
        public bool Disabled { get; init; }
        public bool Uncontrolled { get; init; }
        public bool AutoApplyDeclined { get; init; }
    }

    /// <summary>Reads each surviving link's state under the ordinal it held, keyed to the ordinal it is moving to. Must run before <see cref="Nexus.Service.Lighting.Zones.ZoneStateDrop"/> clears the old slots.</summary>
    private static List<CarriedSlot> CarriedChainState(
        Nexus.Service.Persistence.NexusSettings settings, string deviceId, List<int?> origins)
    {
        var carried = new List<CarriedSlot>();
        for (int i = 0; i < origins.Count; i++)
        {
            if (origins[i] is not { } from) continue;
            var fromId = Nexus.Service.Lighting.Zones.ZoneResolution.CustomZoneId(deviceId, from);
            settings.Lighting.DeviceNames.TryGetValue(fromId, out var name);
            settings.Devices.LightingDevicePrefs.TryGetValue(fromId, out var pref);
            settings.Lighting.DeviceLayouts.TryGetValue(fromId, out var layout);
            settings.Devices.LedGroups.TryGetValue(fromId, out var groups);
            carried.Add(new CarriedSlot
            {
                ZoneId = Nexus.Service.Lighting.Zones.ZoneResolution.CustomZoneId(deviceId, i),
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                Pref = pref,
                Layout = layout,
                Groups = groups,
                Disabled = settings.Devices.DisabledLightingDevices.Contains(fromId),
                Uncontrolled = settings.Devices.UncontrolledLightingDevices.Contains(fromId),
                AutoApplyDeclined = settings.Devices.MappingAutoApplyDeclined.Contains(fromId),
            });
        }
        return carried;
    }

    /// <summary>Writes the carried state back under each link's new ordinal.</summary>
    private static void RestoreChainState(Nexus.Service.Persistence.NexusSettings settings, List<CarriedSlot> carried)
    {
        foreach (var slot in carried)
        {
            if (slot.Name is { } name) settings.Lighting.DeviceNames[slot.ZoneId] = name;
            if (slot.Pref is { } pref) settings.Devices.LightingDevicePrefs[slot.ZoneId] = pref;
            if (slot.Layout is { } layout) settings.Lighting.DeviceLayouts[slot.ZoneId] = layout;
            if (slot.Groups is { } groups) settings.Devices.LedGroups[slot.ZoneId] = groups;
            // Replaced rather than mutated: the frame writers read these lists
            // lock-free at frame rate.
            if (slot.Disabled && !settings.Devices.DisabledLightingDevices.Contains(slot.ZoneId))
                settings.Devices.DisabledLightingDevices = [.. settings.Devices.DisabledLightingDevices, slot.ZoneId];
            if (slot.Uncontrolled && !settings.Devices.UncontrolledLightingDevices.Contains(slot.ZoneId))
                settings.Devices.UncontrolledLightingDevices = [.. settings.Devices.UncontrolledLightingDevices, slot.ZoneId];
            if (slot.AutoApplyDeclined && !settings.Devices.MappingAutoApplyDeclined.Contains(slot.ZoneId))
                settings.Devices.MappingAutoApplyDeclined.Add(slot.ZoneId);
        }
    }

    /// <summary>
    /// Detached settings copy with a chain plan overlaid onto the live
    /// snapshot: the same references for everything the chain does not
    /// touch, freshly copied dictionaries for the four fields it writes.
    /// Never mutates the object <see cref="Nexus.Service.Persistence.IConfigStore.Load"/>
    /// returns, which the 30 Hz frame loop reads.
    /// </summary>
    private static Nexus.Service.Persistence.NexusSettings BuildChainPreviewSettings(
        Nexus.Service.Persistence.NexusSettings live, string deviceId, string chainKey, string defaultCardId, ChainPlan plan)
    {
        var portChains = new Dictionary<string, List<Nexus.Service.Persistence.ChainEntry>>(live.Devices.PortChains);
        var zonePartitions = new Dictionary<string, List<Nexus.Service.Persistence.ZoneDef>>(live.Devices.ZonePartitions);
        var zoneLedCounts = new Dictionary<string, int>(live.Devices.ZoneLedCounts);
        var appliedMappings = new Dictionary<string, AppliedMappingRef>(live.Devices.AppliedMappings);

        if (plan.Entries.Count == 0)
        {
            portChains.Remove(chainKey);
            zonePartitions.Remove(deviceId);
        }
        else
        {
            portChains[chainKey] = plan.Entries;
            zoneLedCounts[defaultCardId] = plan.Total;
            zonePartitions[deviceId] = plan.Defs;
            foreach (var (ordinal, artifact) in plan.Applied)
            {
                var zoneId = Nexus.Service.Lighting.Zones.ZoneResolution.CustomZoneId(deviceId, ordinal);
                appliedMappings[zoneId] = Nexus.Service.Lighting.Zones.PortChainWriter.AppliedRef(artifact);
            }
        }

        return new Nexus.Service.Persistence.NexusSettings
        {
            Devices = new Nexus.Service.Persistence.DevicesSettings
            {
                PortChains = portChains,
                ZonePartitions = zonePartitions,
                ZoneLedCounts = zoneLedCounts,
                AppliedMappings = appliedMappings,
                DeviceLedOverrides = live.Devices.DeviceLedOverrides,
                LedGroups = live.Devices.LedGroups,
                DeviceAspectRatios = live.Devices.DeviceAspectRatios,
            },
        };
    }

    /// <summary>
    /// Structure copy whose chained segment reports the plan's LED total, so
    /// the zone resolver tiles the preview's partition instead of the port's
    /// currently persisted count. Only called for a non-empty plan; clearing a
    /// chain leaves the segment's count untouched.
    /// </summary>
    private static Nexus.Service.Lighting.Zones.DeviceStructure ChainPreviewStructure(
        Nexus.Service.Lighting.Zones.DeviceStructure structure, int segment, int total)
    {
        var segments = new List<Nexus.Service.Lighting.Zones.StructureSegment>(structure.Segments);
        var seg = segments[segment];
        segments[segment] = new Nexus.Service.Lighting.Zones.StructureSegment
        {
            Index = seg.Index,
            Name = seg.Name,
            LedCount = total,
            FrameLedCount = total,
            Resizable = seg.Resizable,
            MaxLedCount = seg.MaxLedCount,
            ZoneType = seg.ZoneType,
            DefaultU = seg.DefaultU,
            DefaultV = seg.DefaultV,
        };
        return new Nexus.Service.Lighting.Zones.DeviceStructure
        {
            DeviceId = structure.DeviceId,
            Name = structure.Name,
            DeviceKey = structure.DeviceKey,
            Segments = segments,
            DefaultZones = structure.DefaultZones,
            Partitionable = structure.Partitionable,
            PhysicalDeviceId = structure.PhysicalDeviceId,
            FrameBaseOffset = structure.FrameBaseOffset,
        };
    }
}
