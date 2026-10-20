using System.Collections.Generic;
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

    private static (float, float, float, float) Rect(LightingDevice d) => (d.CanvasX, d.CanvasY, d.CanvasW, d.CanvasH);

    [Fact]
    public void The_zones_of_one_fan_share_one_grid_slot_and_each_fan_is_one_slot_in_list_order()
    {
        var devices = new List<LightingDevice>
        {
            new() { Id = "openrgb-l-a" },
            new() { Id = "lianli-flex:" + Tl + ":up" },
            new() { Id = "lianli-flex:" + Tl + ":down" },
            new() { Id = "lianli-flex:" + Inf + ":center" },
            new() { Id = "lianli-flex:" + Inf + ":inner" },
            new() { Id = "lianli-flex:" + Inf + ":outer" },
            new() { Id = "openrgb-l-b" },
        };
        CompositeLightingDeviceProvider.ApplyDefaultLayouts(devices, new NexusSettings());

        // 4 slots: a, TL fan, INF fan, b
        Assert.Equal(CanvasGridLayout.Slot(0, 4), Rect(devices[0]));
        Assert.Equal(CanvasGridLayout.Slot(1, 4), Rect(devices[1]));
        Assert.Equal(Rect(devices[1]), Rect(devices[2]));
        Assert.Equal(CanvasGridLayout.Slot(2, 4), Rect(devices[3]));
        Assert.Equal(Rect(devices[3]), Rect(devices[4]));
        Assert.Equal(Rect(devices[3]), Rect(devices[5]));
        Assert.Equal(CanvasGridLayout.Slot(3, 4), Rect(devices[6]));
    }

    [Fact]
    public void Without_Flex_devices_every_device_keeps_the_plain_one_slot_grid()
    {
        for (var n = 1; n <= 14; n++)
        {
            var devices = Enumerable.Range(0, n).Select(i => new LightingDevice { Id = "openrgb-l-" + i }).ToList();
            CompositeLightingDeviceProvider.ApplyDefaultLayouts(devices, new NexusSettings());
            for (var i = 0; i < n; i++)
            {
                Assert.Equal(CanvasGridLayout.Slot(i, n), Rect(devices[i]));
            }
        }
    }

    [Fact]
    public void A_layout_the_user_saved_never_moves_and_still_counts_as_a_slot()
    {
        var devices = new List<LightingDevice>
        {
            new() { Id = "openrgb-l-a" },
            new() { Id = "lianli-flex:" + Tl + ":up" },
            new() { Id = "lianli-flex:" + Tl + ":down" },
            new() { Id = "openrgb-l-b" },
        };
        var settings = new NexusSettings();
        settings.Lighting.DeviceLayouts["openrgb-l-a"] = new DeviceLayout { X = 700, Y = 200, W = 50, H = 40 };
        var saved = Rect(devices[0]);
        CompositeLightingDeviceProvider.ApplyDefaultLayouts(devices, settings);
        Assert.Equal(saved, Rect(devices[0]));
        Assert.Equal(CanvasGridLayout.Slot(1, 3), Rect(devices[1]));
        Assert.Equal(CanvasGridLayout.Slot(1, 3), Rect(devices[2]));
        Assert.Equal(CanvasGridLayout.Slot(2, 3), Rect(devices[3]));
    }

    [Fact]
    public void A_frame_build_asks_for_the_default_rects_once()
    {
        var (sp, _, _) = Build();
        var provider = sp.GetRequiredService<FlexLightingDeviceProvider>();
        var composite = sp.GetRequiredService<ILightingDeviceProvider>();
        var asked = 0;
        var real = provider.DefaultRects!;
        provider.DefaultRects = () =>
        {
            asked++;
            return real();
        };
        Assert.NotEmpty(composite.GetAll().Devices);
        var frames = provider.BuildFrames(0);
        Assert.True(frames.Count >= 2);
        Assert.Equal(1, asked);
    }

    private static (ServiceProvider Sp, FlexHub Hub, FakeFlexDiscovery Discovery) Build()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var hub = new FlexHub(discovery, p => p.ReceiverPath == "tl-receiver" ? new FakeFlexTransport(Tl) : new FakeFlexTransport(Inf, fansType: 0x2B), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        var services = new ServiceCollection();
        services.AddNexusCore().AddNexusSensors().AddNexusCooling().AddNexusBenchmarks().AddNexusLighting().AddNexusDevices()
            .AddNexusPeripherals().AddNexusActivity().AddNexusNetwork().AddNexusLifecycle().AddNexusWeather()
            .AddNexusPanel(servicePort: 9400).AddNexusLinuxDBus().AddNexusHelper();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(hub);
        return (services.BuildServiceProvider(), hub, discovery);
    }

    private static void AssertCardsMatchFrames(ServiceProvider sp)
    {
        var composite = sp.GetRequiredService<ILightingDeviceProvider>();
        var provider = sp.GetRequiredService<FlexLightingDeviceProvider>();
        var cards = composite.GetAll().Devices.Where(c => c.Id.StartsWith("lianli-flex:", System.StringComparison.Ordinal)).ToList();
        var frames = provider.BuildFrames(0);
        Assert.NotEmpty(cards);
        Assert.Equal(cards.Count, frames.Count);
        foreach (var card in cards)
        {
            var frame = frames.Single(f => f.Id == card.Id);
            Assert.Equal((card.CanvasX, card.CanvasY, card.CanvasW, card.CanvasH), (frame.X, frame.Y, frame.W, frame.H));
        }
    }

    [Fact]
    public void Building_the_Flex_frames_does_not_write_the_engine_frames()
    {
        var (sp, _, _) = Build();
        var provider = sp.GetRequiredService<FlexLightingDeviceProvider>();
        var composite = sp.GetRequiredService<ILightingDeviceProvider>();
        var engine = sp.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>();
        // the engine's own copies of the frames, apart from the ones the provider keeps
        engine.UpdateDevices(provider.BuildFrames(0)
            .Select(f => new Nexus.Service.Lighting.Engine.DeviceFrame(f.Index, f.Id, f.LedCount, -5f, -5f, 10f, 10f, 0))
            .ToArray());

        provider.BuildFrames(0);
        Assert.NotEmpty(engine.Devices);
        Assert.All(engine.Devices, f => Assert.Equal(-5f, f.X));

        // a full listing is what writes them, so the check above can tell the two apart
        composite.GetAll();
        Assert.Contains(engine.Devices, f => f.X != -5f);
    }

    [Fact]
    public void The_composite_cards_and_the_Flex_frames_have_the_same_rects_also_after_a_hot_plug()
    {
        var (sp, hub, discovery) = Build();
        AssertCardsMatchFrames(sp);

        discovery.Ports.Add(new FlexUnitPort { ReceiverPath = "inf", ReceiverPid = FlexProtocol.SlInfReceiverProductId });
        hub.Refresh();
        AssertCardsMatchFrames(sp);

        var cards = sp.GetRequiredService<ILightingDeviceProvider>().GetAll().Devices;
        var tl = cards.Where(c => c.Id.StartsWith("lianli-flex:" + Tl, System.StringComparison.Ordinal)).Select(Rect).Distinct().ToList();
        var inf = cards.Where(c => c.Id.StartsWith("lianli-flex:" + Inf, System.StringComparison.Ordinal)).Select(Rect).Distinct().ToList();
        Assert.Single(tl);
        Assert.Single(inf);
        Assert.NotEqual(tl[0], inf[0]);
    }
}
