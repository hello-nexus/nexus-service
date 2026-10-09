using System;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LightingDeviceProviderTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");

    [Fact]
    public void Sl_fan_chain_lays_out_top_and_bottom_edge_bars_left_to_right()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3, FansType = 24 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var structure = Assert.Single(provider.GetStructures());
        var top = structure.Segments[Slv3LightingDeviceProvider.InnerSegment];
        var bottom = structure.Segments[Slv3LightingDeviceProvider.OuterSegment];

        Assert.Equal("Top", top.Name);
        Assert.Equal("Bottom", bottom.Name);
        for (var f = 0; f < 3; f++)
        {
            // The bar then the edge line per fan, each strictly left to right inside the fan's third.
            for (var k = 1; k < 12; k++)
            {
                Assert.True(top.DefaultU![f * 20 + k] > top.DefaultU[f * 20 + k - 1]);
            }
            for (var k = 13; k < 20; k++)
            {
                Assert.True(top.DefaultU![f * 20 + k] > top.DefaultU[f * 20 + k - 1]);
            }
            Assert.InRange(top.DefaultU![f * 20], f / 3f, (f + 1) / 3f);
            Assert.InRange(top.DefaultU[f * 20 + 19], f / 3f, (f + 1) / 3f);
        }
        for (var i = 0; i < 60; i++)
        {
            // The bottom half mirrors the top: same column, opposite edge.
            Assert.Equal(top.DefaultU![i], bottom.DefaultU![i], 5);
            Assert.True(top.DefaultV![i] < 0.5f && bottom.DefaultV![i] > 0.5f);
        }
    }

    [Fact]
    public void GetStructures_returns_empty_when_disconnected()
    {
        var hub = new Slv3Hub(new Slv3TestHub.FakeDiscovery(), _ => throw new InvalidOperationException());
        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        Assert.Empty(provider.GetStructures());
    }

    [Fact]
    public void Bound_fan_chain_becomes_one_device_with_inner_outer_segments()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var structures = provider.GetStructures();

        var structure = Assert.Single(structures);
        Assert.Equal($"lianli-wireless:{Convert.ToHexString(FanMac)}", structure.DeviceId);
        Assert.Equal(2, structure.Segments.Count);
        // 3 fans * 20 LEDs/ring (half of the 40-LED-per-fan wire layout).
        Assert.Equal(60, structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount);
        Assert.Equal(60, structure.Segments[Slv3LightingDeviceProvider.OuterSegment].LedCount);
        Assert.Equal(2, structure.DefaultZones.Count);
    }

    [Fact]
    public void Odd_per_fan_led_count_gives_the_outer_ring_the_extra_led()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3, FansType = 63 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var structure = Assert.Single(provider.GetStructures());
        var inner = structure.Segments[Slv3LightingDeviceProvider.InnerSegment];
        var outer = structure.Segments[Slv3LightingDeviceProvider.OuterSegment];

        Assert.Equal(3 * 4, inner.LedCount);
        Assert.Equal(3 * 5, outer.LedCount);
        Assert.Equal(outer.LedCount, outer.DefaultU!.Length);
        Assert.Equal(3 * Slv3Protocol.LedsPerFanFor(Slv3FanFamily.P28V2), inner.LedCount + outer.LedCount);
    }

    [Fact]
    public void Unbound_fan_is_not_a_device()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        Assert.Empty(provider.GetStructures());
    }

    [Fact]
    public void GetAll_emits_a_card_per_zone_with_fan_icon()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var resp = provider.GetAll();

        Assert.True(resp.IsInit);
        Assert.Equal(2, resp.Devices.Count);
        Assert.All(resp.Devices, d => Assert.Equal("fan", d.IconType));
        Assert.Contains(resp.Devices, d => d.Id.EndsWith(":inner", StringComparison.Ordinal));
        Assert.Contains(resp.Devices, d => d.Id.EndsWith(":outer", StringComparison.Ordinal));
    }

    private const string Strimer24PinKey = "product:lianli-lian-li-strimer-wireless-24-pin";

    [Fact]
    public void Bound_strimer_is_one_fixed_segment_holding_the_whole_cable()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        // dev_type 2 = 24-pin, fan_num 0: the Y70's record.
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var structure = Assert.Single(provider.GetStructures());

        Assert.True(Slv3LightingDeviceProvider.IsStrimerStructure(structure));
        Assert.Equal($"lianli-wireless:{Convert.ToHexString(FanMac)}", structure.DeviceId);
        var segment = Assert.Single(structure.Segments);
        Assert.Equal(6 * 22, segment.FrameLedCount);
        Assert.False(segment.Resizable);
        var zone = Assert.Single(structure.DefaultZones);
        Assert.Equal($"{structure.DeviceId}:strimer", zone.Id);
        Assert.Equal("Lian Li Wireless - Strimer 24-Pin", zone.Name);
    }

    /// <summary>First sight wires the catalog product, as the Nollie 32 does for its Strimer ports: one card, the cable's LED map applied.</summary>
    [Fact]
    public void First_sight_pre_wires_the_strimer_with_its_catalog_product()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        var provider = new Slv3LightingDeviceProvider(hub, store, new Np50IdentifyTracker());
        var deviceId = Slv3LightingDeviceProvider.DeviceIdFor(Convert.ToHexString(FanMac));
        // The bridge rebuilds frames on DevicesChanged, so the wiring must be
        // in the store by the time it fires.
        var wiredAtChange = false;
        provider.DevicesChanged += () => wiredAtChange = store.Load().Devices.ZonePartitions.ContainsKey(deviceId);

        provider.OnHubStateUpdated();

        Assert.True(wiredAtChange);
        var d = store.Load().Devices;
        var chain = Assert.Single(d.PortChains[ZoneResolution.ChainKey(deviceId, 0)]);
        Assert.Equal(Strimer24PinKey, chain.Key);
        Assert.Equal(132, chain.LedCount);
        Assert.Equal(132, d.ZoneLedCounts[deviceId]);
        var zone = Assert.Single(d.ZonePartitions[deviceId]);
        Assert.Equal("Lian Li Strimer Wireless 24-Pin", zone.Name);
        Assert.Equal(Strimer24PinKey, d.AppliedMappings[ZoneResolution.CustomZoneId(deviceId, 0)].MappingId);

        var card = Assert.Single(provider.GetAll().Devices);
        Assert.Equal(ZoneResolution.CustomZoneId(deviceId, 0), card.Id);
        Assert.Equal("Lian Li Wireless - Lian Li Strimer Wireless 24-Pin", card.Name);
        Assert.Equal(132, card.LedCount);
        Assert.Equal("ledstrip", card.IconType);
        Assert.Equal(Slv3LightingDeviceProvider.GroupId, card.ParentDeviceId);
    }

    [Theory]
    [InlineData(1, "product:lianli-lian-li-strimer-wireless-gpu-2x8", 116)]
    [InlineData(3, "product:lianli-lian-li-strimer-wireless-gpu-3x8", 174)]
    [InlineData(4, "product:lianli-lian-li-strimer-wireless-cpu-2x8", 88)]
    public void Every_strimer_dev_type_pre_wires_a_catalog_product_of_its_own_led_count(byte devType, string key, int ledCount)
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = devType, FanCount = 0 });
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        var provider = new Slv3LightingDeviceProvider(hub, store, new Np50IdentifyTracker());

        provider.OnHubStateUpdated();

        var deviceId = Slv3LightingDeviceProvider.DeviceIdFor(Convert.ToHexString(FanMac));
        var chain = Assert.Single(store.Load().Devices.PortChains[ZoneResolution.ChainKey(deviceId, 0)]);
        Assert.Equal(key, chain.Key);
        Assert.Equal(ledCount, chain.LedCount);
        Assert.Equal(ledCount, Assert.Single(provider.GetAll().Devices).LedCount);
        // Community mappings pool by the default zone's DeviceKey, so each model keys on its own dev_type.
        var structure = Assert.Single(provider.GetStructures());
        Assert.True(Slv3LightingDeviceProvider.IsStrimerStructure(structure));
        Assert.EndsWith($"wireless-strimer-{devType}", structure.DeviceKey);
    }

    /// <summary>A cable the user reset or re-partitioned keeps that across rebinds; the seed only fills what was never configured.</summary>
    [Fact]
    public void Rebind_leaves_a_configured_strimer_alone()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        var fan = new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        var provider = new Slv3LightingDeviceProvider(hub, store, new Np50IdentifyTracker());
        provider.OnHubStateUpdated();
        var deviceId = Slv3LightingDeviceProvider.DeviceIdFor(Convert.ToHexString(FanMac));

        // The editor's reset-to-default leaves the count marker behind.
        store.Update(s =>
        {
            s.Devices.PortChains.Remove(ZoneResolution.ChainKey(deviceId, 0));
            s.Devices.ZonePartitions.Remove(deviceId);
            s.Devices.AppliedMappings.Remove(ZoneResolution.CustomZoneId(deviceId, 0));
        });
        fan.MasterMac = new byte[6];
        Assert.True(hub.DriveTick());
        provider.OnHubStateUpdated();
        fan.MasterMac = net.MasterMac;
        Assert.True(hub.DriveTick());
        provider.OnHubStateUpdated();

        var d = store.Load().Devices;
        Assert.False(d.PortChains.ContainsKey(ZoneResolution.ChainKey(deviceId, 0)));
        Assert.False(d.ZonePartitions.ContainsKey(deviceId));
        var card = Assert.Single(provider.GetAll().Devices);
        Assert.Equal($"{deviceId}:strimer", card.Id);
    }

    [Fact]
    public void Strimer_cards_use_the_ledstrip_icon_and_fan_cards_keep_the_fan_icon()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = Convert.FromHexString("AABBCCDDEE01"), MasterMac = net.MasterMac, RxType = 2, FanCount = 1 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var resp = provider.GetAll();

        Assert.Equal(3, resp.Devices.Count);
        Assert.Equal(1, resp.Devices.Count(d => d.IconType == "ledstrip" && d.Id.EndsWith(":strimer", StringComparison.Ordinal)));
        Assert.Equal(2, resp.Devices.Count(d => d.IconType == "fan"));
        Assert.All(resp.Devices, d => Assert.Equal(Slv3LightingDeviceProvider.GroupId, d.ParentDeviceId));
    }

    [Fact]
    public void Hydroshift_is_one_24_led_pump_ring_with_the_cooler_icon()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 10, FanCount = 0, CoolantTempC = 30 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var structure = Assert.Single(provider.GetStructures());
        Assert.True(Slv3LightingDeviceProvider.IsSingleSegmentStructure(structure));
        Assert.Equal(24, Assert.Single(structure.Segments).LedCount);
        var card = Assert.Single(provider.GetAll().Devices);
        Assert.EndsWith(":pump", card.Id, StringComparison.Ordinal);
        Assert.Equal("cooler", card.IconType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Square_hydroshift_lays_its_pump_ring_round_a_square_from_the_top_centre(int fanCount)
    {
        var (u, v) = HydroShiftRingUV(devType: 11, fanCount);
        var centreU = 0.5f / (1 + fanCount);
        Assert.Equal(centreU, u[0], 3);
        Assert.True(v[0] < 0.5f);
        Assert.Equal(v[0], v[3], 3);
        Assert.Equal(u[3], u[9], 3);
        Assert.True(u[3] > centreU && v[9] > 0.5f);
        if (fanCount > 0)
        {
            // A fan wired to the AIO stays a round ring: LED 0 of it sits right of its centre on the midline.
            Assert.Equal(0.5f, v[24], 3);
        }
    }

    [Fact]
    public void Round_hydroshift_lays_its_pump_ring_clockwise_from_the_top()
    {
        var (u, v) = HydroShiftRingUV(devType: 10, fanCount: 0);
        Assert.Equal(0.5f, u[0], 3);
        Assert.True(v[0] < 0.5f);
        Assert.Equal(0.5f, v[6], 3);
        Assert.True(u[6] > 0.5f);
        Assert.Equal(0.5f, v[18], 3);
        Assert.True(u[18] < 0.5f);
    }

    private static (float[] U, float[] V) HydroShiftRingUV(byte devType, int fanCount)
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = devType, FanCount = (byte)fanCount, CoolantTempC = 30 });
        Assert.True(hub.DriveTick());
        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var segment = Assert.Single(Assert.Single(provider.GetStructures()).Segments);
        return (segment.DefaultU!, segment.DefaultV!);
    }

    [Fact]
    public void Strimer_dev_type_without_a_known_geometry_is_not_a_device()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 7, FanCount = 0 });
        Assert.True(hub.DriveTick());

        var store = new InMemoryConfigStore();
        var provider = new Slv3LightingDeviceProvider(hub, store, new Np50IdentifyTracker());
        provider.OnHubStateUpdated();

        Assert.Empty(provider.GetStructures());
        Assert.Empty(store.Load().Devices.PortChains);
    }

    [Fact]
    public void DeviceId_and_mac_roundtrip()
    {
        var macHex = Convert.ToHexString(FanMac);
        var deviceId = Slv3LightingDeviceProvider.DeviceIdFor(macHex);
        Assert.Equal(macHex, Slv3LightingDeviceProvider.MacFromDeviceId(deviceId));
        Assert.Equal("", Slv3LightingDeviceProvider.MacFromDeviceId("lianli:port0"));
    }

    [Fact]
    public void BuildFrames_reuses_the_same_frame_instance_across_calls()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 2 });
        Assert.True(hub.DriveTick());

        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var first = provider.BuildFrames(0);
        var second = provider.BuildFrames(0);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Same(first[i], second[i]);
        }
    }
}
