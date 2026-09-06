using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.LianLiWireless;

public sealed class Slv3LcdScreenSettingsPersistenceTests
{
    [Fact]
    public void Screens_dictionary_round_trips_through_the_source_gen_context()
    {
        var settings = new NexusSettings
        {
            Devices = new DevicesSettings
            {
                LianLiWireless = new LianLiWirelessSettings
                {
                    Screens = new Dictionary<string, LianLiWirelessScreenSettings>
                    {
                        ["AAAA111122223333"] = new()
                        {
                            ContentType = "gif",
                            MediaId = "media-1",
                            Brightness = 80,
                            Rotation = 2,
                            Order = 1,
                        },
                        ["BBBB444455556666"] = new()
                        {
                            ContentType = "animation",
                            SensorSource = "fanRpm",
                            SensorStyle = "bar",
                            ClockFace = "analogMinimal",
                            AnimationId = "spin",
                            ColorA = "#112233",
                            ColorB = "#445566",
                            TempUnit = "f",
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var loaded = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);

        Assert.NotNull(loaded);
        Assert.True(loaded.Devices.LianLiWireless.Screens.TryGetValue("AAAA111122223333", out var screen));
        Assert.Equal("gif", screen.ContentType);
        Assert.Equal("media-1", screen.MediaId);
        Assert.Equal((byte)80, screen.Brightness);
        Assert.Equal((byte)2, screen.Rotation);
        Assert.Equal(1, screen.Order);

        Assert.True(loaded.Devices.LianLiWireless.Screens.TryGetValue("BBBB444455556666", out var second));
        Assert.Equal("animation", second.ContentType);
        Assert.Equal("fanRpm", second.SensorSource);
        Assert.Equal("bar", second.SensorStyle);
        Assert.Equal("analogMinimal", second.ClockFace);
        Assert.Equal("spin", second.AnimationId);
        Assert.Equal("#112233", second.ColorA);
        Assert.Equal("#445566", second.ColorB);
        Assert.Equal("f", second.TempUnit);
    }

    [Fact]
    public void Missing_screens_key_defaults_to_an_empty_dictionary()
    {
        const string json = """
        {
          "schemaVersion": 7,
          "devices": {
            "lianLiWireless": {
              "stopConflictingApps": true
            }
          }
        }
        """;

        var loaded = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Devices.LianLiWireless.Screens);
    }

    [Fact]
    public void New_screen_settings_default_to_off_and_full_brightness()
    {
        var screen = new LianLiWirelessScreenSettings();

        Assert.Equal("off", screen.ContentType);
        Assert.Null(screen.MediaId);
        Assert.Equal((byte)100, screen.Brightness);
        Assert.Equal((byte)0, screen.Rotation);
        Assert.Null(screen.SensorSource);
        Assert.Null(screen.SensorStyle);
        Assert.Null(screen.ClockFace);
        Assert.Null(screen.AnimationId);
        Assert.Null(screen.ColorA);
        Assert.Null(screen.ColorB);
        Assert.Null(screen.TempUnit);
    }
}
