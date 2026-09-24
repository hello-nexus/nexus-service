using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// OpenRGB card emission over the partition model. Default partitions must
/// reproduce the legacy emission exactly: split motherboards as one card per
/// header (persisted LED counts winning over the wire report), everything
/// else as one whole-device card. Custom partitions emit ordinal ids.
/// </summary>
public class OpenRgbZoneCardsTests
{
    private static RgbDevice Motherboard() => new()
    {
        Index = 0,
        Name = "B850I AORUS PRO",
        Type = 0,
        LedCount = 3,
        Serial = "MB01",
        Zones = new()
        {
            new RgbZone { Name = "D_LED1", ZoneType = 1, LedCount = 1 },
            new RgbZone { Name = "D_LED2", ZoneType = 1, LedCount = 1 },
            new RgbZone { Name = "LED_C1C2", ZoneType = 0, LedCount = 1 },
        },
    };

    private static RgbDevice Mouse() => new()
    {
        Index = 1,
        Name = "Gaming Mouse",
        Type = 6,
        LedCount = 4,
        Serial = "MS01",
        Zones = new()
        {
            new RgbZone { Name = "Logo", ZoneType = 1, LedCount = 1 },
            new RgbZone { Name = "Wheel", ZoneType = 1, LedCount = 3 },
        },
    };

    private static RgbDevice PhantomDevice() => new()
    {
        Index = 4,
        Name = "Philips amBX",
        Type = 11,
        LedCount = 0,
        Serial = "PH01",
        Zones = new() { new RgbZone { Name = "All", ZoneType = 0, LedCount = 0 } },
    };

    private static RgbDevice OneLedLight() => new()
    {
        Index = 2,
        Name = "Tiny",
        Type = 11,
        LedCount = 1,
        Serial = "TL01",
        Zones = new() { new RgbZone { Name = "All", ZoneType = 0, LedCount = 1 } },
    };

    [Fact]
    public void Split_motherboard_default_emits_legacy_zone_cards()
    {
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts["openrgb-s-MB01-0"] = 60;

        var resp = OpenRgbZoneSupport.BuildCards(new[] { Motherboard() }, settings, isInit: true);
        Assert.Equal(3, resp.Devices.Count);

        var first = resp.Devices[0];
        Assert.Equal("openrgb-s-MB01-0", first.Id);
        Assert.Equal("B850I AORUS PRO - D_LED1", first.Name);
        Assert.Equal("motherboard", first.Type);
        // Persisted resize choice wins over the wire-reported count.
        Assert.Equal(60, first.LedCount);
        Assert.Equal(60, first.EnabledLedCount);
        Assert.Equal("openrgb-s-MB01", first.ParentDeviceId);
        Assert.Equal(0, first.ZoneIndex);
        Assert.Equal("linear", first.ZoneType);
        Assert.True(first.ZoneResizable);
        // Each header is its own device; the board is the group above it.
        Assert.Equal("openrgb-s-MB01-0", first.DeviceId);
        Assert.True(first.ZoneCustomizable);

        var second = resp.Devices[1];
        Assert.Equal("openrgb-s-MB01-1", second.Id);
        Assert.Equal(1, second.LedCount);

        var third = resp.Devices[2];
        Assert.Equal("openrgb-s-MB01-2", third.Id);
        Assert.Equal("B850I AORUS PRO - LED_C1C2", third.Name);
        Assert.Equal("single", third.ZoneType);
        Assert.True(third.ZoneResizable);
    }

    [Fact]
    public void Non_split_device_default_emits_single_whole_device_card()
    {
        var resp = OpenRgbZoneSupport.BuildCards(new[] { Mouse() }, new NexusSettings(), isInit: true);
        Assert.Single(resp.Devices);
        var card = resp.Devices[0];
        Assert.Equal("openrgb-s-MS01", card.Id);
        Assert.Equal("Gaming Mouse", card.Name);
        Assert.Equal("mouse", card.Type);
        Assert.Equal(4, card.LedCount);
        Assert.Equal(4, card.EnabledLedCount);
        Assert.Null(card.ParentDeviceId);
        Assert.Null(card.ZoneIndex);
        Assert.Null(card.ZoneType);
        Assert.False(card.ZoneResizable);
        Assert.Equal("openrgb-s-MS01", card.DeviceId);
        Assert.True(card.ZoneCustomizable);
    }

