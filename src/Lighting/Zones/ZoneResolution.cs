using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// Resolves a device's current partition (persisted custom zones or the
/// provider-authored default) into the zones that become cards and engine
/// frames. Custom zones carry "{deviceId}:z{ordinal}" ids and
/// "{DeviceName} - {ZoneName}" names; default zones keep their legacy
/// identities. Every zone also carries a RawName (segment default name or
/// the user-given name) for surfaces that already show the device context.
/// A persisted partition that no longer validates against the live segments
/// (e.g. a fixed count changed across firmware) self-heals by falling back
/// to the default partition.
/// </summary>
public static class ZoneResolution
{
    public static IReadOnlyList<ResolvedZone> Resolve(DeviceStructure structure, NexusSettings settings)
    {
        if (settings.Devices.ZonePartitions.TryGetValue(structure.DeviceId, out var defs)
            && defs is { Count: > 0 })
        {
            var chained = ChainOwnedSegments(structure, settings);
            var normalized = NormalizeDefs(structure, defs, chained);
            if (ZonePartitionValidator.Validate(structure.Segments, normalized, chained).Ok)
            {
                return BuildCustom(structure, normalized);
            }
        }
        return BuildDefaults(structure);
    }

    /// <summary>Key for a port's chain record: the device plus the segment it is wired to.</summary>
    public static string ChainKey(string deviceId, int segment) => $"{deviceId}:seg{segment}";

    /// <summary>
    /// Forget every chain wired to a device. A chain record only means
    /// something alongside the partition it wrote, so any path that replaces
    /// or clears that partition must drop it too - otherwise the port keeps
    /// reporting products the zones no longer match, and rule 2 stays lifted
    /// for a segment nothing owns. Returns true when anything was removed.
    /// </summary>
    public static bool DropChains(NexusSettings settings, DeviceStructure structure)
    {
        var dropped = false;
        for (int i = 0; i < structure.Segments.Count; i++)
        {
            dropped |= settings.Devices.PortChains.Remove(ChainKey(structure.DeviceId, i));
        }
        return dropped;
    }

    /// <summary>
    /// A hand-typed count replaces what the chain declared, so the chain record,
    /// its partition and the per-slot state of its cards go together; a slot's
    /// name, layout or control state left behind resurfaces on the next chain.
    /// </summary>
    public static void DropChainForCount(NexusSettings settings, string deviceId)
    {
        if (!settings.Devices.PortChains.Remove(ChainKey(deviceId, 0))) return;
        if (!settings.Devices.ZonePartitions.Remove(deviceId, out var partition)) return;
        var zoneIds = new List<string>(partition.Count);
        for (int i = 0; i < partition.Count; i++)
            zoneIds.Add(CustomZoneId(deviceId, i));
        ZoneStateDrop.Drop(settings, zoneIds);
        foreach (var zoneId in zoneIds)
            settings.Lighting.DeviceNames.Remove(zoneId);
    }

    /// <summary>
    /// Segments whose LED count is owned by a product chain, so a multi-zone
    /// partition over them is legitimate. A segment is owned when its chain
    /// key holds a non-empty product list.
    /// </summary>
    public static IReadOnlySet<int> ChainOwnedSegments(DeviceStructure structure, NexusSettings settings)
    {
        var owned = new HashSet<int>();
        for (int i = 0; i < structure.Segments.Count; i++)
        {
            if (settings.Devices.PortChains.TryGetValue(ChainKey(structure.DeviceId, i), out var chain)
                && chain is { Count: > 0 })
            {
                owned.Add(i);
            }
        }
        return owned;
    }

