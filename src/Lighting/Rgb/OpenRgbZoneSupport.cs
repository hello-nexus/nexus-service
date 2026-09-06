using System.Collections.Generic;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Zone structure and card emission for OpenRGB-backed devices. One
/// structure per controller; segments mirror the controller's zones.
/// Resizable is true only for split-motherboard headers (the single surface
/// our RESIZEZONE path can actually re-wire); every other controller zone is
/// fixed because nothing in the system changes its count, so partitions over
/// them are index-stable. Single-zone (and zoneless) controllers expose one
/// fixed segment covering the device. The default partition reproduces
/// today's cards exactly: per-header cards for split motherboards, one
/// whole-device card for everything else.
/// </summary>
public static class OpenRgbZoneSupport
{
    public static bool IsSplitMotherboard(RgbDevice d) => d.Type == 0 && d.Zones.Count > 1;

    public static DeviceStructure BuildStructure(RgbDevice d, NexusSettings settings)
    {
        var baseId = d.StableId;
        var baseKey = DeviceKeyComputer.ForOpenRgbDevice(d);
        var split = IsSplitMotherboard(d);
        var zoneLedCounts = settings.Devices.ZoneLedCounts;

        var structure = new DeviceStructure
        {
            DeviceId = baseId,
            Name = d.Name,
            DeviceKey = baseKey,
        };

        if (d.Zones.Count == 0)
        {
            structure.Segments.Add(new StructureSegment
            {
                Index = 0,
                Name = d.Name,
                LedCount = d.LedCount,
                FrameLedCount = d.LedCount,
                Resizable = false,
                ZoneType = "linear",
            });
        }
        else
        {
            for (int z = 0; z < d.Zones.Count; z++)
            {
                var zone = d.Zones[z];
                // Effective count: the persisted resize choice wins over what
                // OpenRGB keeps reporting (12V headers ignore RESIZEZONE on
                // the wire). Only split motherboards ever have persisted
                // entries; their keys are the legacy zone-card ids.
                var raw = zone.LedCount;
                var effective = split && zoneLedCounts.TryGetValue($"{baseId}-{z}", out var persisted)
                    ? persisted
                    : raw;
                structure.Segments.Add(new StructureSegment
                {
                    Index = z,
                    Name = string.IsNullOrWhiteSpace(zone.Name) ? $"Zone {z + 1}" : zone.Name,
                    LedCount = effective,
                    FrameLedCount = raw,
                    Resizable = split && IsZoneResizable(zone.ZoneType),
                    ZoneType = ZoneTypeName(zone.ZoneType),
                });
            }
        }

        if (split)
        {
            for (int z = 0; z < structure.Segments.Count; z++)
            {
                structure.DefaultZones.Add(new DefaultZoneDef
                {
                    Id = $"{baseId}-{z}",
                    Name = BuildZoneName(d.Name, d.Zones[z].Name, z),
                    RawName = structure.Segments[z].Name,
                    DeviceKey = DeviceKeyComputer.ForZone(baseKey, z),
                    LegacyZoneIndex = z,
                    Slices = { new ZoneSlice { Segment = z, Start = 0, Count = structure.Segments[z].LedCount } },
                });
            }
        }
        else
        {
            var whole = new DefaultZoneDef
            {
                Id = baseId,
                Name = d.Name,
                RawName = "All",
                DeviceKey = baseKey,
                LegacyZoneIndex = -1,
            };
            foreach (var seg in structure.Segments)
            {
                whole.Slices.Add(new ZoneSlice { Segment = seg.Index, Start = 0, Count = seg.LedCount });
            }
            structure.DefaultZones.Add(whole);
        }

        return structure;
    }

