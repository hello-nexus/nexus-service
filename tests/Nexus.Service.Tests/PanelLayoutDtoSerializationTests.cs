using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests;

public class PanelLayoutDtoSerializationTests
{
    [Fact]
    public void SingleWidgetConfigs_round_trips_through_source_gen_context()
    {
        var layout = new PanelLayoutDto
        {
            Surface = "q60",
            SingleWidgetConfigs = new Dictionary<string, Dictionary<string, JsonElement>>
            {
                ["clock"] = new Dictionary<string, JsonElement>
                {
                    ["style"] = JsonDocument.Parse("\"digital\"").RootElement,
                },
            },
        };

        var json = JsonSerializer.Serialize(layout, AppJsonContext.Default.PanelLayoutDto);
        Assert.Contains("singleWidgetConfigs", json);

        var roundTripped = JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelLayoutDto);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped.SingleWidgetConfigs);
        Assert.True(roundTripped.SingleWidgetConfigs.TryGetValue("clock", out var clockConfig));
        Assert.True(clockConfig.TryGetValue("style", out var style));
        Assert.Equal("digital", style.GetString());
    }

    [Fact]
    public void ImmersiveOnLoadWidgetId_round_trips_through_source_gen_context()
    {
        var layout = new PanelLayoutDto
        {
            Surface = "y70",
            ImmersiveOnLoadWidgetId = "w-media",
        };

        var json = JsonSerializer.Serialize(layout, AppJsonContext.Default.PanelLayoutDto);
        Assert.Contains("immersiveOnLoadWidgetId", json);

        var roundTripped = JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelLayoutDto);

        Assert.NotNull(roundTripped);
        Assert.Equal("w-media", roundTripped.ImmersiveOnLoadWidgetId);
    }

    [Fact]
    public void ImmersiveOnLoadWidgetId_is_null_when_absent_from_legacy_json()
    {
        const string json = """{ "surface": "y70", "pages": [] }""";

        var layout = JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelLayoutDto);

        Assert.NotNull(layout);
        Assert.Null(layout.ImmersiveOnLoadWidgetId);
    }

    [Fact]
    public void WidgetPlaylist_round_trips_through_app_and_persistence_contexts()
    {
        const string json = """
            { "surface": "q60", "pages": [],
              "widgetPlaylist": { "enabled": true, "interval": 30, "shuffle": true, "types": ["clock", "weather"],
                "order": ["weather", "media", "clock"], "cursor": { "type": "weather", "at": 1790727444271 } } }
            """;

        var fromWire = JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelLayoutDto);
        Assert.NotNull(fromWire?.WidgetPlaylist);

        var settings = new Nexus.Service.Persistence.NexusSettings();
        settings.PanelDevices["q"] = new PanelDeviceRecord { Id = "q", Layout = fromWire };
        var persisted = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var reloaded = JsonSerializer.Deserialize(persisted, PersistenceJsonContext.Default.NexusSettings);

        var playlist = reloaded?.PanelDevices["q"].Layout?.WidgetPlaylist;
        Assert.NotNull(playlist);
        Assert.True(playlist.Enabled);
        Assert.Equal(30, playlist.Interval);
        Assert.True(playlist.Shuffle);
        Assert.Equal(new[] { "clock", "weather" }, playlist.Types);
        Assert.Equal(new[] { "weather", "media", "clock" }, playlist.Order);
        Assert.Equal("weather", playlist.Cursor?.Type);
        Assert.Equal(1790727444271L, playlist.Cursor?.At);

        var echoed = JsonSerializer.Serialize(reloaded!.PanelDevices["q"].Layout!, AppJsonContext.Default.PanelLayoutDto);
        Assert.Contains("\"widgetPlaylist\"", echoed);
        Assert.Contains("\"cursor\"", echoed);
    }

    [Fact]
    public void WidgetPlaylist_is_null_when_absent_from_legacy_json()
    {
        const string json = """{ "surface": "q60", "pages": [] }""";

        var layout = JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelLayoutDto);

        Assert.NotNull(layout);
        Assert.Null(layout.WidgetPlaylist);
    }

    [Fact]
    public void SingleWidgetConfigs_is_null_when_absent_from_legacy_json()
    {
        const string json = """{ "surface": "y70", "pages": [] }""";

        var layout = JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelLayoutDto);

        Assert.NotNull(layout);
        Assert.Null(layout.SingleWidgetConfigs);
    }
}