    /// <summary>
    /// Normalization applied before validation and persistence: names are
    /// trimmed, and a whole-segment slice over a resizable segment tracks the
    /// segment's LIVE count (the stored count goes stale whenever the user
    /// resizes the header afterward; rule 2 pins such slices to the whole
    /// segment, so substituting the live count is sound).
    ///
    /// A segment a chain owns is the exception: its slices are deliberately
    /// partial, and the first one starts at 0 like any other. Substituting the
    /// whole count there would stretch the first product over the entire port
    /// and overlap the rest, so the partition would fail validation and the
    /// port would silently fall back to one zone.
    /// </summary>
    public static List<ZoneDef> NormalizeDefs(DeviceStructure structure, IReadOnlyList<ZoneDef> defs)
        => NormalizeDefs(structure, defs, chainOwnedSegments: null);

    public static List<ZoneDef> NormalizeDefs(DeviceStructure structure, IReadOnlyList<ZoneDef> defs,
        IReadOnlySet<int>? chainOwnedSegments)
    {
        var result = new List<ZoneDef>(defs.Count);
        foreach (var def in defs)
        {
            var zone = new ZoneDef { Name = def.Name?.Trim() ?? "" };
            if (def.Slices is not null)
            {
                foreach (var slice in def.Slices)
                {
                    var count = slice.Count;
                    if (slice.Segment >= 0 && slice.Segment < structure.Segments.Count)
                    {
                        var seg = structure.Segments[slice.Segment];
                        var chained = chainOwnedSegments?.Contains(slice.Segment) == true;
                        if (seg.Resizable && !chained && slice.Start == 0)
                        {
                            count = seg.LedCount;
                        }
                    }
                    zone.Slices.Add(new ZoneSlice { Segment = slice.Segment, Start = slice.Start, Count = count });
                }
            }
            result.Add(zone);
        }
        return result;
    }

    private static IReadOnlyList<ResolvedZone> BuildDefaults(DeviceStructure structure)
    {
        var zones = new List<ResolvedZone>(structure.DefaultZones.Count);
        for (int i = 0; i < structure.DefaultZones.Count; i++)
        {
            var def = structure.DefaultZones[i];
            zones.Add(new ResolvedZone
            {
                Id = def.Id,
                Name = def.Name,
                RawName = string.IsNullOrEmpty(def.RawName) ? def.Name : def.RawName,
                DeviceKey = def.DeviceKey,
                Ordinal = i,
                IsDefault = true,
                LegacyZoneIndex = def.LegacyZoneIndex,
                Slices = def.Slices,
                LedCount = SumCounts(def.Slices),
                FrameLedCount = SumFrameCounts(structure, def.Slices),
            });
        }
        return zones;
    }

    private static IReadOnlyList<ResolvedZone> BuildCustom(DeviceStructure structure, List<ZoneDef> defs)
    {
        var zones = new List<ResolvedZone>(defs.Count);
        for (int i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            zones.Add(new ResolvedZone
            {
                Id = CustomZoneId(structure.DeviceId, i),
                Name = $"{structure.Name} - {def.Name}",
                RawName = def.Name,
                DeviceKey = "",
                Ordinal = i,
                IsDefault = false,
                LegacyZoneIndex = -1,
                Slices = def.Slices,
                LedCount = SumCounts(def.Slices),
                FrameLedCount = SumFrameCounts(structure, def.Slices),
            });
        }
        return zones;
    }

    public static string CustomZoneId(string deviceId, int ordinal) => $"{deviceId}:z{ordinal}";

    /// <summary>
    /// Provider-authored stock positions for a zone: the concatenation of its
    /// slices' segment-default spans in zone-local order, so any partition
    /// shape keeps each zone's true sub-shape. Null when any covered segment
    /// has no authored defaults, a span falls outside them, or the effective
    /// and frame counts diverge (resizable headers) - the resolver's linear
    /// default applies then.
    /// </summary>
    public static (float[]? U, float[]? V) DefaultUv(DeviceStructure structure, ResolvedZone zone)
    {
        if (zone.Slices.Count == 0 || zone.LedCount <= 0 || zone.LedCount != zone.FrameLedCount)
        {
            return (null, null);
        }
        var u = new float[zone.LedCount];
        var v = new float[zone.LedCount];
        var pos = 0;
        foreach (var slice in zone.Slices)
        {
            if (slice.Segment < 0 || slice.Segment >= structure.Segments.Count)
            {
                return (null, null);
            }
            var seg = structure.Segments[slice.Segment];
            if (seg.DefaultU is null || seg.DefaultV is null
                || slice.Start < 0 || slice.Count < 0
                || slice.Start + slice.Count > seg.DefaultU.Length
                || slice.Start + slice.Count > seg.DefaultV.Length
                || pos + slice.Count > u.Length)
            {
                return (null, null);
            }
            Array.Copy(seg.DefaultU, slice.Start, u, pos, slice.Count);
            Array.Copy(seg.DefaultV, slice.Start, v, pos, slice.Count);
            pos += slice.Count;
        }
        return pos == zone.LedCount ? (u, v) : (null, null);
    }

