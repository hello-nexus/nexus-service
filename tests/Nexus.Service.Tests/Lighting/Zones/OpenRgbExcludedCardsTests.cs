using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Card emission for detector-excluded devices: the live entry (pre-bounce
/// real device or the fork's zero-LED placeholder) is suppressed and the card
/// renders from the persisted snapshot instead, so the device stays visible
/// and un-ignorable while OpenRGB no longer detects it.
/// </summary>
public class OpenRgbExcludedCardsTests
{
    private static RgbDevice Keyboard() => new()
    {
        Index = 0,
        Name = "Corsair K70 RGB",
        Vendor = "Corsair",
        Type = 5,
        LedCount = 104,
        Serial = "K70A",
        Zones = new() { new RgbZone { Name = "Keyboard", ZoneType = 2, LedCount = 104 } },
    };

    private static RgbDevice Placeholder() => new()
    {
        Index = 0,
        Name = "Corsair K70 RGB",
        Vendor = "Corsair",
        Type = 21,
        LedCount = 0,
        Serial = "K70A",
        Zones = new(),
    };

    private static NexusSettings ExcludedSettings()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70A");
        settings.Devices.OpenRgbDetectorExclusions["openrgb-s-K70A"] = new OpenRgbDetectorExclusion
        {
            DetectorName = "Corsair K70 RGB",
            Vendor = "Corsair",
            Serial = "K70A",
            LedCount = 104,
            Type = 5,
        };
        return settings;
    }

    [Fact]
    public void Snapshot_card_is_emitted_while_hardware_is_undetected()
    {
        var resp = OpenRgbZoneSupport.BuildCards(System.Array.Empty<RgbDevice>(), ExcludedSettings(), isInit: true);

        var card = Assert.Single(resp.Devices);
        Assert.Equal("openrgb-s-K70A", card.Id);
        Assert.Equal("Corsair K70 RGB", card.Name);
        Assert.Equal("keyboard", card.Type);
        Assert.Equal(104, card.LedCount);
        Assert.False(card.ZoneCustomizable);
    }

    [Fact]
    public void Live_excluded_device_is_replaced_by_its_snapshot_card()
    {
        var resp = OpenRgbZoneSupport.BuildCards(new[] { Keyboard() }, ExcludedSettings(), isInit: true);

        var card = Assert.Single(resp.Devices);
        Assert.Equal("openrgb-s-K70A", card.Id);
        Assert.Equal(104, card.LedCount);
    }

    [Fact]
    public void Placeholder_dummy_is_replaced_even_when_latched_drivable()
    {
        // The device latched drivable before it was excluded; the latch must not
        // resurrect the zero-LED placeholder as a second card.
        var latched = new HashSet<string> { "openrgb-s-K70A" };
        var resp = OpenRgbZoneSupport.BuildCards(new[] { Placeholder() }, ExcludedSettings(), isInit: true, latched);

        var card = Assert.Single(resp.Devices);
        Assert.Equal("openrgb-s-K70A", card.Id);
        Assert.Equal(104, card.LedCount);
    }

    [Fact]
    public void The_card_shows_the_device_name_not_the_detector_that_was_denylisted()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-l-I2C__i801__address_0x18");
        settings.Devices.OpenRgbDetectorExclusions["openrgb-l-I2C__i801__address_0x18"] = new OpenRgbDetectorExclusion
        {
            DetectorName = "Corsair DRAM",
            DeviceName = "Corsair Vengeance RGB DDR5",
            Vendor = "Corsair",
            Location = "I2C: i801, address 0x18",
            LedCount = 10,
            Type = 10,
        };

        var card = Assert.Single(OpenRgbZoneSupport.BuildCards(System.Array.Empty<RgbDevice>(), settings, isInit: true).Devices);

        Assert.Equal("Corsair Vengeance RGB DDR5", card.Name);
    }

    [Fact]
    public void A_snapshot_without_a_device_name_still_shows_its_detector_name()
    {
        var card = Assert.Single(OpenRgbZoneSupport.BuildCards(System.Array.Empty<RgbDevice>(), ExcludedSettings(), isInit: true).Devices);

        Assert.Equal("Corsair K70 RGB", card.Name);
    }

    [Fact]
    public void Unrelated_devices_keep_their_cards_alongside_the_snapshot()
    {
        var mouse = new RgbDevice
        {
            Index = 1,
            Name = "Gaming Mouse",
            Type = 6,
            LedCount = 4,
            Serial = "MS01",
            Zones = new() { new RgbZone { Name = "All", ZoneType = 0, LedCount = 4 } },
        };
        var resp = OpenRgbZoneSupport.BuildCards(new[] { mouse }, ExcludedSettings(), isInit: true);

        Assert.Equal(2, resp.Devices.Count);
        Assert.Equal("openrgb-s-MS01", resp.Devices[0].Id);
        Assert.Equal("openrgb-s-K70A", resp.Devices[1].Id);
    }
}
