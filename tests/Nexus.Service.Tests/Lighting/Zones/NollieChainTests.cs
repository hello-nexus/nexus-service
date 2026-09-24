using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Nollie;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// A Nollie ARGB channel is the same problem as a Smart Hub port: the
/// protocol has no read command, so the user declares the chain and each
/// product becomes its own zone. The card list, the engine frames, and the
/// bytes the writer lays into the channel buffer all read these zones.
/// </summary>
public class NollieChainTests
{
    private static NolliePort Channel(NollieController controller) => controller.Spec.Ports[0];

    private static NollieController Attach()
    {
        var spec = NollieProtocol.Lookup(0x16D5, 0x2A01)!;
        var device = new NollieLightingDeviceProviderTests.FakeHidDevice(0x16D5, 0x2A01, "path-CHAIN", "CHAIN");
        return new NollieController(device, spec);
    }

    private static ZoneDef Link(string name, int start, int count)
    {
        var z = new ZoneDef { Name = name };
        z.Slices.Add(new ZoneSlice { Segment = 0, Start = start, Count = count });
        return z;
    }

    /// <summary>FR12 Trio + Y50 Solo Fan on the channel: 68 + 8 = 76 declared LEDs.</summary>
    private static NexusSettings Chained(string channelId)
    {
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[channelId] = 76;
        settings.Devices.ZonePartitions[channelId] = new()
        {
            Link("FR12 Trio", 0, 68),
            Link("Y50 Solo Fan", 68, 8),
        };
        settings.Devices.PortChains[ZoneResolution.ChainKey(channelId, 0)] = new()
        {
            new ChainEntry { Key = "product:hyte-fr12-trio", LedCount = 68 },
            new ChainEntry { Key = "product:hyte-y50-solo", LedCount = 8 },
        };
        return settings;
    }

    [Fact]
    public void An_unchained_channel_is_one_zone_carrying_the_channel_id()
    {
        var controller = Attach();
        var channelId = NollieLightingDeviceProvider.PortId(controller.DeviceId, Channel(controller));
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[channelId] = 60;

        var zone = Assert.Single(NollieLightingDeviceProvider.ResolvePortZones(controller, Channel(controller), settings));

        Assert.True(zone.IsDefault);
        Assert.Equal(channelId, zone.Id);
        Assert.Equal(60, zone.LedCount);
    }

    [Fact]
    public void A_chained_channel_is_one_zone_per_product_in_chain_order()
    {
        var controller = Attach();
        var channelId = NollieLightingDeviceProvider.PortId(controller.DeviceId, Channel(controller));
        var settings = Chained(channelId);

        var zones = NollieLightingDeviceProvider.ResolvePortZones(controller, Channel(controller), settings);

        Assert.Equal(2, zones.Count);
        Assert.All(zones, z => Assert.False(z.IsDefault));
        Assert.Equal(new[] { "FR12 Trio", "Y50 Solo Fan" }, zones.Select(z => z.RawName).ToArray());
        Assert.Equal(new[] { 68, 8 }, zones.Select(z => z.LedCount).ToArray());
    }

    [Fact]
    public void The_products_tile_the_channel_buffer_back_to_back()
    {
        var controller = Attach();
        var channelId = NollieLightingDeviceProvider.PortId(controller.DeviceId, Channel(controller));
        var settings = Chained(channelId);

        var structure = NollieLightingDeviceProvider.BuildPortStructure(controller, Channel(controller), settings.Devices.ZoneLedCounts);
        var zones = ZoneResolution.Resolve(structure, settings);

        // What the writer lays down: offset of each product inside the
        // channel's 76-LED buffer, and a total that matches the declared count.
        Assert.Equal(new[] { 0, 68 },
            zones.Select(z => ZoneResolution.FrameOffset(structure, z)).ToArray());
        Assert.Equal(76, zones.Sum(z => z.LedCount));
        Assert.Equal(76, structure.Segments[0].LedCount);
    }

    [Fact]
    public void Only_a_whole_channel_zone_may_resize_the_channel()
    {
        var controller = Attach();
        var channelId = NollieLightingDeviceProvider.PortId(controller.DeviceId, Channel(controller));
        var chainedSettings = Chained(channelId);
        var chained = NollieLightingDeviceProvider.BuildPortStructure(controller, Channel(controller), chainedSettings.Devices.ZoneLedCounts);
        foreach (var zone in ZoneResolution.Resolve(chained, chainedSettings))
        {
            Assert.True(ZoneResolution.WholeResizableSegment(chained, zone, chainedSettings) < 0);
        }

        var plainSettings = new NexusSettings();
        plainSettings.Devices.ZoneLedCounts[channelId] = 60;
        var plain = NollieLightingDeviceProvider.BuildPortStructure(controller, Channel(controller), plainSettings.Devices.ZoneLedCounts);
        var only = ZoneResolution.Resolve(plain, plainSettings)[0];
        Assert.Equal(0, ZoneResolution.WholeResizableSegment(plain, only, plainSettings));
    }

    [Fact]
    public void A_channel_only_counts_as_uncontrolled_when_every_product_does()
    {
        var controller = Attach();
        var channelId = NollieLightingDeviceProvider.PortId(controller.DeviceId, Channel(controller));
        var settings = Chained(channelId);
        var zones = NollieLightingDeviceProvider.ResolvePortZones(controller, Channel(controller), settings);

        Assert.False(ZoneResolution.IsFullyUncontrolled(zones, new[] { zones[0].Id }));
        Assert.True(ZoneResolution.IsFullyUncontrolled(zones, zones.Select(z => z.Id).ToArray()));
    }

    [Fact]
    public void Dropping_the_chain_returns_the_channel_to_one_zone()
    {
        var controller = Attach();
        var channelId = NollieLightingDeviceProvider.PortId(controller.DeviceId, Channel(controller));
        var settings = Chained(channelId);
        var structure = NollieLightingDeviceProvider.BuildPortStructure(controller, Channel(controller), settings.Devices.ZoneLedCounts);

        Assert.True(ZoneResolution.DropChains(settings, structure));
        settings.Devices.ZonePartitions.Remove(channelId);

        var zone = Assert.Single(ZoneResolution.Resolve(structure, settings));
        Assert.True(zone.IsDefault);
        // The declared total stays: the hardware is still wired that way, the
        // user has only stopped naming the parts.
        Assert.Equal(76, zone.LedCount);
    }
}
