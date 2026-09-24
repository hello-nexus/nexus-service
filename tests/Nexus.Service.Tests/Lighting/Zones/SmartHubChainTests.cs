using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// A Smart Hub ARGB port is the same problem as a motherboard header: the
/// firmware cannot enumerate what is daisy-chained to it, so the user declares
/// the chain and each product becomes its own zone. The card list, the engine
/// frames, and the bytes the writer lays into the port buffer all read these
/// zones, so they have to agree - a port split three ways in one of them and
/// not the others lights the wrong LEDs instead of failing visibly.
/// </summary>
public class SmartHubChainTests
{
    private const string HubId = "smarthub:SH01";
    private const int Channel = 2;
    private static string PortId => $"{HubId}:port{Channel}";

    private static ZoneDef Link(string name, int start, int count)
    {
        var z = new ZoneDef { Name = name };
        z.Slices.Add(new ZoneSlice { Segment = 0, Start = start, Count = count });
        return z;
    }

    /// <summary>FR12 Trio + Y50 Solo Fan on port 2: 68 + 8 = 76 declared LEDs.</summary>
    private static NexusSettings Chained()
    {
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[PortId] = 76;
        settings.Devices.ZonePartitions[PortId] = new()
        {
            Link("FR12 Trio", 0, 68),
            Link("Y50 Solo Fan", 68, 8),
        };
        settings.Devices.PortChains[ZoneResolution.ChainKey(PortId, 0)] = new()
        {
            new ChainEntry { Key = "product:hyte-fr12-trio", LedCount = 68 },
            new ChainEntry { Key = "product:hyte-y50-solo", LedCount = 8 },
        };
        return settings;
    }

    [Fact]
    public void An_unchained_port_is_one_zone_carrying_the_port_id()
    {
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[PortId] = 60;

        var zone = Assert.Single(SmartHubLightingDeviceProvider.ResolvePortZones(settings, HubId, Channel, 0));

        Assert.True(zone.IsDefault);
        Assert.Equal(PortId, zone.Id);
        Assert.Equal(60, zone.LedCount);
    }

    [Fact]
    public void The_declared_count_wins_over_the_firmware_report()
    {
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[PortId] = 45;

        // The hub reports 0 for every port until the user says otherwise.
        var zone = Assert.Single(SmartHubLightingDeviceProvider.ResolvePortZones(settings, HubId, Channel, 0));
        Assert.Equal(45, zone.LedCount);
    }

    [Fact]
    public void A_chained_port_is_one_zone_per_product_in_chain_order()
    {
        var zones = SmartHubLightingDeviceProvider.ResolvePortZones(Chained(), HubId, Channel, 0);

        Assert.Equal(2, zones.Count);
        Assert.All(zones, z => Assert.False(z.IsDefault));
        Assert.Equal(new[] { "FR12 Trio", "Y50 Solo Fan" }, zones.Select(z => z.RawName).ToArray());
        Assert.Equal(new[] { 68, 8 }, zones.Select(z => z.LedCount).ToArray());
    }

    [Fact]
    public void The_products_tile_the_port_buffer_back_to_back()
    {
        var structure = SmartHubLightingDeviceProvider.BuildPortStructure(Chained(), HubId, Channel, 0);
        var zones = ZoneResolution.Resolve(structure, Chained());

        // What the writer lays down: offset of each product inside the port's
        // 76-LED buffer, and a total that matches the declared count.
        Assert.Equal(new[] { 0, 68 },
            zones.Select(z => ZoneResolution.FrameOffset(structure, z)).ToArray());
        Assert.Equal(76, zones.Sum(z => z.LedCount));
        Assert.Equal(76, structure.Segments[0].LedCount);
    }

    [Fact]
    public void Only_a_whole_port_zone_may_resize_the_port()
    {
        var chainedSettings = Chained();
        var chained = SmartHubLightingDeviceProvider.BuildPortStructure(chainedSettings, HubId, Channel, 0);
        foreach (var zone in ZoneResolution.Resolve(chained, chainedSettings))
        {
            Assert.True(ZoneResolution.WholeResizableSegment(chained, zone, chainedSettings) < 0);
        }

        var plainSettings = new NexusSettings();
        plainSettings.Devices.ZoneLedCounts[PortId] = 60;
        var plain = SmartHubLightingDeviceProvider.BuildPortStructure(plainSettings, HubId, Channel, 0);
        var only = ZoneResolution.Resolve(plain, plainSettings)[0];
        Assert.Equal(0, ZoneResolution.WholeResizableSegment(plain, only, plainSettings));
    }

    [Fact]
    public void A_port_only_counts_as_uncontrolled_when_every_product_does()
    {
        var settings = Chained();
        var zones = SmartHubLightingDeviceProvider.ResolvePortZones(settings, HubId, Channel, 0);

        Assert.False(ZoneResolution.IsFullyUncontrolled(zones, new[] { zones[0].Id }));
        Assert.True(ZoneResolution.IsFullyUncontrolled(zones, zones.Select(z => z.Id).ToArray()));
    }

    [Fact]
    public void Dropping_the_chain_returns_the_port_to_one_zone()
    {
        var settings = Chained();
        var structure = SmartHubLightingDeviceProvider.BuildPortStructure(settings, HubId, Channel, 0);

        Assert.True(ZoneResolution.DropChains(settings, structure));
        settings.Devices.ZonePartitions.Remove(PortId);

        var zone = Assert.Single(ZoneResolution.Resolve(structure, settings));
        Assert.True(zone.IsDefault);
        // The declared total stays: the hardware is still wired that way, the
        // user has only stopped naming the parts.
        Assert.Equal(76, zone.LedCount);
    }

    [Fact]
    public void A_partition_left_without_its_chain_falls_back_rather_than_tiling()
    {
        var settings = Chained();
        var structure = SmartHubLightingDeviceProvider.BuildPortStructure(settings, HubId, Channel, 0);
        ZoneResolution.DropChains(settings, structure);

        // The partition alone no longer justifies splitting a resizable
        // segment, so resolution self-heals instead of trusting stale slices.
        var zone = Assert.Single(ZoneResolution.Resolve(structure, settings));
        Assert.True(zone.IsDefault);
    }
}