    /// <summary>True when every card this device would emit is in the uncontrolled set, so the whole physical device should be left off direct mode / skipped on push. Mirrors the card-id derivation in <see cref="BuildCards"/>.</summary>
    public static bool IsFullyUncontrolled(RgbDevice d, NexusSettings settings)
    {
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        if (uncontrolled.Count == 0)
        {
            return false;
        }

        var baseId = d.StableId;
        var isSplitMotherboard = IsSplitMotherboard(d);
        var structure = BuildStructure(d, settings);
        var zones = ZoneResolution.Resolve(structure, settings);
        var isDefault = zones.Count > 0 && zones[0].IsDefault;

        if (isDefault && !isSplitMotherboard)
        {
            return uncontrolled.Contains(baseId);
        }

        if (isDefault)
        {
            if (d.Zones.Count == 0)
            {
                return false;
            }
            for (var z = 0; z < d.Zones.Count; z++)
            {
                if (!uncontrolled.Contains($"{baseId}-{z}"))
                {
                    return false;
                }
            }
            return true;
        }

        if (zones.Count == 0)
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

    /// <summary>Full GetAll card emission; static and bridge-free so tests cover it with fake controller data.</summary>
    public static GetLightingDevicesResponse BuildCards(IReadOnlyList<RgbDevice> devices, NexusSettings settings, bool isInit, IReadOnlySet<string>? drivableIds = null)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var zoneLedCounts = settings.Devices.ZoneLedCounts;
        var exclusions = settings.Devices.OpenRgbDetectorExclusions;

        var result = new List<LightingDevice>(devices.Count);
        // Two independent slot counters so device cards and motherboard zone
        // strips get their own non-overlapping default grids on the canvas.
        int cardSlot = 0;
        int stripSlot = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var baseId = d.StableId;
            // Detector-excluded devices render from their persisted snapshot
            // below; the live entry (pre-bounce real device or the fork's
            // zero-LED placeholder dummy) would duplicate or shadow that card.
            if (exclusions.Count > 0 && exclusions.ContainsKey(baseId))
                continue;
            var baseKey = DeviceKeyComputer.ForOpenRgbDevice(d);
            var conflictAppIds = Conflicts.ConflictDeviceOwnership.AppIdsForVendor(d.Vendor);
            var isSplitMotherboard = IsSplitMotherboard(d);
            var structure = BuildStructure(d, settings);
            var zones = ZoneResolution.Resolve(structure, settings);
            var isDefault = zones.Count > 0 && zones[0].IsDefault;

            if (isDefault && !isSplitMotherboard)
            {
                // OpenRGB false-positive detectors register a controller with no
                // readable LED zones; nexus cannot drive it, so drop the whole-device
                // card rather than surfacing a phantom the user never owned. Non-split
                // whole devices are never resizable, so a zero count means nothing to drive.
                // A device that ever settled drivable is latched (drivableIds) so a
                // later transient 0-LED fetch mid-enumeration can't hide real hardware.
                if (d.LedCount <= 0 && drivableIds?.Contains(d.StableId) != true)
                    continue;

                prefs.TryGetValue(baseId, out var pref);
                layouts.TryGetValue(baseId, out var layout);
                var (dx, dy, dw, dh) = OpenRgbLightingDeviceProvider.DefaultCardLayout(cardSlot);
                result.Add(new LightingDevice
                {
                    Id = baseId,
                    DeviceKey = baseKey,
                    Name = d.Name,
                    Type = OpenRgbTypeName(d.Type),
                    IconType = OpenRgbTypeName(d.Type),
                    LedsOn = !disabled.Contains(baseId),
                    Brightness = pref?.Brightness ?? 100,
                    Hue = pref?.Hue ?? 0,
                    Saturation = pref?.Saturation ?? 1.0f,
                    LedCount = d.LedCount,
                    EnabledLedCount = ZoneResolution.CountEnabled(structure, zones[0], baseId, d.LedCount, zones[0].LegacyZoneIndex, settings),
                    CanvasX = layout?.X ?? dx,
                    CanvasY = layout?.Y ?? dy,
                    CanvasW = layout?.W ?? dw,
                    CanvasH = layout?.H ?? dh,
                    CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
                    DeviceId = baseId,
                    ZoneCustomizable = d.LedCount > 1,
                    ConflictAppIds = new List<string>(conflictAppIds),
                });
                cardSlot++;
                continue;
            }

            if (isDefault)
            {
                // Split motherboard with the default partition - one card per
                // header, identical to the legacy emission.
                for (int z = 0; z < d.Zones.Count; z++)
                {
                    var zone = d.Zones[z];
                    var zoneId = $"{baseId}-{z}";
                    prefs.TryGetValue(zoneId, out var pref);
                    layouts.TryGetValue(zoneId, out var layout);
                    // Trust the user's persisted choice over OpenRGB's
                    // reported count (12V headers ignore ResizeZone).
                    var effectiveLedCount = zoneLedCounts.TryGetValue(zoneId, out var persistedCount)
                        ? persistedCount
                        : zone.LedCount;
                    var (sx, sy, sw, sh) = OpenRgbLightingDeviceProvider.DefaultStripLayout(stripSlot);
                    result.Add(new LightingDevice
                    {
                        Id = zoneId,
                        DeviceKey = DeviceKeyComputer.ForZone(baseKey, z),
                        Name = BuildZoneName(d.Name, zone.Name, z),
                        Type = OpenRgbTypeName(d.Type),
                        IconType = OpenRgbTypeName(d.Type),
                        LedsOn = !disabled.Contains(zoneId),
                        Brightness = pref?.Brightness ?? 100,
                        Hue = pref?.Hue ?? 0,
                        Saturation = pref?.Saturation ?? 1.0f,
                        LedCount = effectiveLedCount,
                        EnabledLedCount = ZoneResolution.CountEnabled(structure, zones[z], zoneId, effectiveLedCount, zones[z].LegacyZoneIndex, settings),
                        CanvasX = layout?.X ?? sx,
                        CanvasY = layout?.Y ?? sy,
                        CanvasW = layout?.W ?? sw,
                        CanvasH = layout?.H ?? sh,
                        CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
                        ParentDeviceId = baseId,
                        ZoneIndex = z,
                        ZoneType = ZoneTypeName(zone.ZoneType),
                        ZoneResizable = IsZoneResizable(zone.ZoneType),
                        DeviceId = baseId,
                        ZoneCustomizable = true,
                        ConflictAppIds = new List<string>(conflictAppIds),
                    });
                    stripSlot++;
                }
                continue;
            }

            // Custom partition - one card per user zone.
            foreach (var zone in zones)
            {
                prefs.TryGetValue(zone.Id, out var pref);
                layouts.TryGetValue(zone.Id, out var layout);
                var (zx, zy, zw, zh) = isSplitMotherboard
                    ? OpenRgbLightingDeviceProvider.DefaultStripLayout(stripSlot)
                    : OpenRgbLightingDeviceProvider.DefaultCardLayout(cardSlot);
                var wholeResizable = ZoneResolution.WholeResizableSegment(structure, zone);
                result.Add(new LightingDevice
                {
                    Id = zone.Id,
                    DeviceKey = "",
                    Name = zone.Name,
                    Type = OpenRgbTypeName(d.Type),
                    IconType = OpenRgbTypeName(d.Type),
                    LedsOn = !disabled.Contains(zone.Id),
                    Brightness = pref?.Brightness ?? 100,
                    Hue = pref?.Hue ?? 0,
                    Saturation = pref?.Saturation ?? 1.0f,
                    LedCount = zone.LedCount,
                    EnabledLedCount = ZoneResolution.CountEnabled(structure, zone, zone.Id, zone.LedCount, zone.Ordinal, settings),
                    CanvasX = layout?.X ?? zx,
                    CanvasY = layout?.Y ?? zy,
                    CanvasW = layout?.W ?? zw,
                    CanvasH = layout?.H ?? zh,
                    CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
                    ParentDeviceId = baseId,
                    ZoneIndex = zone.Ordinal,
                    ZoneType = ZoneCardType(structure, zone),
                    ZoneResizable = wholeResizable >= 0,
                    DeviceId = baseId,
                    ZoneCustomizable = true,
                    ConflictAppIds = new List<string>(conflictAppIds),
                });
                if (isSplitMotherboard)
                    stripSlot++;
                else
                    cardSlot++;
            }
        }