    [Fact]
    public void Cards_carry_the_competing_app_ids_for_their_vendor()
    {
        var mouse = Mouse();
        mouse.Vendor = "Corsair";
        var mb = Motherboard();
        mb.Vendor = "Gigabyte";

        var resp = OpenRgbZoneSupport.BuildCards(new[] { mb, mouse }, new NexusSettings(), isInit: true);

        // Every zone card of the split motherboard resolves to the same apps.
        foreach (var card in resp.Devices.FindAll(d => d.ParentDeviceId == "openrgb-s-MB01"))
        {
            Assert.Contains("gigabyte-rgb-fusion", card.ConflictAppIds);
            Assert.DoesNotContain("icue", card.ConflictAppIds);
        }
        var mouseCard = resp.Devices.Find(d => d.Id == "openrgb-s-MS01")!;
        Assert.Contains("icue", mouseCard.ConflictAppIds);
        Assert.Contains("signalrgb", mouseCard.ConflictAppIds);

        var zones = resp.Devices.FindAll(d => d.ParentDeviceId == "openrgb-s-MB01");
        Assert.NotSame(zones[0].ConflictAppIds, zones[1].ConflictAppIds);
    }

    [Fact]
    public void Zero_led_whole_device_card_is_dropped()
    {
        var resp = OpenRgbZoneSupport.BuildCards(new[] { PhantomDevice(), Mouse() }, new NexusSettings(), isInit: true);
        Assert.Single(resp.Devices);
        Assert.Equal("openrgb-s-MS01", resp.Devices[0].Id);
        // Dropped phantom must not consume slot 0: the surviving Mouse keeps the
        // same default layout it gets when emitted alone.
        var soloMouse = OpenRgbZoneSupport.BuildCards(new[] { Mouse() }, new NexusSettings(), isInit: true).Devices[0];
        Assert.Equal(soloMouse.CanvasX, resp.Devices[0].CanvasX);
        Assert.Equal(soloMouse.CanvasY, resp.Devices[0].CanvasY);
    }

    [Fact]
    public void Zero_led_device_stays_shown_when_latched_drivable()
    {
        // A device that settled drivable is latched by StableId; a later fetch
        // that transiently reports 0 LEDs must not hide it. The hide errs toward
        // showing real hardware, never toward hiding it.
        var flapped = Mouse();
        flapped.LedCount = 0;

        var dropped = OpenRgbZoneSupport.BuildCards(new[] { flapped }, new NexusSettings(), isInit: true);
        Assert.Empty(dropped.Devices);

        var latched = new HashSet<string> { "openrgb-s-MS01" };
        var shown = OpenRgbZoneSupport.BuildCards(new[] { flapped }, new NexusSettings(), isInit: true, latched);
        Assert.Single(shown.Devices);
        Assert.Equal("openrgb-s-MS01", shown.Devices[0].Id);
    }

    [Fact]
    public void One_led_device_is_not_zone_customizable()
    {
        var resp = OpenRgbZoneSupport.BuildCards(new[] { OneLedLight() }, new NexusSettings(), isInit: true);
        Assert.Single(resp.Devices);
        Assert.False(resp.Devices[0].ZoneCustomizable);
        Assert.Equal(resp.Devices[0].Id, resp.Devices[0].DeviceId);
    }

    [Fact]
    public void Whole_device_card_layers_mapping_and_override_disables()
    {
        var settings = new NexusSettings();
        var artifact = new Nexus.Service.Lighting.Mappings.MappingArtifact();
        artifact.Zones.Add(new Nexus.Service.Lighting.Mappings.MappingZone
        {
            ZoneIndex = 0,
            Disabled = { 0, 1 },
        });
        settings.Devices.AppliedMappings["openrgb-s-MS01"] =
            new Nexus.Service.Lighting.Mappings.AppliedMappingRef { Name = "m", Artifact = artifact };
        settings.Devices.DeviceLedOverrides["openrgb-s-MS01"] = new List<SegmentLedOverride>
        {
            // Re-enables a mapping-disabled LED (wheel LED 0 = zone-local 1).
            new() { Segment = 1, LedIndex = 0, Disabled = false },
            // Disables one the mapping left on (wheel LED 2 = zone-local 3).
            new() { Segment = 1, LedIndex = 2, Disabled = true },
        };

        var resp = OpenRgbZoneSupport.BuildCards(new[] { Mouse() }, settings, isInit: true);
        var card = resp.Devices[0];
        Assert.Equal(4, card.LedCount);
        // Mapping disables zone-local 0 and 1; the override re-enables 1 and
        // disables 3, leaving LEDs 1 and 2 on.
        Assert.Equal(2, card.EnabledLedCount);
    }

