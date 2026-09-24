using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// What a catalog product contributes to a port's chain, shared by the chain
/// POST and by a provider pre-wiring a connector that only ever takes one
/// product: the link's LED count, its zone name, and the applied-mapping
/// record. The chain record, partition, count and applied mapping are
/// written together in both places - one without the others is what
/// <see cref="ZoneResolution.DropChains"/> exists to clean up.
/// </summary>
public static class PortChainWriter
{
    /// <summary>LEDs a product occupies on the wire: its first zone's count, or 0 when it has none to chain.</summary>
    public static int ProductLedCount(MappingArtifact artifact)
        => artifact.Zones.Count > 0 ? artifact.Zones[0].LedCount ?? 0 : 0;

    /// <summary>The product's name as a zone name, cut to what a partition accepts.</summary>
    public static string ZoneName(MappingArtifact artifact)
        => artifact.Name.Length > ZonePartitionValidator.MaxZoneNameLength
            ? artifact.Name[..ZonePartitionValidator.MaxZoneNameLength]
            : artifact.Name;

    /// <summary>Applied-mapping record for a product a chain assigns to a zone.</summary>
    public static AppliedMappingRef AppliedRef(MappingArtifact artifact) => new()
    {
        MappingId = artifact.Device.Key,
        Source = MappingApplyService.SourceBuiltIn,
        ContentHash = MappingHash.ContentHash(artifact),
        Name = artifact.Name,
        Artifact = artifact,
        AppliedAt = DateTimeOffset.UtcNow,
        AutoApplied = false,
    };

    /// <summary>
    /// Wires one catalog product to a port: the chain record, the port's
    /// count, a one-zone partition and that zone's applied mapping. False,
    /// writing nothing, for a key the chain POST would also refuse (unknown,
    /// failing lint, no LEDs) or a product longer than the port carries.
    /// </summary>
    public static bool WireProduct(NexusSettings settings, string portId, string key, int maxLedCount)
    {
        var artifact = BuiltInMappingsCatalog.Find(key);
        if (artifact is null || !MappingLint.Validate(artifact).Ok) return false;
        var count = ProductLedCount(artifact);
        if (count <= 0 || count > maxLedCount) return false;

        settings.Devices.PortChains[ZoneResolution.ChainKey(portId, 0)] = new List<ChainEntry>
        {
            new() { Key = key, LedCount = count },
        };
        settings.Devices.ZoneLedCounts[portId] = count;
        settings.Devices.ZonePartitions[portId] = new List<ZoneDef>
        {
            new() { Name = ZoneName(artifact), Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = count } } },
        };
        settings.Devices.AppliedMappings[ZoneResolution.CustomZoneId(portId, 0)] = AppliedRef(artifact);
        return true;
    }
}
