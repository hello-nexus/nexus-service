using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>One malformed OpenRGB device must not fail the card list, the structure list, or zone lookup for the good devices.</summary>
public class OpenRgbBadDeviceProviderTests
{
    private static RgbDevice Good(int index, string location) => new()
    {
        Index = index,
        Address = index,
        Name = "Good " + index,
        LedCount = 10,
        Location = location,
    };

    /// <summary>A null zone entry makes every structure build over this device throw.</summary>
    private static RgbDevice Malformed()
    {
        var d = new RgbDevice { Index = 1, Address = 1, Name = "Malformed", Type = 5, LedCount = 4, Location = "HID: bad" };
        d.Zones.Add(null!);
        return d;
    }

    [Fact]
    public void BuildCards_skips_the_malformed_device_and_keeps_the_rest()
    {
        var devices = new List<RgbDevice> { Good(0, "I2C: a"), Malformed(), Good(2, "I2C: b") };

        var cards = OpenRgbZoneSupport.BuildCards(devices, new NexusSettings(), isInit: true).Devices;

        Assert.Equal(2, cards.Count);
        Assert.DoesNotContain(cards, c => c.Id.Contains("HID__bad"));
    }

    [Fact]
    public void BuildCards_slots_stay_packed_when_a_device_is_skipped()
    {
        var devices = new List<RgbDevice> { Malformed(), Good(0, "I2C: a"), Good(2, "I2C: b") };
        var allGood = new List<RgbDevice> { Good(0, "I2C: a"), Good(2, "I2C: b") };

        var withBad = OpenRgbZoneSupport.BuildCards(devices, new NexusSettings(), isInit: true).Devices;
        var without = OpenRgbZoneSupport.BuildCards(allGood, new NexusSettings(), isInit: true).Devices;

        Assert.Equal(without.Select(c => (c.CanvasX, c.CanvasY)), withBad.Select(c => (c.CanvasX, c.CanvasY)));
    }

    [Fact]
    public void Warning_is_logged_once_per_device()
    {
        var bad = new RgbDevice { Index = 7, Address = 7, Name = "Warned once", LedCount = 1, Location = "HID: warn-once" };
        var ex = new System.InvalidOperationException("x");

        Assert.True(OpenRgbZoneSupport.WarnBadDeviceOnce(bad, ex));
        Assert.False(OpenRgbZoneSupport.WarnBadDeviceOnce(bad, ex));
    }

    [Fact]
    public void ZoneTopology_FindZone_skips_a_structure_that_cannot_resolve()
    {
        var bad = new DeviceStructure { DeviceId = "dev", Segments = { new StructureSegment { Index = 0, LedCount = 1, FrameLedCount = 1 } }, DefaultZones = { null! } };
        var good = new DeviceStructure
        {
            DeviceId = "dev-good",
            Segments = { new StructureSegment { Index = 0, LedCount = 2, FrameLedCount = 2 } },
            DefaultZones = { new DefaultZoneDef { Id = "dev-good", Name = "g", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 2 } } } },
        };
        var topology = BuildTopology(new FixedSource(bad, good));

        var hit = topology.FindZone("dev-good", new NexusSettings());

        Assert.NotNull(hit);
        Assert.Equal("dev-good", hit!.Value.Zone.Id);
    }

    private sealed class FixedSource : IDeviceStructureSource
    {
        private readonly DeviceStructure[] _structures;
        public FixedSource(params DeviceStructure[] structures) => _structures = structures;
        public IReadOnlyList<DeviceStructure> GetStructures() => _structures;
    }

    private static ZoneTopology BuildTopology(IDeviceStructureSource source)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-test-bad-topo-" + System.Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        return new ZoneTopology(new[] { source }, new TestableConfigStore(System.IO.Path.Combine(dir, "settings.json")),
            new Nexus.Service.Lighting.Engine.LightingEngine(), new Nexus.Service.Lighting.Mappings.ContributorFrameLayouts());
    }
}
