using System.Linq;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// A chained ARGB header renders only if the engine-frame path resolves the
/// PORT's partition rather than the board's. The board carries no partition
/// (chains are stored under the port id), so resolving it yields the default
/// trio and every chained card ends up without a frame - cards the user can
/// see and cannot light. These cover the composition the frame loop performs:
/// per-port structure, per-port zones, device-space offsets.
/// </summary>
public class ChainedPortFrameTests
{
    private const string BoardId = "openrgb-s-MB01";

    // Two ARGB headers already resized to 34 and 88, plus a fixed 12V zone.
    private static RgbDevice Board() => new()
    {
        Index = 0,
        Name = "B850I AORUS PRO",
        Type = 0,
        LedCount = 123,
        Serial = "MB01",
        Zones = new()
        {
            new RgbZone { Name = "D_LED1", ZoneType = 1, LedCount = 34 },
            new RgbZone { Name = "D_LED2", ZoneType = 1, LedCount = 88 },
            new RgbZone { Name = "LED_C1C2", ZoneType = 0, LedCount = 1 },
        },
    };

    private static ZoneDef Link(string name, int start, int count)
    {
        var z = new ZoneDef { Name = name };
        z.Slices.Add(new ZoneSlice { Segment = 0, Start = start, Count = count });
        return z;
    }

    /// <summary>QX 34 + a 20-LED strip + QL 34 wired to header 2 (the 88-LED one).</summary>
    private static NexusSettings ChainedSecondHeader()
    {
        var settings = new NexusSettings();
        var portId = $"{BoardId}-1";
        settings.Devices.ZonePartitions[portId] = new()
        {
            Link("QX Fan", 0, 34),
            Link("Generic Strip", 34, 20),
            Link("QL Fan", 54, 34),
        };
        settings.Devices.PortChains[ZoneResolution.ChainKey(portId, 0)] = new()
        {
            new ChainEntry { Key = "product:qx", LedCount = 34 },
            new ChainEntry { Key = "generic:strip", LedCount = 20 },
            new ChainEntry { Key = "product:ql", LedCount = 34 },
        };
        return settings;
    }

    [Fact]
    public void A_port_structure_knows_its_board_and_its_place_in_the_buffer()
    {
        var ports = OpenRgbZoneSupport.BuildStructures(Board(), new NexusSettings());

        Assert.Equal(3, ports.Count);
        Assert.All(ports, p => Assert.Equal(BoardId, p.PhysicalDeviceId));
        Assert.All(ports, p => Assert.Equal(BoardId, p.OwningDeviceId));
        // Device-space start of each header inside the board's LED buffer.
        Assert.Equal(new[] { 0, 34, 122 }, ports.Select(p => p.FrameBaseOffset).ToArray());
        Assert.All(ports, p => Assert.Single(p.Segments));
        // The segment index is its POSITION in its own structure, so always 0
        // on a port. The device-map routes use it as the segment key for
        // MapFromSegment and for the override list, and a board-level number
        // there unmaps every LED on headers past the first - after which the
        // save path reads the empty result as "no overrides" and deletes the
        // stored ones. The board zone number lives on LegacyZoneIndex.
        Assert.Equal(new[] { 0, 0, 0 }, ports.Select(p => p.Segments[0].Index).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, ports.Select(p => p.DefaultZones[0].LegacyZoneIndex).ToArray());
    }

    [Fact]
    public void The_board_alone_never_sees_a_chain()
    {
        var settings = ChainedSecondHeader();
        var board = OpenRgbZoneSupport.BuildStructure(Board(), settings);

        // Regression guard: this is what the frame path used to resolve, and
        // why chained cards were frameless.
        var zones = ZoneResolution.Resolve(board, settings);
        Assert.Equal(3, zones.Count);
        Assert.All(zones, z => Assert.True(z.IsDefault));
    }

    [Fact]
    public void Each_chain_link_gets_its_own_device_space_slice()
    {
        var settings = ChainedSecondHeader();
        var ports = OpenRgbZoneSupport.BuildStructures(Board(), settings);
        var chained = ports[1];

        var zones = ZoneResolution.Resolve(chained, settings);

        Assert.Equal(3, zones.Count);
        Assert.All(zones, z => Assert.False(z.IsDefault));
        Assert.Equal(new[] { 34, 20, 34 }, zones.Select(z => z.LedCount).ToArray());
        Assert.Equal(new[] { 34, 20, 34 }, zones.Select(z => z.FrameLedCount).ToArray());
        // Header 1 starts 34 LEDs into the board, so the links land at 34/68/88
        // rather than at 0/34/54 - which is what a port-local offset would give
        // and would have written the first header's LEDs on every port.
        Assert.Equal(new[] { 34, 68, 88 },
            zones.Select(z => ZoneResolution.FrameOffset(chained, z)).ToArray());
    }

    [Fact]
    public void The_untouched_headers_keep_one_zone_each()
    {
        var settings = ChainedSecondHeader();
        var ports = OpenRgbZoneSupport.BuildStructures(Board(), settings);

        foreach (var index in new[] { 0, 2 })
        {
            var zones = ZoneResolution.Resolve(ports[index], settings);
            var only = Assert.Single(zones);
            Assert.True(only.IsDefault);
            Assert.Equal($"{BoardId}-{index}", only.Id);
            Assert.Equal(ports[index].FrameBaseOffset, ZoneResolution.FrameOffset(ports[index], only));
        }
    }

    [Fact]
    public void A_chain_link_is_never_a_whole_segment_resize_target()
    {
        var settings = ChainedSecondHeader();
        var ports = OpenRgbZoneSupport.BuildStructures(Board(), settings);
        var chained = ports[1];
        var zones = ZoneResolution.Resolve(chained, settings);

        // The first link starts at 0 like a whole-segment zone does. Treating
        // it as one resizes the entire 88-LED header down to the first
        // product's 34 and the rest of the chain stops tiling.
        Assert.All(zones, z => Assert.True(ZoneResolution.WholeResizableSegment(chained, z, settings) < 0));

        // An unchained header still is one. The index it reports is the
        // segment's POSITION in its own structure, which on a port is always
        // 0: the device-map routes use that number as the segment key.
        var plain = ZoneResolution.Resolve(ports[0], settings)[0];
        Assert.Equal(0, ZoneResolution.WholeResizableSegment(ports[0], plain, settings));
    }

    [Fact]
    public void Device_map_defaults_facade_keeps_the_chained_ports_zone_count()
    {
        var settings = ChainedSecondHeader();
        var facade = DevicesRoutes.DeviceMapDefaultsFacade(settings);
        var chained = OpenRgbZoneSupport.BuildStructures(Board(), settings)[1];

        var liveZones = ZoneResolution.Resolve(chained, settings);
        var facadeZones = ZoneResolution.Resolve(chained, facade);

        Assert.Equal(3, liveZones.Count);
        Assert.Equal(liveZones.Count, facadeZones.Count);
    }

    [Fact]
    public void A_single_user_zone_covering_a_whole_header_stays_resizable()
    {
        var settings = new NexusSettings();
        var portId = $"{BoardId}-1";
        settings.Devices.ZonePartitions[portId] = new() { Link("Whole", 0, 88) };

        var chained = OpenRgbZoneSupport.BuildStructures(Board(), settings)[1];
        var zone = Assert.Single(ZoneResolution.Resolve(chained, settings));

        Assert.Equal(0, ZoneResolution.WholeResizableSegment(chained, zone, settings));
    }
}
