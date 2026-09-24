using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Migration;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests.Migration;

/// <summary>Pure unit tests for Nexus2Y70Translator: type/size/design/sensor
/// mapping, placement determinism, and the drop list. The fixture is
/// spec-derived from the Nexus 2 source types (see plan doc); it must be
/// superseded by a capture from a real install before the importer ships
/// beyond dev.</summary>
public sealed class Nexus2Y70TranslatorTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2");

    private static JsonElement LoadFixtureY70()
    {
        var text = File.ReadAllText(Path.Combine(FixtureDir, "nexus2-config.json"))
            .Replace("__FIXTURE_DIR__", FixtureDir.Replace("\\", "\\\\"));
        var doc = JsonDocument.Parse(text);
        var profile = doc.RootElement.GetProperty("profiles")[0];
        return profile.GetProperty("widgets").GetProperty("faces").GetProperty("y70");
    }

    [Fact]
    public void TranslateLayout_CountsWidgetsAndDropsUnportableTypes()
    {
        var y70 = LoadFixtureY70();
        var result = TranslateLayout(y70);

        // dock(clock) + page1(clock,performance,gallery,media,snakeGame,weather,aquarium) + page2(whiteboard,discord) = 10
        // discord is unportable: Nexus 3 has no Discord widget, only the
        // Rich Presence setting, which carries none of the widget's config.
        Assert.Equal(10, result.Widgets);
        Assert.Equal(7, result.MappedWidgets);
        // DroppedTypes is first-seen fixture order, not sorted.
        Assert.Equal(new List<string> { "aquarium", "whiteboard", "discord" }, result.DroppedTypes);
        Assert.Equal(2, result.Pages);
    }

    [Fact]
    public void TranslateLayout_MapsWidgetTypesToNexus3Keys()
    {
        var y70 = LoadFixtureY70();
        var result = TranslateLayout(y70);
        var types = new HashSet<string>();
        foreach (var page in result.Layout!.Pages)
        {
            foreach (var w in page.Widgets)
            {
                types.Add(w.Type);
            }
        }
        Assert.Contains("clock", types);
        Assert.Contains("monitoring", types);
        Assert.Contains("gallery", types);
        Assert.Contains("media", types);
        Assert.Contains("snake", types);
        Assert.Contains("weather", types);
        Assert.DoesNotContain("aquarium", types);
        Assert.DoesNotContain("discord", types);
        Assert.DoesNotContain("whiteboard", types);
    }

    [Fact]
    public void TranslateLayout_DockWidgetLandsOnTheFirstPage()
    {
        var y70 = LoadFixtureY70();
        var result = TranslateLayout(y70);
        var firstPage = result.Layout!.Pages[0];
        Assert.Contains(firstPage.Widgets, w => w.Type == "clock" && w.Size == "1x1");
    }

    [Theory]
    [InlineData("1x1", "1x1")]
    [InlineData("2x2", "2x2")]
    [InlineData("4x2", "4x2")]
    [InlineData("4x4", "4x4")]
    [InlineData("4x8", "4x4")]
    public void TranslateLayout_MapsSizes(string n2Size, string expectedN3Size)
    {
        using var doc = JsonDocument.Parse($$"""
        {
          "pages": [{ "id": "p1", "type": "page", "widgets": [
            { "id": "w1", "type": "snakeGame", "size": "{{n2Size}}", "position": 0, "positionHorizontal": 0, "isImmersive": false }
          ]}]
        }
        """);
        var result = TranslateLayout(doc.RootElement);
        Assert.Equal(expectedN3Size, result.Layout!.Pages[0].Widgets[0].Size);
    }

    [Theory]
    [InlineData("flip", "splitflap")]
    [InlineData("analog", "analog")]
    [InlineData("digital", "digital")]
    public void TranslateLayout_MapsClockDesign(string n2Design, string expectedN3Design)
    {
        var widget = ClockWidget(n2Design, "24", false, false, null);
        var config = SingleWidgetConfig(widget);
        Assert.Equal(expectedN3Design, config["design"].GetString());
    }

    [Theory]
    [InlineData("12", "12h")]
    [InlineData("24", "24h")]
    public void TranslateLayout_MapsTimeFormat(string n2Format, string expectedFormat)
    {
        var widget = ClockWidget("digital", n2Format, false, false, null);
        var config = SingleWidgetConfig(widget);
        Assert.Equal(expectedFormat, config["format"].GetString());
    }

    [Fact]
    public void TranslateLayout_PerformanceSlots_ClampCountAndResolveSummarySensors()
    {
        var y70 = LoadFixtureY70();
        var result = TranslateLayout(y70);
        var perf = FindWidget(result, "monitoring");
        var config = perf.Config!;

        // 5 source slots clamp to 4 (nearest of {1,2,4}).
        Assert.Equal(4, config["slotCount"].GetInt32());
        Assert.Equal("quick", config["slot0_device"].GetString());
        Assert.Equal("summary/cpu-usage", config["slot0_sensor"].GetString());
        Assert.False(config.ContainsKey("slot0_design")); // CatDog has no Nexus 3 match
        Assert.Equal("summary/cpu-temp", config["slot1_sensor"].GetString());
        Assert.Equal("waterLevel", config["slot1_design"].GetString());
        Assert.Equal("summary/gpu-usage", config["slot2_sensor"].GetString());
        Assert.Equal("text", config["slot2_design"].GetString());
        // slot4 (name "GPU Memory Used", device gpu) resolves to vram, not gpu-usage.
        Assert.Equal("summary/vram-usage", config["slot3_sensor"].GetString());
        Assert.Equal("caterpillar", config["slot3_design"].GetString());
        // The 5th source slot (memory) never makes it in - clamp dropped it.
        Assert.False(config.ContainsKey("slot4_sensor"));
    }

    [Fact]
    public void TranslateLayout_GalleryConfig_MapsModeAndSnapsInterval()
    {
        var y70 = LoadFixtureY70();
        var result = TranslateLayout(y70);
        var gallery = FindWidget(result, "gallery");
        Assert.Equal("slideshow", gallery.Config!["mode"].GetString());
        // "Every 15 seconds" is already an allowed step.
        Assert.Equal(15, gallery.Config!["interval"].GetInt32());
    }

    [Fact]
    public void TranslateLayout_WeatherConfig_MapsUnitAndLocation()
    {
        var y70 = LoadFixtureY70();
        var result = TranslateLayout(y70);
        var weather = FindWidget(result, "weather");
        Assert.Equal("C", weather.Config!["unit"].GetString());
        var location = weather.Config!["location"];
        Assert.Equal(48.2082, location.GetProperty("lat").GetDouble());
        Assert.Equal(16.3738, location.GetProperty("lon").GetDouble());
        Assert.Equal("Vienna, Austria", location.GetProperty("label").GetString());
        Assert.Equal("", location.GetProperty("cc").GetString());
    }

    [Fact]
    public void TranslateLayout_PlacementIsDeterministicAcrossRuns()
    {
        var y70 = LoadFixtureY70();
        var first = TranslateLayout(y70);
        var second = TranslateLayout(y70);
        Assert.Equal(first.Layout!.Pages.Count, second.Layout!.Pages.Count);
        for (var p = 0; p < first.Layout.Pages.Count; p++)
        {
            for (var w = 0; w < first.Layout.Pages[p].Widgets.Count; w++)
            {
                Assert.Equal(first.Layout.Pages[p].Widgets[w].Col, second.Layout.Pages[p].Widgets[w].Col);
                Assert.Equal(first.Layout.Pages[p].Widgets[w].Row, second.Layout.Pages[p].Widgets[w].Row);
                Assert.Equal(first.Layout.Pages[p].Widgets[w].Type, second.Layout.Pages[p].Widgets[w].Type);
            }
        }
    }

    [Fact]
    public void TranslateLayout_NeverOverlapsWidgetsOnAPage()
    {
        var y70 = LoadFixtureY70();
        var result = TranslateLayout(y70);
        foreach (var page in result.Layout!.Pages)
        {
            for (var i = 0; i < page.Widgets.Count; i++)
            {
                for (var j = i + 1; j < page.Widgets.Count; j++)
                {
                    Assert.False(Overlaps(page.Widgets[i], page.Widgets[j]), $"{page.Widgets[i].Type} overlaps {page.Widgets[j].Type}");
                }
            }
        }
    }

    private static bool Overlaps(Nexus.Service.Models.Panel.PanelWidgetDto a, Nexus.Service.Models.Panel.PanelWidgetDto b)
    {
        var (aCols, aRows) = Y70LayoutPlacement.SpanForSize(a.Size);
        var (bCols, bRows) = Y70LayoutPlacement.SpanForSize(b.Size);
        return a.Col < b.Col + bCols && b.Col < a.Col + aCols &&
            a.Row < b.Row + bRows && b.Row < a.Row + aRows;
    }

    private static Nexus2Y70LayoutResult TranslateLayout(JsonElement y70) => Nexus2Y70Translator.TranslateLayout(y70);

    private static Nexus.Service.Models.Panel.PanelWidgetDto FindWidget(Nexus2Y70LayoutResult result, string type)
    {
        foreach (var page in result.Layout!.Pages)
        {
            foreach (var w in page.Widgets)
            {
                if (w.Type == type)
                {
                    return w;
                }
            }
        }
        Assert.Fail($"widget of type '{type}' not found");
        throw new InvalidOperationException("unreachable");
    }

    private static JsonElement ClockWidget(string design, string timeFormat, bool seconds, bool displayTimezone, string? timezone)
    {
        var tzJson = timezone is null ? "null" : $"\"{timezone}\"";
        using var doc = JsonDocument.Parse($$"""
        {
          "pages": [{ "id": "p1", "type": "page", "widgets": [
            { "id": "w1", "type": "clock", "size": "4x2", "design": "{{design}}", "timeFormat": "{{timeFormat}}",
              "seconds": {{seconds.ToString().ToLowerInvariant()}}, "displayTimezone": {{displayTimezone.ToString().ToLowerInvariant()}},
              "timezone": {{tzJson}}, "position": 0, "positionHorizontal": 0, "isImmersive": false }
          ]}]
        }
        """);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> SingleWidgetConfig(JsonElement y70)
    {
        var result = TranslateLayout(y70);
        return result.Layout!.Pages[0].Widgets[0].Config!;
    }
}
