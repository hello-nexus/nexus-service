using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// <see cref="OpenRgbZoneSupport.IsFullyUncontrolled"/> gates whether RgbBridge
/// claims direct mode / pushes frames for a physical device: true only when
/// every card the device would emit is in the uncontrolled set.
/// </summary>
public class OpenRgbZoneUncontrolledTests
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

    [Fact]
    public void Empty_uncontrolled_list_is_never_fully_uncontrolled()
    {
        Assert.False(OpenRgbZoneSupport.IsFullyUncontrolled(Mouse(), new NexusSettings()));
    }

    [Fact]
    public void Whole_device_card_uncontrolled_marks_device_fully_uncontrolled()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MS01");

        Assert.True(OpenRgbZoneSupport.IsFullyUncontrolled(Mouse(), settings));
    }

    [Fact]
    public void Other_ids_uncontrolled_does_not_affect_unrelated_device()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-OTHER");

        Assert.False(OpenRgbZoneSupport.IsFullyUncontrolled(Mouse(), settings));
    }

    [Fact]
    public void Split_motherboard_requires_every_zone_uncontrolled()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-0");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-1");
        // openrgb-s-MB01-2 stays controlled.

        Assert.False(OpenRgbZoneSupport.IsFullyUncontrolled(Motherboard(), settings));
    }

    [Fact]
    public void Split_motherboard_fully_uncontrolled_when_all_zones_listed()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-0");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-1");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-2");

        Assert.True(OpenRgbZoneSupport.IsFullyUncontrolled(Motherboard(), settings));
    }
}
