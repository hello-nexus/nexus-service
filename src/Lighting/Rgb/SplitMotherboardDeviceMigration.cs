using System.Collections.Generic;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// One-shot rekey for the move that made each ARGB header its own device.
///
/// Per-card settings needed nothing: a port's device id is the id its card
/// already had ("{stableId}-{z}"). Only the DEVICE-scoped dictionaries were
/// keyed on the parent controller, holding all its headers at once - per-LED
/// overrides addressed by segment, one aspect ratio, one partition. Those are
/// split out here, per header.
///
/// Runs against live hardware rather than at settings load, because only the
/// detected controller says whether an OpenRGB id is a split motherboard; a
/// two-zone GPU keys its overrides the same way and must be left alone.
/// </summary>
public static class SplitMotherboardDeviceMigration
{
    /// <summary>
    /// True when any split motherboard still has device-scoped state under the
    /// parent id. Read-only on purpose: <see cref="IConfigStore.Load"/> hands
    /// back the SHARED settings instance, so probing by running the move would
    /// mutate dictionaries the 30 Hz frame loop is reading, outside the store
    /// lock. Callers gate <see cref="Apply"/> on this and run it inside Update.
    /// </summary>
    public static bool NeedsApply(NexusSettings settings, IReadOnlyList<RgbDevice> devices)
    {
        foreach (var d in devices)
        {
            if (!OpenRgbZoneSupport.IsSplitMotherboard(d))
                continue;
            if (settings.Devices.DeviceLedOverrides.ContainsKey(d.StableId)
                || settings.Devices.DeviceAspectRatios.ContainsKey(d.StableId)
                || settings.Devices.ZonePartitions.ContainsKey(d.StableId))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when anything was moved, so the caller can persist once for the whole sweep.</summary>
    public static bool Apply(NexusSettings settings, IReadOnlyList<RgbDevice> devices)
    {
        var changed = false;
        foreach (var d in devices)
        {
            if (!OpenRgbZoneSupport.IsSplitMotherboard(d))
                continue;
            changed |= MoveOverrides(settings, d);
            changed |= MoveAspectRatio(settings, d);
            // A parent partition described a tiling across every header, which
            // no longer has a device to belong to. Dropping it restores the
            // default per-port zone rather than leaving a partition that can
            // never validate.
            changed |= settings.Devices.ZonePartitions.Remove(d.StableId);
        }
        return changed;
    }

    private static bool MoveOverrides(NexusSettings settings, RgbDevice d)
    {
        var overrides = settings.Devices.DeviceLedOverrides;
        if (!overrides.TryGetValue(d.StableId, out var entries) || entries is null || entries.Count == 0)
            return overrides.Remove(d.StableId);

        var bySegment = new Dictionary<int, List<SegmentLedOverride>>();
        foreach (var entry in entries)
        {
            if (!bySegment.TryGetValue(entry.Segment, out var list))
            {
                list = new List<SegmentLedOverride>();
                bySegment[entry.Segment] = list;
            }
            // The port owns one segment, so every override on it is segment 0.
            list.Add(new SegmentLedOverride
            {
                Segment = 0,
                LedIndex = entry.LedIndex,
                U = entry.U,
                V = entry.V,
                Disabled = entry.Disabled,
            });
        }

        foreach (var (segment, list) in bySegment)
        {
            var portId = $"{d.StableId}-{segment}";
            // A port that already has its own entries was written after the
            // move; the parent copy is stale and must not clobber it.
            if (!settings.Devices.DeviceLedOverrides.ContainsKey(portId))
                settings.Devices.DeviceLedOverrides[portId] = list;
        }
        settings.Devices.DeviceLedOverrides.Remove(d.StableId);
        return true;
    }

    private static bool MoveAspectRatio(NexusSettings settings, RgbDevice d)
    {
        var ratios = settings.Devices.DeviceAspectRatios;
        if (!ratios.TryGetValue(d.StableId, out var ratio))
            return false;
        for (int z = 0; z < d.Zones.Count; z++)
        {
            var portId = $"{d.StableId}-{z}";
            if (!ratios.ContainsKey(portId))
                ratios[portId] = ratio;
        }
        ratios.Remove(d.StableId);
        return true;
    }
}
