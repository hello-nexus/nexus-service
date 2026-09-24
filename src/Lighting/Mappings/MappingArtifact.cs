using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Portable LED mapping artifact - the unit that is exported, imported,
/// published to and pulled from the community registry. Describes how a
/// device's LEDs are laid out (positions, disabled set, named groups, LED
/// counts) and nothing about where the device sits on the user's canvas.
/// Serialized camelCase via AppJsonContext (wire) and PersistenceJsonContext
/// (embedded in settings so applied mappings work offline forever).
/// </summary>
public sealed class MappingArtifact
{
    public int SchemaVersion { get; set; } = MappingSchema.Version;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public MappingDeviceInfo Device { get; set; } = new();
    public List<MappingZone> Zones { get; set; } = new();
}

public static class MappingSchema
{
    public const int Version = 1;
    /// <summary>Serialized artifact size cap; mirrors the registry's publish cap.</summary>
    public const int MaxPayloadBytes = 65536;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 2000;
    public const int MaxGroupNameLength = 40;
    public const int MaxGroupsPerZone = 64;
}

public sealed class MappingDeviceInfo
{
    /// <summary>Device fingerprint the registry matches on. See <see cref="DeviceKeyComputer"/>.</summary>
    public string Key { get; set; } = "";
    public MappingDeviceMatch? Match { get; set; }
}

/// <summary>Secondary match evidence; used for ranking/disambiguation, never required.</summary>
public sealed class MappingDeviceMatch
{
    public string? Vid { get; set; }
    public string? Pid { get; set; }
    public string? NameHint { get; set; }
    public string? VendorHint { get; set; }
    public int? OpenRgbType { get; set; }
    public List<MappingZoneSignature>? ZoneSignature { get; set; }
}

public sealed class MappingZoneSignature
{
    /// <summary>OpenRGB zone type: single, linear, or matrix.</summary>
    public int Type { get; set; }
    public int DefaultLeds { get; set; }
}

public sealed class MappingZone
{
    public int ZoneIndex { get; set; }
    /// <summary>Target LED count. Null = keep the device default (count fixed by firmware or not part of this mapping).</summary>
    public int? LedCount { get; set; }
    public float? AspectRatio { get; set; }
    public List<MappingLed> Leds { get; set; } = new();
    public List<int> Disabled { get; set; } = new();
    public List<MappingGroup> Groups { get; set; } = new();
}

public sealed class MappingLed
{
    public int I { get; set; }
    public float U { get; set; }
    public float V { get; set; }
}

/// <summary>
/// Named LED segment inside a zone ("Fan 1"). Shared shape between the
/// artifact and the local user-delta group store (settings LedGroups).
/// </summary>
public sealed class MappingGroup
{
    public string Name { get; set; } = "";
    public List<MappingLedRange> Ranges { get; set; } = new();
}

/// <summary>Inclusive LED index range.</summary>
public sealed class MappingLedRange
{
    public int Start { get; set; }
    public int End { get; set; }
}

/// <summary>
/// Persisted record of a mapping applied to a local device. The full artifact
/// is embedded so the registry is a distribution channel, not a runtime
/// dependency - removal or backend downtime never affects an install that
/// already applied it.
/// </summary>
public sealed class AppliedMappingRef
{
    /// <summary>Registry id for a community mapping, product key for a built-in; null for file imports.</summary>
    public string? MappingId { get; set; }
    /// <summary>"community" | "file" | "builtin"</summary>
    public string Source { get; set; } = "community";
    public string ContentHash { get; set; } = "";
    public string Name { get; set; } = "";
    public MappingArtifact Artifact { get; set; } = new();
    public DateTimeOffset AppliedAt { get; set; }
    /// <summary>True when applied by the first-seen auto-apply path rather than an explicit user choice.</summary>
    public bool AutoApplied { get; set; }
}
