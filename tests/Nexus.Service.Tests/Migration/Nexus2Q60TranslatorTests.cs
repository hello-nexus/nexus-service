using System;
using System.IO;
using System.Text.Json;
using Nexus.Service.Migration;
using Xunit;

namespace Nexus.Service.Tests.Migration;

/// <summary>Pure unit tests for Nexus2Q60Translator: active/stash face
/// selection and wallpaper resolution against the shared fixture. The
/// fixture is spec-derived from the Nexus 2 source types (see plan doc) and
/// must be superseded by a capture from a real install before the importer
/// ships beyond dev.</summary>
public sealed class Nexus2Q60TranslatorTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2");

    // JsonElement.Clone() detaches from the parsing JsonDocument, so each
    // call here is safe to use without keeping the document itself alive.
    private static JsonElement LoadFixtureQ60()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "nexus2-config.json")));
        var profile = doc.RootElement.GetProperty("profiles")[0];
        return profile.GetProperty("q60").GetProperty("software").Clone();
    }

    [Fact]
    public void TranslateFace_ActivePageBecomesTheLiveWidget()
    {
        var software = LoadFixtureQ60();
        var face = Nexus2Q60Translator.TranslateFace(software);

        Assert.True(face.Available);
        Assert.Equal("clock", face.ActiveWidgetType);
        Assert.Equal("analog", face.ActiveConfig!["design"].GetString());
        // theme.accentColor "18, 54, 255" -> hex.
        Assert.Equal("#1236ff", face.AccentHex);
    }

    [Fact]
    public void TranslateFace_NonActivePageIsStashedByType()
    {
        var software = LoadFixtureQ60();
        var face = Nexus2Q60Translator.TranslateFace(software);

        Assert.Single(face.StashedConfigs);
        Assert.True(face.StashedConfigs.ContainsKey("monitoring"));
        var config = face.StashedConfigs["monitoring"];
        Assert.Equal(2, config["slotCount"].GetInt32());
        // slot1: device cpu, sensorId "cpu-load-main" -> summary/cpu-usage; design WaterLevel -> waterLevel.
        Assert.Equal("summary/cpu-usage", config["slot0_sensor"].GetString());
        Assert.Equal("waterLevel", config["slot0_design"].GetString());
        // slot2: device gpu, sensorId "gpu-load-main" -> summary/gpu-usage; design Catapillar -> caterpillar.
        Assert.Equal("summary/gpu-usage", config["slot1_sensor"].GetString());
        Assert.Equal("caterpillar", config["slot1_design"].GetString());
    }

    [Fact]
    public void TranslateWallpaper_ResolvesAnExistingGallerySourceFile()
    {
        var software = LoadFixtureQ60();
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(software, FixtureDir, File.Exists);

        Assert.True(wallpaper.Available);
        Assert.Equal("sunset.jpg", wallpaper.FileName);
        Assert.True(File.Exists(wallpaper.AbsolutePath));
    }

    [Fact]
    public void TranslateWallpaper_MissingFileIsUnavailable()
    {
        using var doc = JsonDocument.Parse("""
        { "background": { "gallerySource": "does-not-exist.jpg" } }
        """);
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(doc.RootElement, FixtureDir, File.Exists);
        Assert.False(wallpaper.Available);
    }

    [Fact]
    public void TranslateWallpaper_EmptyGallerySourceIsUnavailable()
    {
        using var doc = JsonDocument.Parse("""
        { "background": { "gallerySource": "" } }
        """);
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(doc.RootElement, FixtureDir, File.Exists);
        Assert.False(wallpaper.Available);
    }

    [Fact]
    public void TranslateWallpaper_ReadsThePlaylistModeAndItsIntervalInSeconds()
    {
        using var doc = JsonDocument.Parse("""
        { "background": { "gallerySource": "", "playlistMode": true, "playlistInterval": 5000 } }
        """);
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(doc.RootElement, FixtureDir, File.Exists);
        Assert.True(wallpaper.Playlist);
        Assert.Equal(5, wallpaper.PlaylistIntervalSec);
    }

    [Fact]
    public void TranslateWallpaper_AStockPresetPlaysFromBgs()
    {
        using var doc = JsonDocument.Parse("""
        { "background": { "type": "media-background", "gallerySource": "cyber" } }
        """);
        var stock = Path.Combine(FixtureDir, "q60", "web", "bgs", "cyber.webm");
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(doc.RootElement, FixtureDir, p => p == stock);
        Assert.True(wallpaper.Available);
        Assert.Equal(stock, wallpaper.AbsolutePath);
        Assert.Equal("cyber.webm", wallpaper.FileName);
    }

    [Fact]
    public void TranslateWallpaper_AStockNameUnderALightingBackgroundIsUnavailable()
    {
        using var doc = JsonDocument.Parse("""
        { "background": { "type": "lighting-background", "gallerySource": "cyber" } }
        """);
        var onlyStockExists = (string p) => p.EndsWith(".webm", StringComparison.Ordinal);
        Assert.False(Nexus2Q60Translator.TranslateWallpaper(doc.RootElement, FixtureDir, onlyStockExists).Available);
    }

    [Theory]
    [InlineData("analog", "analog")]
    [InlineData("default", "digital")]
    public void TranslateFace_MapsSupportedClockDesigns(string n2Design, string expected)
    {
        using var doc = JsonDocument.Parse($$"""
        {
          "activePageId": "p1",
          "pages": [{ "id": "p1", "front": { "id": "p1", "type": "clock", "timeFormat": "12", "design": "{{n2Design}}", "bounce": true,
            "theme": { "textColor": "0,0,0", "accentColor": "0,0,0", "opacity": 1, "overlayOpacity": 0, "overlayColor": "0,0,0" } } }]
        }
        """);
        var face = Nexus2Q60Translator.TranslateFace(doc.RootElement);
        Assert.Equal(expected, face.ActiveConfig!["design"].GetString());
    }

    [Fact]
    public void TranslateFace_GradientClockFallsBackToWidgetDefault()
    {
        using var doc = JsonDocument.Parse("""
        {
          "activePageId": "p1",
          "pages": [{ "id": "p1", "front": { "id": "p1", "type": "clock", "timeFormat": "12", "design": "gradient", "bounce": true,
            "theme": { "textColor": "0,0,0", "accentColor": "0,0,0", "opacity": 1, "overlayOpacity": 0, "overlayColor": "0,0,0" } } }]
        }
        """);
        var face = Nexus2Q60Translator.TranslateFace(doc.RootElement);
        Assert.False(face.ActiveConfig!.ContainsKey("design"));
    }

    [Fact]
    public void TranslateFace_BlankFrontHasNoActiveWidget()
    {
        using var doc = JsonDocument.Parse("""
        {
          "activePageId": "p1",
          "pages": [{ "id": "p1", "front": { "id": "p1", "type": "blank",
            "theme": { "textColor": "0,0,0", "accentColor": "0,0,0", "opacity": 1, "overlayOpacity": 0, "overlayColor": "0,0,0" } } }]
        }
        """);
        var face = Nexus2Q60Translator.TranslateFace(doc.RootElement);
        Assert.True(face.Available);
        Assert.Null(face.ActiveWidgetType);
    }
}