    [Fact]
    public void Custom_partition_on_fixed_device_emits_ordinal_cards()
    {
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions["openrgb-s-MS01"] = new List<ZoneDef>
        {
            new()
            {
                Name = "Front",
                Slices =
                {
                    new ZoneSlice { Segment = 0, Start = 0, Count = 1 },
                    new ZoneSlice { Segment = 1, Start = 0, Count = 1 },
                },
            },
            new() { Name = "Back", Slices = { new ZoneSlice { Segment = 1, Start = 1, Count = 2 } } },
        };

        var resp = OpenRgbZoneSupport.BuildCards(new[] { Mouse() }, settings, isInit: true);
        Assert.Equal(2, resp.Devices.Count);

        var front = resp.Devices[0];
        Assert.Equal("openrgb-s-MS01:z0", front.Id);
        Assert.Equal("Gaming Mouse - Front", front.Name);
        Assert.Equal(2, front.LedCount);
        Assert.Equal(2, front.EnabledLedCount);
        Assert.Equal("", front.DeviceKey);
        Assert.Equal("openrgb-s-MS01", front.ParentDeviceId);
        Assert.Equal("openrgb-s-MS01", front.DeviceId);
        Assert.Equal(0, front.ZoneIndex);
        Assert.False(front.ZoneResizable);
        Assert.True(front.ZoneCustomizable);

        var back = resp.Devices[1];
        Assert.Equal("openrgb-s-MS01:z1", back.Id);
        Assert.Equal(2, back.LedCount);
        Assert.Equal(1, back.ZoneIndex);
    }