        // Detector-excluded devices: the hardware is deliberately undetected
        // (denylisted in the daemon's config), so the card renders from the
        // snapshot taken at exclusion time. Controlled=false is stamped by the
        // composite provider - the base id stays in the uncontrolled list for
        // as long as the exclusion exists - and toggling the card back on
        // lifts the exclusion via the bridge's reconcile pass. Sorted so the
        // default canvas slots stay stable across calls.
        if (exclusions.Count > 0)
        {
            var keys = new List<string>(exclusions.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var snap = exclusions[key];
                // Pre-map snapshots kept the device name in DetectorName.
                var snapName = string.IsNullOrEmpty(snap.DeviceName) ? snap.DetectorName : snap.DeviceName;
                var snapDevice = new RgbDevice
                {
                    Name = snapName,
                    Vendor = snap.Vendor,
                    Serial = snap.Serial,
                    Location = snap.Location,
                    Type = snap.Type,
                    LedCount = snap.LedCount,
                };
                prefs.TryGetValue(key, out var pref);
                layouts.TryGetValue(key, out var layout);
                var (dx, dy, dw, dh) = OpenRgbLightingDeviceProvider.DefaultCardLayout(cardSlot);
                result.Add(new LightingDevice
                {
                    Id = key,
                    DeviceKey = DeviceKeyComputer.ForOpenRgbDevice(snapDevice),
                    Name = snapName,
                    Type = OpenRgbTypeName(snap.Type),
                    IconType = OpenRgbTypeName(snap.Type),
                    LedsOn = !disabled.Contains(key),
                    Brightness = pref?.Brightness ?? 100,
                    Hue = pref?.Hue ?? 0,
                    Saturation = pref?.Saturation ?? 1.0f,
                    LedCount = snap.LedCount,
                    EnabledLedCount = snap.LedCount,
                    CanvasX = layout?.X ?? dx,
                    CanvasY = layout?.Y ?? dy,
                    CanvasW = layout?.W ?? dw,
                    CanvasH = layout?.H ?? dh,
                    CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
                    DeviceId = key,
                    ZoneCustomizable = false,
                    ConflictAppIds = Conflicts.ConflictDeviceOwnership.AppIdsForVendor(snap.Vendor),
                });
                cardSlot++;
            }
        }

