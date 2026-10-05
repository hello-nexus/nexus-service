using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Single resolution pipeline for per-LED layout, replacing the logic that
/// was previously duplicated between the led-map GET route, the engine
/// refresh helper, and RgbBridge's frame build - and extending it to
/// first-party / contributor devices (which previously had no layout
/// resolution at all). Layering, lowest to highest precedence:
///
///   1. computed default   - matrix UVs from OpenRGB metadata, else linear
///   2. applied mapping    - community / file artifact zone (positions,
///                           disabled, groups). LED-count changes are an
///                           apply-time side effect through the existing
///                           zone-resize path, never resolved here.
///   3. user delta         - DeviceLedOverrides positions/disabled (resolved
///                           through the zone's segment slices) and the
///                           LedGroups dict; always wins.
/// </summary>
public static class LedLayoutResolver
{
    /// <summary>OpenRGB device or default-partition zone card. <paramref name="overrides"/> maps the card into the device's segment-local override space; null treats the card as its own single-segment device.</summary>
    public static ResolvedLedLayout ResolveOpenRgb(RgbDevice device, int zoneIndex, string id, NexusSettings settings, ZoneOverrideContext? overrides = null)
    {
        var layout = new ResolvedLedLayout { Id = id, ZoneHint = zoneIndex };
        if (zoneIndex >= 0 && zoneIndex < device.Zones.Count)
        {
            var zone = device.Zones[zoneIndex];
            // Some single-zone headers ignore RESIZEZONE on the wire, so the
            // persisted count wins over what OpenRGB keeps reporting; the
            // editor's count field then stays in sync with the device card.
            layout.LedCount = settings.Devices.ZoneLedCounts.TryGetValue(id, out var persisted)
                ? persisted
                : zone.LedCount;
            layout.GlobalOffset = 0;
            for (int z = 0; z < zoneIndex; z++)
                layout.GlobalOffset += device.Zones[z].LedCount;
            FillLinearDefaults(layout);
            var zoneType = ZoneTypeName(zone.ZoneType);
            layout.ZoneTypes = new string[layout.LedCount];
            for (int i = 0; i < layout.LedCount; i++)
                layout.ZoneTypes[i] = zoneType;
        }
        else
        {
            layout.LedCount = device.LedCount;
            var (defU, defV) = LedUvComputer.ComputeDefaults(device);
            if (defU.Length == layout.LedCount && defU.Length > 0)
            {
                layout.U = defU;
                layout.V = defV;
            }
            else
            {
                FillLinearDefaults(layout);
            }
            layout.ZoneTypes = BuildZoneTypeMap(device);
        }
        ApplyMappingAndDeltas(layout, settings, overrides ?? ZoneOverrideContext.Identity(id));
        return layout;
    }

    /// <summary>
    /// Custom-partition zone over an OpenRGB device. The zone is one
    /// contiguous device-space run (validation rule), so defaults slice the
    /// device-level computed UVs when the zone's hardware indices are all
    /// addressable; resizable headers (whose effective count can exceed what
    /// the hardware reports) fall back to a linear strip, matching the legacy
    /// zone-card behavior.
    /// </summary>
    public static ResolvedLedLayout ResolveZoneOpenRgb(RgbDevice device, DeviceStructure structure, ResolvedZone zone, NexusSettings settings)
    {
        var layout = new ResolvedLedLayout
        {
            Id = zone.Id,
            LedCount = zone.LedCount,
            ZoneHint = zone.Ordinal,
            GlobalOffset = ZoneResolution.FrameOffset(structure, zone),
        };
        var (defU, defV) = LedUvComputer.ComputeDefaults(device);
        var sliceable = defU.Length == device.LedCount
            && defU.Length > 0
            && layout.GlobalOffset + zone.LedCount <= defU.Length
            && zone.LedCount == zone.FrameLedCount;
        if (sliceable)
        {
            layout.U = new float[zone.LedCount];
            layout.V = new float[zone.LedCount];
            for (int i = 0; i < zone.LedCount; i++)
            {
                layout.U[i] = defU[layout.GlobalOffset + i];
                layout.V[i] = defV[layout.GlobalOffset + i];
            }
        }
        else
        {
            FillLinearDefaults(layout);
        }
        layout.ZoneTypes = new string[Math.Max(0, zone.LedCount)];
        var pos = 0;
        foreach (var slice in zone.Slices)
        {
            var typeName = slice.Segment >= 0 && slice.Segment < structure.Segments.Count
                ? structure.Segments[slice.Segment].ZoneType
                : "unknown";
            for (int i = 0; i < slice.Count && pos < layout.ZoneTypes.Length; i++)
                layout.ZoneTypes[pos++] = typeName;
        }
        while (pos < layout.ZoneTypes.Length)
            layout.ZoneTypes[pos++] = "unknown";
        ApplyMappingAndDeltas(layout, settings, ZoneResolution.ContextOf(structure, zone));
        return layout;
    }

