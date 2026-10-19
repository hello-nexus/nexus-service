using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexRound2Tests
{
    private const string Tl = "6D1007F517E1";
    private const string Inf = "C5D212506CE1";

    private static (FlexHub Hub, InMemoryConfigStore Store) Create(bool both = false)
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl("tl"));
        if (both)
        {
            discovery.Ports.Add(new FlexUnitPort { ReceiverPath = "inf", ReceiverPid = FlexProtocol.SlInfReceiverProductId });
        }
        var hub = new FlexHub(discovery, p => p.ReceiverPath == "tl"
            ? new FakeFlexTransport(Tl)
            : new FakeFlexTransport(Inf, fansType: 0x2B), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        return (hub, new InMemoryConfigStore());
    }

    // ----- Quick/Sync -----

    [Theory]
    [InlineData("perZone")]
    [InlineData("custom")]
    [InlineData("nonsense")]
    public void Sync_refuses_a_mode_that_is_not_a_shared_effect(string mode)
    {
        var (hub, store) = Create(both: true);
        var group = new FlexGroupLighting(hub, store);
        Assert.NotNull(group.Apply(new FlexSyncPatch { Enabled = true, Mode = mode }));
        Assert.All(store.Load().Devices.FlexUnits.Values, u => Assert.NotEqual(mode, u.Mode));
    }

    [Fact]
    public void The_merge_leader_skips_custom_and_per_zone_units_and_the_group_mode_never_reads_as_one()
    {
        var (hub, store) = Create(both: true);
        store.Update(s =>
        {
            s.Devices.FlexUnits[Tl] = new LianLiFlexUnitSettings { Mode = LianLiFlexUnitSettings.ModePerZone, Merge = true, SortIndex = 0 };
            s.Devices.FlexUnits[Inf] = new LianLiFlexUnitSettings { Mode = "wave", Merge = true, SortIndex = 1 };
        });
        var group = new FlexGroupLighting(hub, store);
        Assert.Equal(Inf, group.LeaderMac());
        Assert.Equal("wave", group.Leader().Mode);
        store.Update(s => s.Devices.FlexUnits[Inf] = new LianLiFlexUnitSettings { Mode = "custom", Merge = true, SortIndex = 1 });
        Assert.Equal("", group.LeaderMac());
        Assert.Equal("rainbow", FlexGroupLighting.SyncModeOf(group.Leader().Mode));
        Assert.Equal("rainbow", FlexGroupLighting.SyncModeOf("perZone"));
        var lighting = LianLiFlexRoutes.BuildLighting(hub, store.Load());
        Assert.Equal("", lighting.MergeLeaderMac);
        store.Update(s => s.Devices.FlexUnits[Inf] = new LianLiFlexUnitSettings { Mode = "wave", Merge = true, SortIndex = 1 });
        Assert.Equal(Inf, LianLiFlexRoutes.BuildLighting(hub, store.Load()).MergeLeaderMac);
    }

    [Fact]
    public void Sync_writes_one_look_to_every_unit_in_order_with_slice_directions()
    {
        var (hub, store) = Create(both: true);
        var group = new FlexGroupLighting(hub, store);
        Assert.Null(group.Apply(new FlexSyncPatch
        {
            Enabled = true,
            Mode = "rainbow",
            Speed = 2,
            Brightness = 3,
            Members = new[] { (Inf, 1), (Tl, 0) },
        }));
        var units = store.Load().Devices.FlexUnits;
        Assert.Equal(0, units[Inf].SortIndex);
        Assert.Equal(1, units[Tl].SortIndex);
        Assert.Equal(1, units[Inf].MergeDirection);
        Assert.All(units.Values, u =>
        {
            Assert.True(u.Merge);
            Assert.Equal("rainbow", u.Mode);
            Assert.Equal(2, u.Speed);
            Assert.Equal(3, u.Brightness);
        });
        Assert.True(store.Load().Devices.FlexSyncEnabled);
        Assert.Equal(new[] { Inf, Tl }, group.Members().Select(m => m.Mac).ToArray());
        Assert.Null(group.Apply(new FlexSyncPatch { Enabled = false }));
        Assert.All(store.Load().Devices.FlexUnits.Values, u => Assert.False(u.Merge));
    }

    [Fact]
    public void Sync_refuses_bad_members_colors_and_unmergeable_effects()
    {
        var (hub, store) = Create();
        var group = new FlexGroupLighting(hub, store);
        Assert.NotNull(group.Apply(new FlexSyncPatch { Members = new[] { ("000000000000", 0) } }));
        Assert.NotNull(group.Apply(new FlexSyncPatch { Colors = new[] { "red" } }));
        Assert.NotNull(group.Apply(new FlexSyncPatch { Enabled = true, Mode = "staggered" }));
    }
}
