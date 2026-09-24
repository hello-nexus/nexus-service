using System.Collections.Generic;
using Nexus.Service.Lighting.Mappings;

namespace Nexus.Service.Models.Devices;

// ----- /devices/lighting-devices/{id}/mappings - community mapping flow -----

/// <summary>
/// One community mapping as served by the registry (and proxied to the SPA).
/// Field set is the wire contract with nexus-api's mappings module.
/// </summary>
public sealed class CommunityMapping
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string DeviceKey { get; set; } = "";
    public string ContentHash { get; set; } = "";
    /// <summary>"community" | "verified"</summary>
    public string Origin { get; set; } = "community";
    /// <summary>True when the registry's eligibility gate allows silent auto-apply (score, age, undo rate, kill switch).</summary>
    public bool AutoApply { get; set; }
    public double Score { get; set; }
    public int AdopterCount { get; set; }
    public string? AuthorName { get; set; }
    public MappingArtifact? Payload { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

/// <summary>Registry list envelope: GET /mappings?deviceKey= returns { items: [...] }.</summary>
public sealed class CommunityMappingList
{
    public List<CommunityMapping> Items { get; set; } = new();
}

/// <summary>SPA-facing list for one local device, including local apply state.</summary>
public sealed class DeviceMappingsResponse : ApiResponse
{
    public string DeviceKey { get; set; } = "";
    /// <summary>True when the registry was unreachable and items come from the disk cache (possibly empty).</summary>
    public bool Offline { get; set; }
    public List<CommunityMapping> Items { get; set; } = new();
    public AppliedMappingSummary? Applied { get; set; }
    /// <summary>True when the user undid an auto-apply on this device; the client will not auto-apply again.</summary>
    public bool AutoApplyDeclined { get; set; }
}

public sealed class AppliedMappingSummary
{
    public string? MappingId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"community" | "file" | "builtin"</summary>
    public string Source { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public bool AutoApplied { get; set; }
    public long AppliedAtMs { get; set; }
}

public sealed class ApplyMappingBody
{
    public string MappingId { get; set; } = "";
}

// ----- built-in mapping catalog (embedded, offline) -----

/// <summary>The embedded catalog resource: one entry per pre-built product mapping.</summary>
public sealed class BuiltInMappingsFile
{
    public int SchemaVersion { get; set; }
    public int Count { get; set; }
    public List<BuiltInMappingEntry> Mappings { get; set; } = new();
}

/// <summary>
/// One catalog row. Only <see cref="Type"/> lives outside the artifact; name,
/// brand and LED count are read back off the artifact so there is one source
/// of truth for each.
/// </summary>
public sealed class BuiltInMappingEntry
{
    /// <summary>Virtual product key, e.g. "product:corsair-qx-fan".</summary>
    public string Key { get; set; } = "";
    /// <summary>Fan | Strip | AIO | Case | Cable | Water Block | ... - the picker's category filter.</summary>
    public string Type { get; set; } = "";
    public MappingArtifact Artifact { get; set; } = new();
}

/// <summary>Search-result row for the assign picker; the artifact is fetched only on assign.</summary>
public sealed class BuiltInMappingSummary
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Type { get; set; } = "";
    public int LedCount { get; set; }
    /// <summary>True for the generic fan and strip, whose geometry follows a count the user types; every other row's count is fixed by its artifact.</summary>
    public bool Parametric { get; set; }
}

public sealed class BuiltInMappingsResponse : ApiResponse
{
    public List<BuiltInMappingSummary> Items { get; set; } = new();
    /// <summary>Catalog size before the query and limit, so the UI can say "showing N of M".</summary>
    public int Total { get; set; }
}

/// <summary>Assign a built-in mapping to a device by product key.</summary>
public sealed class AssignMappingBody
{
    public string Key { get; set; } = "";
}

/// <summary>Wire an ordered chain to one ARGB port. An empty chain clears it.</summary>
public sealed class SetChainBody
{
    /// <summary>Chain in wire order: products, custom LED runs, or a mix. Null on the wire reads as empty.</summary>
    public List<SetChainEntry>? Entries { get; set; }
}

/// <summary>One requested link: a catalog product by key, or a bare LED count.</summary>
public sealed class SetChainEntry
{
    public string? Key { get; set; }
    public int LedCount { get; set; }
    /// <summary>Ordinal this link occupied in the chain currently on disk, so its rename follows it; null for a link the user just added, which starts unnamed.</summary>
    public int? FromOrdinal { get; set; }
}

/// <summary>A port's current chain, one entry per zone in wire order.</summary>
public sealed class ChainEntryDto
{
    /// <summary>Catalog product key, or null when the user just counted LEDs.</summary>
    public string? Key { get; set; }
    /// <summary>Product name, or the zone name for a custom run.</summary>
    public string Name { get; set; } = "";
    public int LedCount { get; set; }
    /// <summary>True when the count is the user's to edit, i.e. a generic fan or strip.</summary>
    public bool EditableCount { get; set; }
}

public sealed class SetChainResponse : ApiResponse
{
    /// <summary>LED count the chain resolves to - the sum of its products.</summary>
    public int LedCount { get; set; }
    /// <summary>Card ids the chain produced, one per product, in wire order.</summary>
    public List<string> ZoneIds { get; set; } = new();
}

/// <summary>
/// Preview of what the chain POST would produce, writing nothing: the same
/// structure and device-map payloads the two GET routes would answer once
/// the chain was saved, so the editor can stage a chain edit and feed it
/// into the code path it already uses after a normal load.
/// </summary>
public sealed class ChainPreviewResponse : ApiResponse
{
    public DeviceStructureResponse? Structure { get; set; }
    public DeviceMapResponse? Map { get; set; }
}

/// <summary>Device id -> cached community mapping count (only entries with a nonzero count).</summary>
public sealed class MappingsAvailableResponse : ApiResponse
{
    public Dictionary<string, int> Counts { get; set; } = new();
}

public sealed class PublishMappingResponse : ApiResponse
{
    public string? MappingId { get; set; }
    /// <summary>True when the registry already had an identical artifact for this device and returned the existing row.</summary>
    public bool AlreadyExisted { get; set; }
}

public sealed class ExportMappingResponse : ApiResponse
{
    public MappingArtifact? Artifact { get; set; }
}

// ----- cloud-bound request bodies (service -> nexus-api) -----

public sealed class PublishCloudBody
{
    public string InstallId { get; set; } = "";
    public MappingArtifact Artifact { get; set; } = new();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? AuthorName { get; set; }
}

public sealed class AdoptCloudBody
{
    public string InstallId { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    /// <summary>"manual" | "auto"</summary>
    public string Source { get; set; } = "manual";
}

public sealed class RevokeCloudBody
{
    public string InstallId { get; set; } = "";
    /// <summary>"undo" | "switched" | "reset"</summary>
    public string Reason { get; set; } = "switched";
}

public sealed class DevicesSeenCloudBody
{
    public string InstallId { get; set; } = "";
    public List<string> DeviceKeys { get; set; } = new();
}