    /// <summary>
    /// First-party / contributor card (NP50 module, hub port, smart light,
    /// CNVS...). Seeds from the provider-authored UV defaults when available
    /// (smart-light sample grids, future matrix layouts) so a partial user
    /// edit never collapses the rest of the device to a line; falls back to
    /// a linear strip. Artifacts for these cards use zone index 0.
    /// </summary>
    public static ResolvedLedLayout ResolveSeeded(string id, int ledCount, float[]? seedU, float[]? seedV, NexusSettings settings, ZoneOverrideContext? overrides = null)
    {
        var layout = new ResolvedLedLayout { Id = id, LedCount = ledCount, ZoneHint = 0 };
        var seeded = seedU is not null && seedV is not null
            && seedU.Length == ledCount && seedV.Length == ledCount;
        if (seeded)
        {
            layout.U = (float[])seedU!.Clone();
            layout.V = (float[])seedV!.Clone();
        }
        else
        {
            FillLinearDefaults(layout);
        }
        layout.ZoneTypes = new string[Math.Max(0, ledCount)];
        for (int i = 0; i < layout.ZoneTypes.Length; i++)
            layout.ZoneTypes[i] = seeded ? "matrix" : "linear";
        ApplyMappingAndDeltas(layout, settings, overrides ?? ZoneOverrideContext.Identity(id));
        return layout;
    }

    /// <summary>Push resolved positions into a live engine frame. Length mismatches (stale count) leave the frame untouched, matching the engine's own sampling guard.</summary>
    public static void ApplyToFrame(DeviceFrame frame, ResolvedLedLayout layout)
    {
        if (layout.U.Length != frame.LedCount || layout.V.Length != frame.LedCount)
            return;
        frame.LedU = layout.U;
        frame.LedV = layout.V;
        frame.LedDisabled = layout.Disabled;
    }

    /// <summary>
    /// Pick the artifact zone for a card. v1 artifacts are per-card single
    /// zone; selection stays lenient for registry artifacts that describe a
    /// whole multi-zone device.
    /// </summary>
    public static MappingZone? SelectZone(MappingArtifact artifact, int zoneIndex)
    {
        foreach (var zone in artifact.Zones)
        {
            if (zone.ZoneIndex == zoneIndex)
                return zone;
        }
        if (artifact.Zones.Count == 1)
            return artifact.Zones[0];
        foreach (var zone in artifact.Zones)
        {
            if (zone.ZoneIndex == 0)
                return zone;
        }
        return artifact.Zones.Count > 0 ? artifact.Zones[0] : null;
    }

