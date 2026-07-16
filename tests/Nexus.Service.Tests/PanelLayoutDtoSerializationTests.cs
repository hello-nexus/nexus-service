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
    public void SingleWidgetConfigs_is_null_when_absent_from_legacy_json()
    {
        const string json = """{ "surface": "y70", "pages": [] }""";

        var layout = JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelLayoutDto);

        Assert.NotNull(layout);
        Assert.Null(layout.SingleWidgetConfigs);
    }
}