        return new GetLightingDevicesResponse
        {
            IsInit = isInit,
            Devices = result,
        };
    }

    /// <summary>Card zone type for a custom zone: the shared segment type when uniform, else "linear".</summary>
    private static string ZoneCardType(DeviceStructure structure, ResolvedZone zone)
    {
        string? type = null;
        foreach (var slice in zone.Slices)
        {
            if (slice.Segment < 0 || slice.Segment >= structure.Segments.Count)
                continue;
            var t = structure.Segments[slice.Segment].ZoneType;
            if (type is null)
                type = t;
            else if (type != t)
                return "linear";
        }
        return type ?? "linear";
    }

    public static string BuildZoneName(string deviceName, string zoneName, int zoneIndex)
    {
        if (!string.IsNullOrWhiteSpace(zoneName))
        {
            return $"{deviceName} - {zoneName}";
        }
        return $"{deviceName} - Zone {zoneIndex + 1}";
    }

    public static string ZoneTypeName(uint t) => t switch
    {
        0 => "single",
        1 => "linear",
        2 => "matrix",
        _ => "unknown",
    };

    /// <summary>
    /// Single (12V RGB) and Linear (5V ARGB) motherboard zones are both
    /// user-resizable; matrix zones have a fixed layout. See the original
    /// rationale on <see cref="OpenRgbLightingDeviceProvider"/>.
    /// </summary>
    public static bool IsZoneResizable(uint zoneType) => zoneType == 0 || zoneType == 1;

    /// <summary>
    /// OpenRGB device type enum to human-readable string. Mirrors the names
    /// in OpenRGB's RGBController.h DEVICE_TYPE enum.
    /// </summary>
    public static string OpenRgbTypeName(uint type) => type switch
    {
        0 => "motherboard",
        1 => "dram",
        2 => "gpu",
        3 => "cooler",
        4 => "ledstrip",
        5 => "keyboard",
        6 => "mouse",
        7 => "mousemat",
        8 => "headset",
        9 => "headset_stand",
        10 => "gamepad",
        11 => "light",
        12 => "speaker",
        13 => "virtual",
        14 => "storage",
        15 => "case",
        16 => "microphone",
        17 => "accessory",
        _ => "unknown",
    };

    /// <summary>
    /// Clamp persisted rotation values to the four valid quarter-turns.
    /// Older builds wrote a nonsense default (DTO bug); normalize on load.
    /// </summary>
    public static int NormalizeRotation(int rotation)
    {
        var r = ((rotation % 360) + 360) % 360;
        return r switch
        {
            90 => 90,
            180 => 180,
            270 => 270,
            _ => 0,
        };
    }
}