    private static void ApplyMappingAndDeltas(ResolvedLedLayout layout, NexusSettings settings, ZoneOverrideContext overrides)
    {
        // Layer 2: applied mapping artifact.
        if (settings.Devices.AppliedMappings.TryGetValue(layout.Id, out var applied))
        {
            layout.Applied = applied;
            var zone = SelectZone(BuiltInMappingsCatalog.Current(applied), layout.ZoneHint);
            if (zone is not null)
            {
                foreach (var led in zone.Leds)
                {
                    if (led.I >= 0 && led.I < layout.LedCount)
                    {
                        layout.U[led.I] = led.U;
                        layout.V[led.I] = led.V;
                    }
                }
                foreach (var di in zone.Disabled)
                {
                    if (di >= 0 && di < layout.LedCount)
                    {
                        layout.Disabled ??= new bool[layout.LedCount];
                        layout.Disabled[di] = true;
                    }
                }
                if (zone.Groups.Count > 0)
                    layout.Groups = zone.Groups;
                if (zone.AspectRatio is { } ar && ar > 0)
                    layout.AspectRatio = ar;
            }
        }

        // Layer 3: user deltas always win. Overrides live in the device's
        // stable (segment, localIndex) space; the context maps them into
        // this card's zone-local indices regardless of partition shape.
        if (settings.Devices.DeviceLedOverrides.TryGetValue(overrides.DeviceId, out var deviceOverrides)
            && deviceOverrides.Count > 0)
        {
            var anyDisabled = layout.Disabled is not null;
            var mapped = new List<(int index, SegmentLedOverride o)>(deviceOverrides.Count);
            foreach (var o in deviceOverrides)
            {
                var idx = overrides.MapFromSegment(o.Segment, o.LedIndex);
                if (idx < 0 || idx >= layout.LedCount)
                    continue;
                mapped.Add((idx, o));
            }
            if (mapped.Count > 0)
                layout.HasUserOverrides = true;
            foreach (var (idx, o) in mapped)
            {
                layout.CustomLeds.Add(idx);
                layout.U[idx] = o.U;
                layout.V[idx] = o.V;
                if (o.Disabled)
                    anyDisabled = true;
            }
            if (anyDisabled && mapped.Count > 0)
            {
                // A user override is authoritative for its LED in both
                // directions (it can re-enable an LED a mapping disabled);
                // LEDs without overrides keep the mapping-layer state.
                layout.Disabled ??= new bool[layout.LedCount];
                foreach (var (idx, o) in mapped)
                    layout.Disabled[idx] = o.Disabled;
            }
        }
        if (settings.Devices.LedGroups.TryGetValue(layout.Id, out var userGroups))
            layout.Groups = userGroups;
        if (settings.Devices.DeviceAspectRatios.TryGetValue(overrides.DeviceId, out var savedRatio) && savedRatio > 0)
            layout.AspectRatio = savedRatio;

        // An all-false disabled array downgrades to null so the engine takes
        // its zero-overhead fast path.
        if (layout.Disabled is { } flags)
        {
            var any = false;
            foreach (var f in flags)
            {
                if (f) { any = true; break; }
            }
            if (!any)
                layout.Disabled = null;
        }
    }

    private static void FillLinearDefaults(ResolvedLedLayout layout)
    {
        var n = Math.Max(0, layout.LedCount);
        layout.U = new float[n];
        layout.V = new float[n];
        for (int i = 0; i < n; i++)
        {
            layout.U[i] = n > 1 ? (float)i / (n - 1) : 0.5f;
            layout.V[i] = 0.5f;
        }
    }

    private static string ZoneTypeName(uint zoneType) => zoneType switch
    {
        0 => "single",
        1 => "linear",
        2 => "matrix",
        _ => "unknown",
    };

    private static string[] BuildZoneTypeMap(RgbDevice device)
    {
        var result = new string[Math.Max(0, device.LedCount)];
        int offset = 0;
        foreach (var z in device.Zones)
        {
            var typeName = ZoneTypeName(z.ZoneType);
            for (int i = 0; i < z.LedCount && offset + i < result.Length; i++)
                result[offset + i] = typeName;
            offset += z.LedCount;
        }
        for (int i = 0; i < result.Length; i++)
            result[i] ??= "unknown";
        return result;
    }
}

public sealed class ResolvedLedLayout
{
    public string Id { get; set; } = "";
    public int LedCount { get; set; }
    public float[] U { get; set; } = Array.Empty<float>();
    public float[] V { get; set; } = Array.Empty<float>();
    /// <summary>Null = no LED disabled (engine fast path).</summary>
    public bool[]? Disabled { get; set; }
    public List<MappingGroup> Groups { get; set; } = new();
    /// <summary>Per-LED zone type name for the editor response.</summary>
    public string[] ZoneTypes { get; set; } = Array.Empty<string>();
    /// <summary>LED indices touched by user overrides (editor renders these as custom).</summary>
    public HashSet<int> CustomLeds { get; } = new();
    public float AspectRatio { get; set; }
    public bool HasUserOverrides { get; set; }
    public AppliedMappingRef? Applied { get; set; }
    /// <summary>LED offset of this zone within the physical device's LED-name array.</summary>
    public int GlobalOffset { get; set; }
    /// <summary>Zone index used to select the artifact zone; negative for whole-device cards.</summary>
    public int ZoneHint { get; set; } = -1;
}
