using System.Linq;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// An effects-only zone (ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE_EFFECTS_ONLY, the
/// JRAINBOW headers on MSI boards without per-LED direct mode) reports the size it
/// was given, but OpenRGB keeps it at one LED in the device's color buffer
/// (RGBController::LEDsInZone). Frame offsets counted from the reported size
/// pushed every later header past its own LEDs: on a MEG Z790 GODLIKE, JRAINBOW1
/// at 60 painted PIPE1 (dragon) and PIPE2 (GODLIKE) and the pipes fell off the
/// end of the buffer.
/// </summary>
public class EffectsOnlyZoneFrameTests
{
    private const string BoardId = "openrgb-s-A02022060100";

    private const uint EffectsOnlySized = RgbZone.FlagConfigurableSizeEffectsOnly | RgbZone.FlagConfiguredSize;

    private static RgbZone Fixed(string name) => new() { Name = name, ZoneType = 0, LedCount = 1, LedsMin = 1, LedsMax = 1 };

    private static RgbZone Rainbow(string name, int count) => new()
    {
        Name = name,
        ZoneType = 0,
        LedCount = count,
        LedsMin = 0,
        LedsMax = 30,
        Flags = EffectsOnlySized,
    };

    // The board as the daemon reports it after Nexus seeded the three JRAINBOW headers
    // with 60: six zones, but one LED each in the color buffer.
    private static RgbDevice Board() => new()
    {
        Index = 0,
        Name = "MSI MEG Z790 GODLIKE (MS-7D85)",
        Type = 0,
        LedCount = 6,
        Serial = "A02022060100",
        Zones = new()
        {
            Fixed("JRGB1"),
            Rainbow("JRAINBOW1", 60),
            Rainbow("JRAINBOW2", 60),
            Rainbow("JRAINBOW3", 60),
            Fixed("PIPE1"),
            Fixed("PIPE2"),
        },
    };

    private static NexusSettings Seeded()
    {
        var settings = new NexusSettings();
        for (var z = 1; z <= 3; z++)
        {
            settings.Devices.ZoneLedCounts[$"{BoardId}-{z}"] = 60;
        }
        return settings;
    }

    [Fact]
    public void An_effects_only_zone_holds_one_led_whatever_size_it_reports()
    {
        Assert.Equal(1, Rainbow("JRAINBOW1", 60).BufferLedCount);
        Assert.Equal(1, Rainbow("JRAINBOW1", 1).BufferLedCount);
        Assert.Equal(0, Rainbow("JRAINBOW1", 0).BufferLedCount);
        Assert.Equal(60, new RgbZone { Name = "JRAINBOW1", ZoneType = 1, LedCount = 60, Flags = RgbZone.FlagConfigurableSize }.BufferLedCount);
    }

    [Fact]
    public void Every_header_starts_where_its_leds_are_in_the_buffer()
    {
        var ports = OpenRgbZoneSupport.BuildStructures(Board(), Seeded());

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, ports.Select(p => p.FrameBaseOffset).ToArray());
        Assert.All(ports, p => Assert.Equal(1, p.Segments[0].FrameLedCount));
        // The size the user (or the seed) chose stays the card's count.
        Assert.Equal(new[] { 1, 60, 60, 60, 1, 1 }, ports.Select(p => p.Segments[0].LedCount).ToArray());
    }

    [Fact]
    public void A_sized_rainbow_header_writes_only_its_own_led()
    {
        var settings = Seeded();
        var ports = OpenRgbZoneSupport.BuildStructures(Board(), settings);

        var rainbow = Assert.Single(ZoneResolution.Resolve(ports[1], settings));
        Assert.Equal(1, ZoneResolution.FrameOffset(ports[1], rainbow));
        Assert.Equal(1, rainbow.FrameLedCount);

        var pipe1 = Assert.Single(ZoneResolution.Resolve(ports[4], settings));
        var pipe2 = Assert.Single(ZoneResolution.Resolve(ports[5], settings));
        Assert.Equal(4, ZoneResolution.FrameOffset(ports[4], pipe1));
        Assert.Equal(5, ZoneResolution.FrameOffset(ports[5], pipe2));
    }

    [Fact]
    public void The_zone_total_matches_the_device_buffer()
    {
        var board = Board();

        Assert.Equal(board.LedCount, RgbBridge.SumZoneLeds(board));
    }

    [Fact]
    public void The_led_map_places_a_later_header_at_its_buffer_offset()
    {
        var settings = Seeded();

        var pipe2 = LedLayoutResolver.ResolveOpenRgb(Board(), 5, $"{BoardId}-5", settings);

        Assert.Equal(5, pipe2.GlobalOffset);
        Assert.Equal(1, pipe2.LedCount);
    }
}
