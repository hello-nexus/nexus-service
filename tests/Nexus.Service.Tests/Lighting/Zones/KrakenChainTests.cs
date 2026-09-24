using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// A Kraken fan channel with an unmeasured LED count is the same problem as
/// a Smart Hub port: the firmware cannot say what fan chain is attached, so
/// the user declares the chain and each product becomes its own zone. The
/// pump ring's count is always known and stays a single, non-chainable zone.
/// </summary>
public class KrakenChainTests
{
    private const string ModelName = "NZXT Kraken 2024 Elite";
    private const int FanChannelIndex = 1;
    private static string FanZoneId => KrakenHub.ZoneIdForChannelIndex(FanChannelIndex);

    private static KrakenLightingChannel PumpRing() =>
        new(channelId: 0, accessoryId: 0x1E, accessoryName: "Kraken Pump Ring", ledCount: 24, rings: 1);

    private static KrakenLightingChannel UnknownFanChain() =>
        new(channelId: 1, accessoryId: 0x20, accessoryName: "Fans", ledCount: 0);

    private static ZoneDef Link(string name, int start, int count)
    {
        var z = new ZoneDef { Name = name };
        z.Slices.Add(new ZoneSlice { Segment = 0, Start = start, Count = count });
        return z;
    }

    /// <summary>FR12 Trio + Y50 Solo Fan on the fan channel: 68 + 8 = 76 declared LEDs.</summary>
    private static NexusSettings Chained()
    {
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[FanZoneId] = 76;
        settings.Devices.ZonePartitions[FanZoneId] = new()
        {
            Link("FR12 Trio", 0, 68),
            Link("Y50 Solo Fan", 68, 8),
        };
        settings.Devices.PortChains[ZoneResolution.ChainKey(FanZoneId, 0)] = new()
        {
            new ChainEntry { Key = "product:hyte-fr12-trio", LedCount = 68 },
            new ChainEntry { Key = "product:hyte-y50-solo", LedCount = 8 },
        };
        return settings;
    }

    [Fact]
    public void The_pump_ring_is_not_partitionable()
    {
        var settings = new NexusSettings();
        var structure = KrakenLightingDeviceProvider.BuildChannelStructure(settings, ModelName, PumpRing(), 0, KrakenProtocol.MaxDirectColors);

        Assert.False(structure.Partitionable);
        Assert.False(structure.Segments[0].Resizable);
    }

    [Fact]
    public void The_fan_channel_is_partitionable_and_resizable()
    {
        var settings = new NexusSettings();
        var structure = KrakenLightingDeviceProvider.BuildChannelStructure(settings, ModelName, UnknownFanChain(), FanChannelIndex, KrakenProtocol.MaxDirectColors);

        Assert.True(structure.Partitionable);
        Assert.True(structure.Segments[0].Resizable);
    }

    [Fact]
    public void An_unchained_fan_channel_is_one_zone_carrying_the_channel_id()
    {
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[FanZoneId] = 60;

        var zone = Assert.Single(
            KrakenLightingDeviceProvider.ResolveChannelZones(settings, ModelName, UnknownFanChain(), FanChannelIndex, KrakenProtocol.MaxDirectColors));

        Assert.True(zone.IsDefault);
        Assert.Equal(FanZoneId, zone.Id);
        Assert.Equal(60, zone.LedCount);
    }

    [Fact]
    public void A_chained_fan_channel_is_one_zone_per_product_in_chain_order()
    {
        var zones = KrakenLightingDeviceProvider.ResolveChannelZones(Chained(), ModelName, UnknownFanChain(), FanChannelIndex, KrakenProtocol.MaxDirectColors);

        Assert.Equal(2, zones.Count);
        Assert.All(zones, z => Assert.False(z.IsDefault));
        Assert.Equal(new[] { "FR12 Trio", "Y50 Solo Fan" }, zones.Select(z => z.RawName).ToArray());
        Assert.Equal(new[] { 68, 8 }, zones.Select(z => z.LedCount).ToArray());
    }

    [Fact]
    public void The_products_tile_the_fan_channel_buffer_back_to_back()
    {
        var settings = Chained();
        var structure = KrakenLightingDeviceProvider.BuildChannelStructure(settings, ModelName, UnknownFanChain(), FanChannelIndex, KrakenProtocol.MaxDirectColors);
        var zones = ZoneResolution.Resolve(structure, settings);

        // What the writer lays down: offset of each product inside the
        // channel's 76-LED buffer, and a total that matches the declared count.
        Assert.Equal(new[] { 0, 68 },
            zones.Select(z => ZoneResolution.FrameOffset(structure, z)).ToArray());
        Assert.Equal(76, zones.Sum(z => z.LedCount));
        Assert.Equal(76, structure.Segments[0].LedCount);
    }

    [Fact]
    public void Only_a_whole_channel_zone_may_resize_the_fan_channel()
    {
        var chainedSettings = Chained();
        var chained = KrakenLightingDeviceProvider.BuildChannelStructure(chainedSettings, ModelName, UnknownFanChain(), FanChannelIndex, KrakenProtocol.MaxDirectColors);
        foreach (var zone in ZoneResolution.Resolve(chained, chainedSettings))
        {
            Assert.True(ZoneResolution.WholeResizableSegment(chained, zone, chainedSettings) < 0);
        }

        var plainSettings = new NexusSettings();
        plainSettings.Devices.ZoneLedCounts[FanZoneId] = 60;
        var plain = KrakenLightingDeviceProvider.BuildChannelStructure(plainSettings, ModelName, UnknownFanChain(), FanChannelIndex, KrakenProtocol.MaxDirectColors);
        var only = ZoneResolution.Resolve(plain, plainSettings)[0];
        Assert.Equal(0, ZoneResolution.WholeResizableSegment(plain, only, plainSettings));
    }

    [Fact]
    public void A_fan_channel_only_counts_as_uncontrolled_when_every_product_does()
    {
        var settings = Chained();
        var zones = KrakenLightingDeviceProvider.ResolveChannelZones(settings, ModelName, UnknownFanChain(), FanChannelIndex, KrakenProtocol.MaxDirectColors);

        Assert.False(ZoneResolution.IsFullyUncontrolled(zones, new[] { zones[0].Id }));
        Assert.True(ZoneResolution.IsFullyUncontrolled(zones, zones.Select(z => z.Id).ToArray()));
    }

    [Fact]
    public void Dropping_the_chain_returns_the_fan_channel_to_one_zone()
    {
        var settings = Chained();
        var structure = KrakenLightingDeviceProvider.BuildChannelStructure(settings, ModelName, UnknownFanChain(), FanChannelIndex, KrakenProtocol.MaxDirectColors);

        Assert.True(ZoneResolution.DropChains(settings, structure));
        settings.Devices.ZonePartitions.Remove(FanZoneId);

        var zone = Assert.Single(ZoneResolution.Resolve(structure, settings));
        Assert.True(zone.IsDefault);
        // The declared total stays: the hardware is still wired that way, the
        // user has only stopped naming the parts.
        Assert.Equal(76, zone.LedCount);
    }
}
