using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// Device-scoped settings that hung off the parent controller move to the
/// per-header device ids, and only for a real split motherboard: a multi-zone
/// GPU keys its overrides identically and must be left alone.
/// </summary>
public class SplitMotherboardDeviceMigrationTests
{
    private static RgbDevice Board() => new()
    {
        Index = 0,
        // StableId is derived; a serial makes it "openrgb-s-BOARD".
        Serial = "BOARD",
        Name = "Test Board",
        Type = 0,
        Zones =
        {
            new RgbZone { Name = "ARGB_1", LedCount = 20, ZoneType = 1 },
            new RgbZone { Name = "ARGB_2", LedCount = 60, ZoneType = 1 },
        },
    };

    private static NexusSettings SettingsWithParentState()
    {
        var s = new NexusSettings();
        s.Devices.DeviceLedOverrides["openrgb-s-BOARD"] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 3, U = 0.1f, V = 0.2f },
            new SegmentLedOverride { Segment = 1, LedIndex = 7, U = 0.7f, V = 0.8f, Disabled = true },
        };
        s.Devices.DeviceAspectRatios["openrgb-s-BOARD"] = 2.5f;
        s.Devices.ZonePartitions["openrgb-s-BOARD"] = new() { new ZoneDef { Name = "whatever" } };
        return s;
    }

    [Fact]
    public void Overrides_move_to_the_port_that_owns_their_segment()
    {
        var s = SettingsWithParentState();
        Assert.True(SplitMotherboardDeviceMigration.Apply(s, new[] { Board() }));

        Assert.False(s.Devices.DeviceLedOverrides.ContainsKey("openrgb-s-BOARD"));
        var first = Assert.Single(s.Devices.DeviceLedOverrides["openrgb-s-BOARD-0"]);
        Assert.Equal(3, first.LedIndex);
        var second = Assert.Single(s.Devices.DeviceLedOverrides["openrgb-s-BOARD-1"]);
        Assert.Equal(7, second.LedIndex);
        Assert.True(second.Disabled);
        // The port owns one segment, so every override on it is segment 0.
        Assert.Equal(0, first.Segment);
        Assert.Equal(0, second.Segment);
    }

    [Fact]
    public void Aspect_ratio_is_copied_to_every_port_and_the_parent_partition_is_dropped()
    {
        var s = SettingsWithParentState();
        SplitMotherboardDeviceMigration.Apply(s, new[] { Board() });

        Assert.Equal(2.5f, s.Devices.DeviceAspectRatios["openrgb-s-BOARD-0"]);
        Assert.Equal(2.5f, s.Devices.DeviceAspectRatios["openrgb-s-BOARD-1"]);
        Assert.False(s.Devices.DeviceAspectRatios.ContainsKey("openrgb-s-BOARD"));
        Assert.False(s.Devices.ZonePartitions.ContainsKey("openrgb-s-BOARD"));
    }

    [Fact]
    public void State_already_written_against_a_port_is_not_clobbered()
    {
        var s = SettingsWithParentState();
        s.Devices.DeviceLedOverrides["openrgb-s-BOARD-1"] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 99 },
        };
        SplitMotherboardDeviceMigration.Apply(s, new[] { Board() });
        Assert.Equal(99, Assert.Single(s.Devices.DeviceLedOverrides["openrgb-s-BOARD-1"]).LedIndex);
    }

    [Fact]
    public void A_multi_zone_device_that_is_not_a_motherboard_is_left_alone()
    {
        var gpu = Board();
        gpu.Type = 2;   // not ZONE type 0, so not a split motherboard
        var s = SettingsWithParentState();
        Assert.False(SplitMotherboardDeviceMigration.Apply(s, new[] { gpu }));
        Assert.True(s.Devices.DeviceLedOverrides.ContainsKey("openrgb-s-BOARD"));
    }

    [Fact]
    public void Running_twice_changes_nothing_the_second_time()
    {
        var s = SettingsWithParentState();
        Assert.True(SplitMotherboardDeviceMigration.Apply(s, new[] { Board() }));
        Assert.False(SplitMotherboardDeviceMigration.Apply(s, new[] { Board() }));
    }
}
