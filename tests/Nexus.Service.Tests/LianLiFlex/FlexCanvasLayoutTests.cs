using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexCanvasLayoutTests
{
    private const string Tl = "6D1007F517E1";
    private const string Inf = "C5D212506CE1";

    private static (FlexLightingDeviceProvider Provider, InMemoryConfigStore Store) Create()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl("tl"));
        discovery.Ports.Add(new FlexUnitPort { ReceiverPath = "inf", ReceiverPid = FlexProtocol.SlInfReceiverProductId });
        var hub = new FlexHub(discovery, p => p.ReceiverPath == "tl" ? new FakeFlexTransport(Tl) : new FakeFlexTransport(Inf, fansType: 0x2B), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        return (new FlexLightingDeviceProvider(hub, store, new Np50IdentifyTracker()), store);
    }

    [Fact]
    public void The_zones_of_one_fan_share_one_block_and_the_fans_sit_side_by_side()
    {
        var (provider, _) = Create();
        var cards = provider.GetAll().Devices;
        var tl = cards.Where(c => c.Id.StartsWith("lianli-flex:" + Tl, System.StringComparison.Ordinal)).ToList();
        var inf = cards.Where(c => c.Id.StartsWith("lianli-flex:" + Inf, System.StringComparison.Ordinal)).ToList();
        Assert.Equal(2, tl.Count);
        Assert.Equal(3, inf.Count);
        Assert.Single(tl.Select(c => (c.CanvasX, c.CanvasY, c.CanvasW, c.CanvasH)).Distinct());
        Assert.Single(inf.Select(c => (c.CanvasX, c.CanvasY, c.CanvasW, c.CanvasH)).Distinct());
        Assert.True(tl[0].CanvasX + tl[0].CanvasW <= inf[0].CanvasX);
        Assert.Equal(tl[0].CanvasY, inf[0].CanvasY);
    }

    [Fact]
    public void The_sort_index_orders_the_blocks_and_the_frames_use_the_same_blocks()
    {
        var (provider, store) = Create();
        store.Update(s =>
        {
            s.Devices.FlexUnits[Tl] = new LianLiFlexUnitSettings { SortIndex = 1 };
            s.Devices.FlexUnits[Inf] = new LianLiFlexUnitSettings { SortIndex = 0 };
        });
        var cards = provider.GetAll().Devices;
        var infX = cards.First(c => c.Id.StartsWith("lianli-flex:" + Inf, System.StringComparison.Ordinal)).CanvasX;
        var tlX = cards.First(c => c.Id.StartsWith("lianli-flex:" + Tl, System.StringComparison.Ordinal)).CanvasX;
        Assert.True(infX < tlX);
        var frames = provider.BuildFrames(0);
        Assert.Equal(infX, frames.First(f => f.Id.StartsWith("lianli-flex:" + Inf, System.StringComparison.Ordinal)).X);
        Assert.Equal(tlX, frames.First(f => f.Id.StartsWith("lianli-flex:" + Tl, System.StringComparison.Ordinal)).X);
    }

    [Fact]
    public void A_layout_the_user_saved_is_never_replaced_by_the_default_block()
    {
        var (provider, store) = Create();
        var id = provider.GetAll().Devices.First(c => c.Id.StartsWith("lianli-flex:" + Tl, System.StringComparison.Ordinal)).Id;
        store.Update(s => s.Lighting.DeviceLayouts[id] = new DeviceLayout { X = 700, Y = 200, W = 50, H = 40 });
        var card = provider.GetAll().Devices.First(c => c.Id == id);
        Assert.Equal((700f, 200f, 50f, 40f), (card.CanvasX, card.CanvasY, card.CanvasW, card.CanvasH));
        var frame = provider.BuildFrames(0).First(f => f.Id == id);
        Assert.Equal(700f, frame.X);
    }

    [Fact]
    public void The_canvas_grid_gives_every_zone_of_one_fan_the_fans_slot_and_leaves_saved_layouts_alone()
    {
        var devices = new System.Collections.Generic.List<LightingDevice>
        {
            new() { Id = "openrgb-l-a" },
            new() { Id = "lianli-flex:" + Tl + ":up" },
            new() { Id = "lianli-flex:" + Tl + ":down" },
            new() { Id = "lianli-flex:" + Inf + ":center" },
            new() { Id = "lianli-flex:" + Inf + ":inner" },
            new() { Id = "lianli-flex:" + Inf + ":outer" },
        };
        var settings = new NexusSettings();
        settings.Lighting.DeviceLayouts["lianli-flex:" + Inf + ":outer"] = new DeviceLayout { X = 5, Y = 6, W = 7, H = 8 };
        var before = devices[4].CanvasX;

        CompositeLightingDeviceProvider.ApplyDefaultLayouts(devices, settings);

        Rect(devices[1]).Should_Equal(Rect(devices[2]));
        Rect(devices[3]).Should_Equal(Rect(devices[4]));
        Assert.NotEqual(Rect(devices[1]), Rect(devices[3]));
        Assert.NotEqual(Rect(devices[0]), Rect(devices[1]));
        Assert.Equal(before, devices[5].CanvasX);
    }

    private static (float, float, float, float) Rect(LightingDevice d) => (d.CanvasX, d.CanvasY, d.CanvasW, d.CanvasH);
}

internal static class RectAssert
{
    public static void Should_Equal(this (float, float, float, float) actual, (float, float, float, float) expected) => Assert.Equal(expected, actual);
}
