using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.DependencyInjection;
using Nexus.Service.Devices;
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
    public void The_canvas_grid_leaves_the_Flex_zones_to_their_provider_and_places_the_rest_without_them()
    {
        var devices = new System.Collections.Generic.List<LightingDevice>
        {
            new() { Id = "openrgb-l-a" },
            new() { Id = "lianli-flex:" + Tl + ":up", CanvasX = 11, CanvasY = 22, CanvasW = 33, CanvasH = 44 },
            new() { Id = "openrgb-l-b" },
        };
        var alone = new System.Collections.Generic.List<LightingDevice> { new() { Id = "openrgb-l-a" }, new() { Id = "openrgb-l-b" } };
        var settings = new NexusSettings();

        CompositeLightingDeviceProvider.ApplyDefaultLayouts(devices, settings);
        CompositeLightingDeviceProvider.ApplyDefaultLayouts(alone, settings);

        Assert.Equal((11f, 22f, 33f, 44f), Rect(devices[1]));
        Assert.Equal(Rect(alone[0]), Rect(devices[0]));
        Assert.Equal(Rect(alone[1]), Rect(devices[2]));
    }

    [Fact]
    public void The_composite_cards_and_the_Flex_frames_always_have_the_same_rects()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var hub = new FlexHub(discovery, _ => new FakeFlexTransport(Tl), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        var services = new ServiceCollection();
        services.AddNexusCore().AddNexusSensors().AddNexusCooling().AddNexusBenchmarks().AddNexusLighting().AddNexusDevices()
            .AddNexusPeripherals().AddNexusActivity().AddNexusNetwork().AddNexusLifecycle().AddNexusWeather()
            .AddNexusPanel(servicePort: 9400).AddNexusLinuxDBus().AddNexusHelper();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(hub);
        var sp = services.BuildServiceProvider();

        var composite = sp.GetRequiredService<ILightingDeviceProvider>();
        var provider = sp.GetRequiredService<FlexLightingDeviceProvider>();
        var cards = composite.GetAll().Devices.Where(c => c.Id.StartsWith("lianli-flex:", System.StringComparison.Ordinal)).ToList();
        var frames = provider.BuildFrames(0);
        Assert.NotEmpty(cards);
        foreach (var card in cards)
        {
            var frame = frames.Single(f => f.Id == card.Id);
            Assert.Equal((card.CanvasX, card.CanvasY, card.CanvasW, card.CanvasH), (frame.X, frame.Y, frame.W, frame.H));
        }
    }

    [Theory]
    [InlineData(3, 3, 5)]
    [InlineData(1, 9, 5)]
    [InlineData(2, 2, 14)]
    [InlineData(3, 3, 25)]
    public void The_strip_wraps_inside_the_canvas_and_the_grid_never_reaches_it(int units, int fansPerUnit, int otherDevices)
    {
        var discovery = new FakeFlexDiscovery();
        for (var u = 0; u < units; u++)
        {
            discovery.Ports.Add(FakeFlexDiscovery.Tl("tl" + u));
        }
        var hub = new FlexHub(discovery, p => new FakeFlexTransport("6D1007F517E" + int.Parse(p.ReceiverPath[2..]).ToString("X"), fans: fansPerUnit), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        var provider = new FlexLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var settings = new NexusSettings();
        var blocks = provider.UnitBlocks(settings);
        var band = provider.BandHeight(settings);
        Assert.Equal(units, blocks.Count);
        Assert.All(blocks.Values, b =>
        {
            Assert.True(b.x >= 0 && b.x + b.w <= 1000f);
            Assert.True(b.y >= 600f - band && b.y + b.h <= 600f);
        });
        var list = blocks.Values.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                var a = list[i];
                var b = list[j];
                Assert.False(a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h);
            }
        }

        var devices = Enumerable.Range(0, otherDevices).Select(n => new LightingDevice { Id = "openrgb-l-" + n }).ToList();
        CompositeLightingDeviceProvider.ApplyDefaultLayouts(devices, settings, band);
        Assert.All(devices, d => Assert.True(d.CanvasY + d.CanvasH <= 600f - band + 0.01f));
    }

    private static (float, float, float, float) Rect(LightingDevice d) => (d.CanvasX, d.CanvasY, d.CanvasW, d.CanvasH);
}

internal static class RectAssert
{
    public static void Should_Equal(this (float, float, float, float) actual, (float, float, float, float) expected) => Assert.Equal(expected, actual);
}