    public static ZoneOverrideContext ContextOf(DeviceStructure structure, ResolvedZone zone)
        => new(structure.DeviceId, zone.Slices);

    /// <summary>
    /// Count of the card's LEDs not disabled by the resolved layout stack,
    /// mirroring the disabled layering in
    /// <see cref="LedLayoutResolver"/>: the applied mapping's
    /// disabled set first, then user overrides mapped through the zone's
    /// slices into zone-local space - an override wins in both directions
    /// (it can re-enable a mapping-disabled LED). Null structure/zone is the
    /// non-partitionable card path: the card is its own single-segment
    /// device (identity context). No disable data = <paramref name="ledCount"/>.
    /// <paramref name="zoneHint"/> MUST be the artifact zone hint the render
    /// path resolves this card with (ResolveOpenRgb / ResolveZoneOpenRgb for
    /// bridge cards; ResolveSeeded, always zero, for contributor cards) so
    /// the card's count agrees with the actually-lit LEDs - right or wrong -
    /// when a multi-zone artifact makes the selection ambiguous.
    /// </summary>
    public static int CountEnabled(DeviceStructure? structure, ResolvedZone? zone, string cardId, int ledCount, int zoneHint, NexusSettings settings)
    {
        if (ledCount <= 0)
        {
            return 0;
        }

        bool[]? disabled = null;

        // Mapping layer: artifact disabled indices are already zone-local;
        // the caller-supplied hint keeps zone selection identical to the
        // render path's.
        if (settings.Devices.AppliedMappings.TryGetValue(cardId, out var applied))
        {
            var mappingZone = LedLayoutResolver.SelectZone(applied.Artifact, zoneHint);
            if (mappingZone is not null)
            {
                foreach (var di in mappingZone.Disabled)
                {
                    if (di >= 0 && di < ledCount)
                    {
                        disabled ??= new bool[ledCount];
                        disabled[di] = true;
                    }
                }
            }
        }

        // User layer: overrides live in the device's stable (segment, local)
        // space; the context maps them into this card's zone-local indices.
        var ctx = structure is not null && zone is not null
            ? ContextOf(structure, zone)
            : ZoneOverrideContext.Identity(cardId);
        if (settings.Devices.DeviceLedOverrides.TryGetValue(ctx.DeviceId, out var deviceOverrides))
        {
            foreach (var o in deviceOverrides)
            {
                var idx = ctx.MapFromSegment(o.Segment, o.LedIndex);
                if (idx < 0 || idx >= ledCount)
                {
                    continue;
                }
                if (o.Disabled)
                {
                    disabled ??= new bool[ledCount];
                }
                disabled?[idx] = o.Disabled;
            }
        }

        if (disabled is null)
        {
            return ledCount;
        }
        var enabled = 0;
        foreach (var f in disabled)
        {
            if (!f)
            {
                enabled++;
            }
        }
        return enabled;
    }

