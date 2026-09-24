using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Devices;

// ----- /devices/lighting-devices/{deviceId}/structure -----

public sealed class StructureSegmentDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int LedCount { get; set; }
    /// <summary>Resizable segments are walls: a zone touching one must be exactly that whole segment.</summary>
    public bool Resizable { get; set; }
    /// <summary>Most LEDs this port can drive, 0 when the hardware declares no ceiling. The editor stops the chain growing past it, since the firmware would take the count and light only the head of it.</summary>
    public int MaxLedCount { get; set; }
    public string ZoneType { get; set; } = "";
}

public sealed class StructureZoneDto
{
    public string Id { get; set; } = "";
    /// <summary>Raw zone name without the device prefix (segment default name for default zones, user-given name for custom zones). Card names stay "{DeviceName} - {ZoneName}".</summary>
    public string Name { get; set; } = "";
    public List<ZoneSlice> Slices { get; set; } = new();
}

public sealed class DeviceStructureResponse : ApiResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public List<StructureSegmentDto> Segments { get; set; } = new();
    public List<StructureZoneDto> Zones { get; set; } = new();
    public bool IsDefaultPartition { get; set; }
    /// <summary>Present when the device belongs to a composable hub (mirror / combine rings); drives the LED-map editor's composition panel.</summary>
    public HubCompositionDto? HubComposition { get; set; }
    /// <summary>
    /// True when this device is a single addressable port, so its zones are a
    /// chain the user composes: products can be picked per zone, zones added and
    /// removed, and a custom zone's LED count typed. False for firmware-fixed
    /// zones (a keeb), where the editor lists the same rows read-only.
    /// </summary>
    public bool Chainable { get; set; }
    /// <summary>The port's chain in wire order, one entry per zone. Empty when nothing is chained.</summary>
    public List<ChainEntryDto> Chain { get; set; } = new();
}

/// <summary>Composition capability + state for a composable hub, embedded in the structure response.</summary>
public sealed class HubCompositionDto
{
    public string HubId { get; set; } = "";
    /// <summary>"lianli" | "smarthub" - selects the web setter endpoint.</summary>
    public string HubKind { get; set; } = "";
    public int PortCount { get; set; }
    public bool HasRingsAxis { get; set; }
    public bool HasPortToggle { get; set; }
    public bool HasMirror { get; set; }
    public bool Mirror { get; set; }
    public bool CombineRings { get; set; }
    public bool[] ActivePorts { get; set; } = System.Array.Empty<bool>();
}

// ----- /devices/lighting-devices/{deviceId}/zones -----

public sealed class SaveZonePartitionBody
{
    public List<ZoneDef> Zones { get; set; } = new();
}

// ----- /devices/lighting-devices/{deviceId}/device-map -----

public sealed class DeviceMapLedDto
{
    /// <summary>Segment-local LED index.</summary>
    public int Index { get; set; }
    public float U { get; set; }
    public float V { get; set; }
    public string Name { get; set; } = "";
    /// <summary>True when a user override exists for this LED.</summary>
    public bool IsCustom { get; set; }
    public bool Disabled { get; set; }
    /// <summary>Card id of the zone this LED belongs to.</summary>
    public string ZoneId { get; set; } = "";
}

public sealed class DeviceMapSegmentDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int LedCount { get; set; }
    public bool Resizable { get; set; }
    public string ZoneType { get; set; } = "";
    public List<DeviceMapLedDto> Leds { get; set; } = new();
}

public sealed class DeviceMapResponse : ApiResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefaultPartition { get; set; }
    public float AspectRatio { get; set; }
    public List<DeviceMapSegmentDto> Segments { get; set; } = new();
}

public sealed class SaveDeviceMapBody
{
    /// <summary>Segment-local override list; replaces the device's stored overrides wholesale.</summary>
    public List<SegmentLedOverride> Overrides { get; set; } = new();
    /// <summary>Editor canvas aspect ratio; non-positive leaves the stored value untouched.</summary>
    public float AspectRatio { get; set; }
}