    [Fact]
    public void A_board_level_partition_no_longer_describes_a_split_motherboard()
    {
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts["openrgb-s-MB01-0"] = 60;
        // Legacy shape: one partition tiling every header at once, from before
        // each header became its own device. SplitMotherboardDeviceMigration
        // drops it, but GetAll can run first, and the engine-frame path
        // resolves ports regardless - so the cards must agree with the frames
        // rather than briefly describing a device that no longer exists.
        settings.Devices.ZonePartitions["openrgb-s-MB01"] = new List<ZoneDef>
        {
            new() { Name = "Fans", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 60 } } },
            new() { Name = "Pump", Slices = { new ZoneSlice { Segment = 1, Start = 0, Count = 1 } } },
            new() { Name = "Strimmer", Slices = { new ZoneSlice { Segment = 2, Start = 0, Count = 1 } } },
        };

        var resp = OpenRgbZoneSupport.BuildCards(new[] { Motherboard() }, settings, isInit: true);
        Assert.Equal(3, resp.Devices.Count);
        Assert.Equal("openrgb-s-MB01-0", resp.Devices[0].Id);
        Assert.Equal("B850I AORUS PRO - D_LED1", resp.Devices[0].Name);
        Assert.Equal(60, resp.Devices[0].LedCount);
        Assert.True(resp.Devices[0].ZoneResizable);
        Assert.Equal("openrgb-s-MB01-0", resp.Devices[0].DeviceId);
    }

    [Fact]
    public void A_chain_on_one_header_leaves_the_others_alone()
    {
        var settings = new NexusSettings();
        var portId = "openrgb-s-MB01-0";
        settings.Devices.ZoneLedCounts[portId] = 60;
        settings.Devices.ZonePartitions[portId] = new List<ZoneDef>
        {
            new() { Name = "QX Fan", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 34 } } },
            new() { Name = "Generic Strip", Slices = { new ZoneSlice { Segment = 0, Start = 34, Count = 26 } } },
        };
        settings.Devices.PortChains[ZoneResolution.ChainKey(portId, 0)] = new List<ChainEntry>
        {
            new() { Key = "product:qx", LedCount = 34 },
            new() { Key = "generic:strip", LedCount = 26 },
        };

        var resp = OpenRgbZoneSupport.BuildCards(new[] { Motherboard() }, settings, isInit: true);

        // Two products on header 1, then the two untouched headers.
        Assert.Equal(4, resp.Devices.Count);
        Assert.Equal($"{portId}:z0", resp.Devices[0].Id);
        Assert.Equal($"{portId}:z1", resp.Devices[1].Id);
        Assert.Equal("openrgb-s-MB01-1", resp.Devices[2].Id);
        Assert.Equal("openrgb-s-MB01-2", resp.Devices[3].Id);
        Assert.Equal(new[] { 34, 26 }, new[] { resp.Devices[0].LedCount, resp.Devices[1].LedCount });
        // Both chain cards point at the port, which is what the chain and zone
        // editors address, and neither may resize the header on its own.
        Assert.Equal(portId, resp.Devices[0].DeviceId);
        Assert.Equal(portId, resp.Devices[1].DeviceId);
        Assert.False(resp.Devices[0].ZoneResizable);
        Assert.False(resp.Devices[1].ZoneResizable);
        // The untouched header keeps its whole-segment resize.
        Assert.True(resp.Devices[2].ZoneResizable);
    }

    [Fact]
    public void Structure_marks_only_split_motherboard_headers_resizable()
    {
        var settings = new NexusSettings();
        var mobo = OpenRgbZoneSupport.BuildStructure(Motherboard(), settings);
        Assert.Equal(3, mobo.Segments.Count);
        Assert.All(mobo.Segments, s => Assert.True(s.Resizable));
        Assert.Equal(3, mobo.DefaultZones.Count);
        // Split-motherboard default zones expose the segment default name as
        // the raw name; the card name keeps the device prefix.
        Assert.Equal("D_LED1", mobo.DefaultZones[0].RawName);
        Assert.Equal("B850I AORUS PRO - D_LED1", mobo.DefaultZones[0].Name);

        var mouse = OpenRgbZoneSupport.BuildStructure(Mouse(), settings);
        Assert.Equal(2, mouse.Segments.Count);
        Assert.All(mouse.Segments, s => Assert.False(s.Resizable));
        Assert.Single(mouse.DefaultZones);
        Assert.Equal("openrgb-s-MS01", mouse.DefaultZones[0].Id);
        Assert.Equal(2, mouse.DefaultZones[0].Slices.Count);
        // The whole-device default zone's raw name marks the full LED space.
        Assert.Equal("All", mouse.DefaultZones[0].RawName);
        Assert.Equal("Gaming Mouse", mouse.DefaultZones[0].Name);
    }

    [Fact]
    public void Zoneless_device_gets_one_fixed_segment_covering_the_device()
    {
        var bare = new RgbDevice { Index = 3, Name = "Bare", Type = 4, LedCount = 12, Serial = "BR01" };
        var structure = OpenRgbZoneSupport.BuildStructure(bare, new NexusSettings());
        Assert.Single(structure.Segments);
        Assert.Equal(12, structure.Segments[0].LedCount);
        Assert.False(structure.Segments[0].Resizable);
    }
}

/// <summary>
/// A split card's header names a device that owns no card of its own - an ARGB
/// port, a keeb - so the rename has to travel on the device id or the header
/// cannot be renamed at all.
/// </summary>
public class DeviceNameEchoTests
{
    private static List<LightingDevice> TwoZonesOfOneDevice() => new()
    {
        new LightingDevice { Id = "port:z0", DeviceId = "port", ParentDeviceId = "board", Name = "Board - Port - Fan 1" },
        new LightingDevice { Id = "port:z1", DeviceId = "port", ParentDeviceId = "board", Name = "Board - Port - Fan 2" },
    };

    [Fact]
    public void A_name_stored_under_the_device_id_reaches_every_zone()
    {
        var devices = TwoZonesOfOneDevice();
        LightingDeviceNames.Apply(devices, new Dictionary<string, string> { ["port"] = "Front intake" });
        Assert.All(devices, d => Assert.Equal("Front intake", d.DeviceName));
        // The zones keep their own names; only the header changes.
        Assert.Equal("Board - Port - Fan 1", devices[0].Name);
    }

    [Fact]
    public void Card_parent_and_device_names_stay_independent()
    {
        var devices = TwoZonesOfOneDevice();
        LightingDeviceNames.Apply(devices, new Dictionary<string, string>
        {
            ["port:z0"] = "Top fan",
            ["port"] = "Front intake",
            ["board"] = "My board",
        });
        Assert.Equal("Top fan", devices[0].Name);
        Assert.Equal("Board - Port - Fan 1", devices[0].OriginalName);
        Assert.Equal("Front intake", devices[0].DeviceName);
        Assert.Equal("My board", devices[0].ParentName);
    }
}
