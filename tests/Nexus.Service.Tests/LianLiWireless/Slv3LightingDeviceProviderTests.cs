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