    /// <summary>
    /// Device-space offset (in hardware-reported counts) of the zone's first
    /// LED. Rule 3 guarantees every zone is one contiguous device-space run,
    /// so offset plus <see cref="ResolvedZone.FrameLedCount"/> fully describes
    /// the zone for frame composition.
    /// </summary>
    public static int FrameOffset(DeviceStructure structure, ResolvedZone zone)
    {
        if (zone.Slices.Count == 0)
        {
            return structure.FrameBaseOffset;
        }
        var first = zone.Slices[0];
        var offset = structure.FrameBaseOffset;
        for (int i = 0; i < first.Segment && i < structure.Segments.Count; i++)
        {
            offset += structure.Segments[i].FrameLedCount;
        }
        return offset + first.Start;
    }

    /// <summary>True when every one of a device's currently resolved zones is in the uncontrolled set, so the whole physical device can be handed back to firmware. Works under a custom partition because it checks the live resolved zone ids rather than a provider's default card id.</summary>
    public static bool IsFullyUncontrolled(IReadOnlyList<ResolvedZone> zones, IReadOnlyList<string> uncontrolled)
    {
        if (zones.Count == 0 || uncontrolled.Count == 0)
        {
            return false;
        }
        foreach (var zone in zones)
        {
            if (!uncontrolled.Contains(zone.Id))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True when every currently resolved zone touching a segment is uncontrolled, so that segment's own wire write can be skipped. False when no zone touches the segment (nothing to write either way) or any touching zone (including one spanning other segments) is still controlled.</summary>
    public static bool IsSegmentFullyUncontrolled(IReadOnlyList<ResolvedZone> zones, int segment, IReadOnlyList<string> uncontrolled)
    {
        if (uncontrolled.Count == 0)
        {
            return false;
        }
        var touchesSegment = false;
        foreach (var zone in zones)
        {
            var touches = false;
            foreach (var slice in zone.Slices)
            {
                if (slice.Segment == segment)
                {
                    touches = true;
                    break;
                }
            }
            if (!touches)
            {
                continue;
            }
            touchesSegment = true;
            if (!uncontrolled.Contains(zone.Id))
            {
                return false;
            }
        }
        return touchesSegment;
    }

    /// <summary>
    /// The segment a zone wholly covers when it is a single whole-resizable-segment
    /// zone (rule 2 shape); negative otherwise, which is what gates the LED-count
    /// editor and the RESIZEZONE path.
    ///
    /// Two shapes must NOT qualify. A chain's first link also starts at 0, so
    /// without the count check resizing it would resize the entire header to
    /// one product's count. And a ONE-link chain does cover the whole segment,
    /// so the settings are needed too: resizing it would restate a count the
    /// chain owns, the partition would stop tiling, and the port would fall
    /// back to a single zone leaving the chain record and the per-zone applied
    /// mappings describing zones that no longer exist.
    /// </summary>
    public static int WholeResizableSegment(DeviceStructure structure, ResolvedZone zone, NexusSettings settings)
    {
        if (zone.Slices.Count != 1)
        {
            return -1;
        }
        var slice = zone.Slices[0];
        if (slice.Segment < 0 || slice.Segment >= structure.Segments.Count)
        {
            return -1;
        }
        if (settings.Devices.PortChains.ContainsKey(ChainKey(structure.DeviceId, slice.Segment)))
        {
            return -1;
        }
        var seg = structure.Segments[slice.Segment];
        return seg.Resizable && slice.Start == 0 && slice.Count == seg.LedCount ? seg.Index : -1;
    }

    private static int SumCounts(IReadOnlyList<ZoneSlice> slices)
    {
        var sum = 0;
        foreach (var s in slices)
        {
            sum += s.Count;
        }
        return sum;
    }

    private static int SumFrameCounts(DeviceStructure structure, IReadOnlyList<ZoneSlice> slices)
    {
        var sum = 0;
        foreach (var s in slices)
        {
            if (s.Segment >= 0 && s.Segment < structure.Segments.Count)
            {
                var seg = structure.Segments[s.Segment];
                // Whole-resizable slices size frames from the hardware report;
                // partial slices only exist on fixed segments where the two
                // counts are identical.
                sum += seg.Resizable && s.Start == 0 && s.Count == seg.LedCount
                    ? seg.FrameLedCount
                    : s.Count;
            }
        }
        return sum;
    }
}
