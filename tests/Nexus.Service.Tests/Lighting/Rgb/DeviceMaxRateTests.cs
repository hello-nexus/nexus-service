using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class DeviceMaxRateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "max-rate-" + Guid.NewGuid().ToString("N"));

    public DeviceMaxRateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static RgbDevice Gpu() => new()
    {
        Index = 0,
        Name = "GPU",
        Vendor = "Gigabyte",
        Type = 2,
        LedCount = 8,
        Serial = "G1",
        Location = "I2C: AMD ADL, address 0x75",
        Zones = new() { new RgbZone { Name = "Logo", ZoneType = 1, LedCount = 8 } },
    };

    [Fact]
    public void OpenRgb_card_reports_support_and_the_stored_cap()
    {
        var settings = new NexusSettings();
        var card = Assert.Single(OpenRgbZoneSupport.BuildCards(new[] { Gpu() }, settings, isInit: true).Devices);
        Assert.True(card.RateCapSupported);
        Assert.Null(card.MaxRateHz);

        settings.Devices.LightingMaxRates[card.Id] = 10;
        var capped = Assert.Single(OpenRgbZoneSupport.BuildCards(new[] { Gpu() }, settings, isInit: true).Devices);
        Assert.Equal(10, capped.MaxRateHz);
    }

    [Fact]
    public void Cap_round_trips_through_the_settings_file()
    {
        var path = Path.Combine(_dir, "settings.json");
        using (var store = new JsonConfigStore(path))
        {
            store.Update(s => s.Devices.LightingMaxRates["dev"] = 5);
            store.FlushNow();
        }
        var loaded = new JsonConfigStore(path).Load();
        Assert.Equal(5, loaded.Devices.LightingMaxRates["dev"]);
    }
}
