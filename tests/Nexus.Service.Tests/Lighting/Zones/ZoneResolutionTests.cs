using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Partition resolution: custom zone identity ({deviceId}:z{ordinal} ids,
/// "{DeviceName} - {ZoneName}" names plus the raw zone name, empty device
/// keys), zone-local to (segment, local) index mapping including multi-slice
/// zones, and the self-heal fallback to the default partition for stale
/// persisted data.
/// </summary>
public class ZoneResolutionTests
{
    private const string DeviceId = "dev-1";

    private static DeviceStructure Structure()
    {
        var s = new DeviceStructure { DeviceId = DeviceId, Name = "Device", DeviceKey = "usb:1111:2222" };
        s.Segments.Add(new StructureSegment { Index = 0, Name = "A", LedCount = 10, FrameLedCount = 10 });
        s.Segments.Add(new StructureSegment { Index = 1, Name = "B", LedCount = 6, FrameLedCount = 6 });
        s.DefaultZones.Add(new DefaultZoneDef
        {
            Id = "legacy-a",
            Name = "Device - A",
            RawName = "A",
            DeviceKey = "usb:1111:2222:zone:0",
            LegacyZoneIndex = 0,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 10 } },
        });
        s.DefaultZones.Add(new DefaultZoneDef
        {
            Id = "legacy-b",
            Name = "Device - B",
            RawName = "B",
            DeviceKey = "usb:1111:2222:zone:1",
            LegacyZoneIndex = 1,
            Slices = { new ZoneSlice { Segment = 1, Start = 0, Count = 6 } },
        });
        return s;
    }

    private static NexusSettings WithPartition(params ZoneDef[] zones)
    {
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[DeviceId] = new List<ZoneDef>(zones);
        return settings;
    }

    private static ZoneDef Zone(string name, params (int seg, int start, int count)[] slices)
    {
        var zone = new ZoneDef { Name = name };
        foreach (var (seg, start, count) in slices)
            zone.Slices.Add(new ZoneSlice { Segment = seg, Start = start, Count = count });
        return zone;
    }

    [Fact]
    public void Absent_partition_resolves_to_default_zones_with_legacy_identity()
    {
        var zones = ZoneResolution.Resolve(Structure(), new NexusSettings());
        Assert.Equal(2, zones.Count);
        Assert.True(zones[0].IsDefault);
        Assert.Equal("legacy-a", zones[0].Id);
        Assert.Equal("Device - A", zones[0].Name);
        Assert.Equal("A", zones[0].RawName);
        Assert.Equal("usb:1111:2222:zone:0", zones[0].DeviceKey);
        Assert.Equal(0, zones[0].LegacyZoneIndex);
        Assert.Equal(10, zones[0].LedCount);
        Assert.Equal("legacy-b", zones[1].Id);
        Assert.Equal(6, zones[1].LedCount);
    }

    [Fact]
    public void Custom_partition_emits_ordinal_ids_combined_names_and_empty_keys()
    {
        var settings = WithPartition(
            Zone("Ring", (0, 0, 10), (1, 0, 2)),
            Zone("Strip", (1, 2, 4)));
        var zones = ZoneResolution.Resolve(Structure(), settings);

        Assert.Equal(2, zones.Count);
        Assert.False(zones[0].IsDefault);
        Assert.Equal($"{DeviceId}:z0", zones[0].Id);
        Assert.Equal("Device - Ring", zones[0].Name);
        Assert.Equal("Ring", zones[0].RawName);
        Assert.Equal("", zones[0].DeviceKey);
        Assert.Equal(12, zones[0].LedCount);
        Assert.Equal(-1, zones[0].LegacyZoneIndex);
        Assert.Equal($"{DeviceId}:z1", zones[1].Id);
        Assert.Equal("Device - Strip", zones[1].Name);
        Assert.Equal("Strip", zones[1].RawName);
        Assert.Equal(4, zones[1].LedCount);
    }

    [Fact]
    public void Invalid_persisted_partition_falls_back_to_default()
    {
        // Covers only part of segment A: fails tiling, must self-heal.
        var settings = WithPartition(Zone("Broken", (0, 0, 4)));
        var zones = ZoneResolution.Resolve(Structure(), settings);
        Assert.True(zones[0].IsDefault);
        Assert.Equal("legacy-a", zones[0].Id);
    }

    [Fact]
    public void Stale_count_after_hardware_change_falls_back_to_default()
    {
        var settings = WithPartition(
            Zone("Top", (0, 0, 12)),
            Zone("Bottom", (1, 0, 6)));
        var zones = ZoneResolution.Resolve(Structure(), settings);
        Assert.True(zones[0].IsDefault);
    }

    // ── zone-local <-> (segment, local) mapping ──────────────────────────

    [Fact]
    public void Multi_slice_zone_maps_local_indices_through_slices()
    {
        var structure = Structure();
        var settings = WithPartition(
            Zone("Span", (0, 6, 4), (1, 0, 3)),
            Zone("Head", (0, 0, 6)),
            Zone("Tail", (1, 3, 3)));
        var zones = ZoneResolution.Resolve(structure, settings);
        var span = zones[0];
        var ctx = ZoneResolution.ContextOf(structure, span);

        // Zone-local to segment-local across the slice boundary.
        Assert.True(ctx.TryMapToSegment(0, out var seg, out var local));
        Assert.Equal((0, 6), (seg, local));
        Assert.True(ctx.TryMapToSegment(3, out seg, out local));
        Assert.Equal((0, 9), (seg, local));
        Assert.True(ctx.TryMapToSegment(4, out seg, out local));
        Assert.Equal((1, 0), (seg, local));
        Assert.True(ctx.TryMapToSegment(6, out seg, out local));
        Assert.Equal((1, 2), (seg, local));
        Assert.False(ctx.TryMapToSegment(7, out _, out _));
        Assert.False(ctx.TryMapToSegment(-1, out _, out _));

        // And the reverse direction.
        Assert.Equal(0, ctx.MapFromSegment(0, 6));
        Assert.Equal(3, ctx.MapFromSegment(0, 9));
        Assert.Equal(4, ctx.MapFromSegment(1, 0));
        Assert.Equal(6, ctx.MapFromSegment(1, 2));
        Assert.True(ctx.MapFromSegment(0, 5) < 0);
        Assert.True(ctx.MapFromSegment(1, 3) < 0);
    }

    [Fact]
    public void Identity_context_maps_segment_zero_only()
    {
        var ctx = ZoneOverrideContext.Identity("card-1");
        Assert.Equal(5, ctx.MapFromSegment(0, 5));
        Assert.True(ctx.MapFromSegment(1, 5) < 0);
        Assert.True(ctx.TryMapToSegment(7, out var seg, out var local));
        Assert.Equal((0, 7), (seg, local));
    }

    [Fact]
    public void Frame_offset_is_the_device_space_run_start()
    {
        var structure = Structure();
        var settings = WithPartition(
            Zone("Head", (0, 0, 6)),
            Zone("Span", (0, 6, 4), (1, 0, 3)),
            Zone("Tail", (1, 3, 3)));
        var zones = ZoneResolution.Resolve(structure, settings);
        Assert.Equal(0, ZoneResolution.FrameOffset(structure, zones[0]));
        Assert.Equal(6, ZoneResolution.FrameOffset(structure, zones[1]));
        Assert.Equal(13, ZoneResolution.FrameOffset(structure, zones[2]));
        Assert.Equal(7, zones[1].FrameLedCount);
    }

    [Fact]
    public void Whole_resizable_segment_zone_tracks_live_count()
    {
        var structure = new DeviceStructure { DeviceId = DeviceId, Name = "Mobo" };
        structure.Segments.Add(new StructureSegment
        { Index = 0, Name = "Header", LedCount = 60, FrameLedCount = 1, Resizable = true, ZoneType = "linear" });
        structure.DefaultZones.Add(new DefaultZoneDef
        { Id = "legacy", Name = "Mobo - Header", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 60 } } });

        // Persisted with the count the header had at authoring time; the
        // live effective count differs and must win.
        var settings = WithPartition(Zone("Header", (0, 0, 30)));
        var zones = ZoneResolution.Resolve(structure, settings);
        Assert.False(zones[0].IsDefault);
        Assert.Equal(60, zones[0].LedCount);
        Assert.Equal(1, zones[0].FrameLedCount);
        Assert.Equal(0, ZoneResolution.WholeResizableSegment(structure, zones[0], settings));
    }
}
