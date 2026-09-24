using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// Hardware-reported subdivision of a device's LED space. Resizable means the
/// protocol states the LED count is user-wired and can change (motherboard
/// ARGB headers, 12V channels), which would shift concatenated indices;
/// fixed counts never move. Flags are authored for first-party devices and
/// derived from controller data for OpenRGB. No physical inference anywhere.
/// </summary>
public sealed class StructureSegment
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Effective LED count shown to the user; for resizable segments the persisted resize choice wins over the hardware report.</summary>
    public int LedCount { get; set; }
    /// <summary>Hardware-reported count used for engine frame sizing and device-space offsets. Equals <see cref="LedCount"/> for fixed segments.</summary>
    public int FrameLedCount { get; set; }
    public bool Resizable { get; set; }
    /// <summary>
    /// Most LEDs this segment can carry, or 0 when nothing advertises one.
    /// A chain longer than this would persist, render cards and frames, and
    /// then be silently truncated by the writer.
    /// </summary>
    public int MaxLedCount { get; set; }
    /// <summary>"single" | "linear" | "matrix" - same vocabulary as the card DTO.</summary>
    public string ZoneType { get; set; } = "linear";
    /// <summary>
    /// Provider-authored stock per-LED positions in segment-local order
    /// (length == <see cref="FrameLedCount"/>), or null when the provider has
    /// no physical layout (the resolver's linear default applies). Treated as
    /// immutable: consumers slice or clone, never write.
    /// </summary>
    public float[]? DefaultU { get; set; }
    /// <summary>Paired with <see cref="DefaultU"/>.</summary>
    public float[]? DefaultV { get; set; }
}

/// <summary>
/// Provider-authored zone of the DEFAULT partition. Default zones keep the
/// LEGACY card ids, names, and device keys so existing prefs, layouts, and
/// applied mappings keep working untouched when no custom partition exists.
/// </summary>
public sealed class DefaultZoneDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Zone name without the device prefix (the segment default name, or "All" for a whole-device zone). Surfaced by the structure endpoint so the editor's zone rail doesn't repeat the device name; <see cref="Name"/> stays the card name.</summary>
    public string RawName { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    /// <summary>OpenRGB zone index the legacy card carried (drives the legacy resolution path); negative for whole-device and non-OpenRGB zones.</summary>
    public int LegacyZoneIndex { get; set; } = -1;
    public List<ZoneSlice> Slices { get; set; } = new();
}

/// <summary>
/// One partitionable device as exposed by its provider: a stable identity,
/// the hardware segments of its LED space, and the authored default
/// partition. Hub ports, smart lights, and other non-partitionable cards
/// never appear here.
/// </summary>
public sealed class DeviceStructure
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public List<StructureSegment> Segments { get; set; } = new();
    public List<DefaultZoneDef> DefaultZones { get; set; } = new();
    /// <summary>False when the provider builds its cards and frames from a fixed list rather than <see cref="ZoneResolution.Resolve"/>; the zone routes reject partition writes for those, since the saved zones would back no card while <see cref="ZoneStateDrop"/> had already dropped the legacy one's state.</summary>
    public bool Partitionable { get; set; } = true;
    /// <summary>
    /// Stable id of the physical controller this structure is carved out of.
    /// Equals <see cref="DeviceId"/> for a whole device; a split-motherboard
    /// port sets it to the board, which is the device its frames, LED names,
    /// and RESIZEZONE calls address. Empty falls back to <see cref="DeviceId"/>.
    /// </summary>
    public string PhysicalDeviceId { get; set; } = "";
    /// <summary>
    /// Offset of this structure's segment 0 inside the physical controller's
    /// LED buffer, in hardware-reported counts. Nonzero only for a carved-out
    /// port, whose slices are otherwise port-local and would address the first
    /// header's LEDs on every port.
    /// </summary>
    public int FrameBaseOffset { get; set; }
    /// <summary>Physical controller id, falling back to the device id for whole-device structures.</summary>
    public string OwningDeviceId => string.IsNullOrEmpty(PhysicalDeviceId) ? DeviceId : PhysicalDeviceId;
}

/// <summary>Providers with partitionable devices expose their structures through this; <see cref="ZoneTopology"/> aggregates all sources.</summary>
public interface IDeviceStructureSource
{
    /// <summary>Structures for the source's currently-connected partitionable devices.</summary>
    IReadOnlyList<DeviceStructure> GetStructures();
}

/// <summary>A zone resolved against the current partition (default or custom) of one device.</summary>
public sealed class ResolvedZone
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Raw zone name without the device prefix: the segment default name for default zones, the user-given name for custom zones. Card names keep the full <see cref="Name"/>.</summary>
    public string RawName { get; set; } = "";
    /// <summary>Cross-install fingerprint. Default zones keep the legacy card key; custom zones carry an empty key (community features hidden until device-scope artifacts land).</summary>
    public string DeviceKey { get; set; } = "";
    public int Ordinal { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>See <see cref="DefaultZoneDef.LegacyZoneIndex"/>; always negative for custom zones.</summary>
    public int LegacyZoneIndex { get; set; } = -1;
    /// <summary>Ordered slices in effective (user-facing) counts.</summary>
    public IReadOnlyList<ZoneSlice> Slices { get; set; } = Array.Empty<ZoneSlice>();
    /// <summary>Sum of effective slice counts (card LED count for custom zones).</summary>
    public int LedCount { get; set; }
    /// <summary>Sum of hardware-reported slice counts (engine frame size). Equals <see cref="LedCount"/> unless a resizable segment ignores resize on the wire.</summary>
    public int FrameLedCount { get; set; }
}

/// <summary>
/// Maps a card's zone-local LED indices into the owning device's stable
/// (segment, localIndex) override space. A null slice list is the identity
/// context used for non-partitionable cards: the card is its own
/// single-segment device.
/// </summary>
public sealed class ZoneOverrideContext
{
    public ZoneOverrideContext(string deviceId, IReadOnlyList<ZoneSlice>? slices)
    {
        DeviceId = deviceId;
        Slices = slices;
    }

    public static ZoneOverrideContext Identity(string deviceId) => new(deviceId, null);

    public string DeviceId { get; }
    public IReadOnlyList<ZoneSlice>? Slices { get; }

    /// <summary>Zone-local index for a (segment, localIndex) pair, or negative when the LED is outside this zone.</summary>
    public int MapFromSegment(int segment, int ledIndex)
    {
        if (Slices is null)
        {
            return segment == 0 ? ledIndex : -1;
        }
        var acc = 0;
        foreach (var slice in Slices)
        {
            if (slice.Segment == segment && ledIndex >= slice.Start && ledIndex < slice.Start + slice.Count)
            {
                return acc + (ledIndex - slice.Start);
            }
            acc += slice.Count;
        }
        return -1;
    }

    /// <summary>(segment, localIndex) for a zone-local index; false when out of range.</summary>
    public bool TryMapToSegment(int zoneLocal, out int segment, out int localIndex)
    {
        if (zoneLocal >= 0)
        {
            if (Slices is null)
            {
                segment = 0;
                localIndex = zoneLocal;
                return true;
            }
            var acc = 0;
            foreach (var slice in Slices)
            {
                if (zoneLocal < acc + slice.Count)
                {
                    segment = slice.Segment;
                    localIndex = slice.Start + (zoneLocal - acc);
                    return true;
                }
                acc += slice.Count;
            }
        }
        segment = -1;
        localIndex = -1;
        return false;
    }
}
