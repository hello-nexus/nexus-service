using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3FlexZoneMigrationTests
{
    private const string Mac = "112233445566";
    private static readonly string DeviceId = "lianli-wireless:" + Mac;

    private static NexusSettings Seeded()
    {
        var s = new NexusSettings();
        s.Devices.ZonePartitions[DeviceId] = new List<ZoneDef> { new() { Name = "old" } };
        s.Devices.LedGroups[DeviceId + ":inner"] = new();
        s.Devices.AppliedMappings[DeviceId + ":outer"] = new Nexus.Service.Lighting.Mappings.AppliedMappingRef();
        s.Devices.LedGroups[DeviceId + ":other"] = new();
        s.Devices.DeviceLedOverrides[DeviceId] = new List<SegmentLedOverride>
        {
            new() { Segment = 0, LedIndex = 3, U = 0.1f, V = 0.2f },                // fan 0, wire 3: center 3
            new() { Segment = 0, LedIndex = 22 + 9, U = 0.3f, V = 0.4f },           // fan 1, wire 9: inner 1 of fan 1
            new() { Segment = 0, LedIndex = 20, Disabled = true },                  // fan 0, wire 20: outer 2
            new() { Segment = 1, LedIndex = 4 },                                    // fan 0, wire 26: inner 10
            new() { Segment = 1, LedIndex = 22 + 14 },                              // fan 1, wire 36: outer 8 of fan 1
            new() { Segment = 0, LedIndex = 44 },                                   // fan 2 does not exist
            new() { Segment = 2, LedIndex = 0 },                                    // no such segment
        };
        return s;
    }

    [Fact]
    public void Overrides_land_on_the_same_physical_led_in_the_new_segments()
    {
        var s = Seeded();
        Assert.True(Slv3FlexZoneMigration.Apply(s, Mac, 2));
        var o = s.Devices.DeviceLedOverrides[DeviceId];
        Assert.Equal(5, o.Count);
        Assert.Contains(o, x => x.Segment == 0 && x.LedIndex == 3 && x.U == 0.1f);
        Assert.Contains(o, x => x.Segment == 1 && x.LedIndex == 20 + 1 && x.V == 0.4f);
        Assert.Contains(o, x => x.Segment == 2 && x.LedIndex == 2 && x.Disabled);
        Assert.Contains(o, x => x.Segment == 1 && x.LedIndex == 10);
        Assert.Contains(o, x => x.Segment == 2 && x.LedIndex == 16 + 8);
    }

    [Fact]
    public void Partitions_and_the_ring_groups_and_mappings_are_dropped_and_other_data_stays()
    {
        var s = Seeded();
        Slv3FlexZoneMigration.Apply(s, Mac, 2);
        Assert.False(s.Devices.ZonePartitions.ContainsKey(DeviceId));
        Assert.False(s.Devices.LedGroups.ContainsKey(DeviceId + ":inner"));
        Assert.False(s.Devices.AppliedMappings.ContainsKey(DeviceId + ":outer"));
        Assert.True(s.Devices.LedGroups.ContainsKey(DeviceId + ":other"));
    }

    [Fact]
    public void It_runs_once_per_chain()
    {
        var s = Seeded();
        Assert.True(Slv3FlexZoneMigration.Apply(s, Mac, 2));
        var after = s.Devices.DeviceLedOverrides[DeviceId].Select(x => (x.Segment, x.LedIndex)).ToList();
        Assert.False(Slv3FlexZoneMigration.Apply(s, Mac.ToLowerInvariant(), 2));
        Assert.Equal(after, s.Devices.DeviceLedOverrides[DeviceId].Select(x => (x.Segment, x.LedIndex)).ToList());
        Assert.Single(s.Devices.LianLiWireless.FlexZonesMigrated);
    }

    [Fact]
    public void Overrides_that_all_fall_outside_the_chain_are_removed()
    {
        var s = new NexusSettings();
        s.Devices.DeviceLedOverrides[DeviceId] = new List<SegmentLedOverride> { new() { Segment = 0, LedIndex = 100 } };
        Slv3FlexZoneMigration.Apply(s, Mac, 1);
        Assert.False(s.Devices.DeviceLedOverrides.ContainsKey(DeviceId));
    }

    private static (Slv3LightingDeviceProvider Provider, InMemoryConfigStore Store) Rig(int fansType, string mac = Mac)
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = Convert.FromHexString(mac), MasterMac = net.MasterMac, RxType = 1, FanCount = 2, FansType = (byte)fansType });
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Devices.DeviceLedOverrides["lianli-wireless:" + mac] = new List<SegmentLedOverride> { new() { Segment = 0, LedIndex = 3 } };
            s.Devices.ZonePartitions["lianli-wireless:" + mac] = new List<ZoneDef> { new() { Name = "p" } };
            s.Devices.ZonePartitions["lianli-flex:" + mac] = new List<ZoneDef> { new() { Name = "wired" } };
        });
        return (new Slv3LightingDeviceProvider(hub, store, new Np50IdentifyTracker()), store);
    }

    [Fact]
    public void First_sight_of_a_wireless_sl_inf_flex_chain_migrates_it_and_leaves_wired_flex_data_alone()
    {
        var (provider, store) = Rig(43);
        provider.OnHubStateUpdated();
        provider.OnHubStateUpdated();
        var s = store.Load();
        Assert.Equal(new[] { Mac }, s.Devices.LianLiWireless.FlexZonesMigrated);
        Assert.False(s.Devices.ZonePartitions.ContainsKey("lianli-wireless:" + Mac));
        Assert.True(s.Devices.ZonePartitions.ContainsKey("lianli-flex:" + Mac));
        Assert.Equal((0, 3), (s.Devices.DeviceLedOverrides["lianli-wireless:" + Mac][0].Segment, s.Devices.DeviceLedOverrides["lianli-wireless:" + Mac][0].LedIndex));
    }

    [Theory]
    [InlineData(51)]
    [InlineData(36)]
    [InlineData(24)]
    public void Tl_flex_and_other_families_are_never_migrated(int fansType)
    {
        var (provider, store) = Rig(fansType);
        provider.OnHubStateUpdated();
        var s = store.Load();
        Assert.Empty(s.Devices.LianLiWireless.FlexZonesMigrated);
        Assert.True(s.Devices.ZonePartitions.ContainsKey("lianli-wireless:" + Mac));
        Assert.Single(s.Devices.DeviceLedOverrides["lianli-wireless:" + Mac]);
    }
}
