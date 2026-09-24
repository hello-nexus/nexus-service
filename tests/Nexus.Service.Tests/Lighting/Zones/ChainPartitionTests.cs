using System.Linq;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// A chained ARGB port: rule 2 must lift for the segment the chain owns and
/// keep holding for every other resizable segment on the same device.
/// </summary>
public class ChainPartitionTests
{
    // The B850I AORUS shape: two resizable headers plus a fixed single LED.
    private static List<StructureSegment> Aorus() => new()
    {
        new StructureSegment { Index = 0, Name = "ARGB_V2_1", LedCount = 12, FrameLedCount = 12, Resizable = true, ZoneType = "linear" },
        new StructureSegment { Index = 1, Name = "ARGB_V2_2", LedCount = 88, FrameLedCount = 88, Resizable = true, ZoneType = "linear" },
        new StructureSegment { Index = 2, Name = "LED_C", LedCount = 1, FrameLedCount = 1, Resizable = false, ZoneType = "single" },
    };

    private static ZoneDef Zone(string name, int seg, int start, int count)
    {
        var z = new ZoneDef { Name = name };
        z.Slices.Add(new ZoneSlice { Segment = seg, Start = start, Count = count });
        return z;
    }

    // QX 34 + generic 20-LED strip + QL 34 on header 1.
    private static List<ZoneDef> ChainPartition() => new()
    {
        Zone("ARGB_V2_1", 0, 0, 12),
        Zone("QX Fan 1", 1, 0, 34),
        Zone("Strip 2", 1, 34, 20),
        Zone("QL Fan 3", 1, 54, 34),
        Zone("LED_C", 2, 0, 1),
    };

    [Fact]
    public void Chained_segment_may_be_split_into_one_zone_per_product()
    {
        var result = ZonePartitionValidator.Validate(Aorus(), ChainPartition(),
            chainOwnedSegments: new HashSet<int> { 1 });
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void The_same_partition_is_refused_without_a_chain()
    {
        var result = ZonePartitionValidator.Validate(Aorus(), ChainPartition(), chainOwnedSegments: null);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("resizable"));
    }

    [Fact]
    public void A_chain_on_one_header_does_not_unlock_the_other()
    {
        var defs = ChainPartition();
        // Split header 0 as well, which no chain owns.
        defs[0] = Zone("ARGB_V2_1 a", 0, 0, 6);
        defs.Add(Zone("ARGB_V2_1 b", 0, 6, 6));
        var result = ZonePartitionValidator.Validate(Aorus(), defs,
            chainOwnedSegments: new HashSet<int> { 1 });
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("resizable"));
    }

    [Fact]
    public void Chain_slices_must_still_tile_the_segment_exactly()
    {
        var defs = ChainPartition();
        defs[3] = Zone("QL Fan 3", 1, 54, 30);   // 4 LEDs short of the 88
        var result = ZonePartitionValidator.Validate(Aorus(), defs,
            chainOwnedSegments: new HashSet<int> { 1 });
        Assert.False(result.Ok);
    }

    [Fact]
    public void Resolution_uses_the_chain_record_to_build_the_zones()
    {
        var structure = new DeviceStructure { DeviceId = "dev", Name = "AORUS", Partitionable = true };
        structure.Segments.AddRange(Aorus());
        // Fallback path reads DefaultZones, so the fixture needs the stock trio.
        for (int i = 0; i < 3; i++)
        {
            var def = new DefaultZoneDef { Id = $"dev-{i}", Name = structure.Segments[i].Name, LegacyZoneIndex = i };
            def.Slices.Add(new ZoneSlice { Segment = i, Start = 0, Count = structure.Segments[i].LedCount });
            structure.DefaultZones.Add(def);
        }
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions["dev"] = ChainPartition();
        settings.Devices.PortChains[ZoneResolution.ChainKey("dev", 1)] = new()
        {
            new ChainEntry { Key = "product:a", LedCount = 34 },
            new ChainEntry { Key = null, LedCount = 20 },      // a custom run chains like any product
            new ChainEntry { Key = "product:c", LedCount = 34 },
        };

        var zones = ZoneResolution.Resolve(structure, settings);
        Assert.Equal(5, zones.Count);
        // Name is device-qualified for display; RawName is what the chain wrote.
        Assert.Contains(zones, z => z.RawName == "QX Fan 1");
        Assert.Equal(new[] { 12, 34, 20, 34, 1 }, zones.Select(z => z.LedCount).ToArray());

        // Drop the chain record and the partition must self-heal to defaults.
        settings.Devices.PortChains.Clear();
        Assert.Equal(3, ZoneResolution.Resolve(structure, settings).Count);
    }
}
