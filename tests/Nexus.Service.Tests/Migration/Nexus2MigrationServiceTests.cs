using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Gallery;
using Nexus.Service.Migration;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Nexus2MigrationService end to end through the real /migration/nexus2/preview
/// and /apply routes: preview composition against the shared fixture (via a
/// fake INexus2ConfigReader), and apply orchestration against real
/// PanelDeviceRegistry/GalleryLibrary/PanelBgLibrary instances rooted in a
/// per-test temp dir. The fixture is spec-derived from the Nexus 2 source
/// types (see plan doc); it must be superseded by a capture from a real
/// install before the importer ships beyond dev.
/// </summary>
public sealed class Nexus2MigrationServiceTests : IDisposable
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2");

    private readonly string _tempDir;
    private readonly NexusAppFactory _baseFactory;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Nexus2MigrationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus2-migration-itest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var configText = File.ReadAllText(Path.Combine(FixtureDir, "nexus2-config.json"))
            .Replace("__FIXTURE_DIR__", FixtureDir.Replace("\\", "\\\\"));
        var reader = new Nexus.Service.Tests.Migration.FakeNexus2ConfigReader { ConfigText = configText, ConfigDir = FixtureDir };

        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<INexus2ConfigReader>();
                services.AddSingleton<INexus2ConfigReader>(reader);
                services.RemoveAll<GalleryLibrary>();
                services.AddSingleton(new GalleryLibrary(Path.Combine(_tempDir, "gallery")));
                services.RemoveAll<PanelBgLibrary>();
                services.AddSingleton(new PanelBgLibrary(Path.Combine(_tempDir, "panel-bg")));
            }));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();
    private PanelDeviceRegistry Panels => _factory.Services.GetRequiredService<PanelDeviceRegistry>();

    private Task<HttpResponseMessage> PostApply(IEnumerable<string> categories, bool replaceCustomizedLayout) =>
        _client.PostAsJsonAsync("/migration/nexus2/apply", new { categories, replaceCustomizedLayout });

    [Fact]
    public async Task Preview_ComposesAllSevenCategoriesFromTheFixture()
    {
        var res = await _client.PostAsync("/migration/nexus2/preview", null);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("Profile 1", root.GetProperty("profileName").GetString());

        var byId = new Dictionary<string, JsonElement>();
        foreach (var cat in root.GetProperty("categories").EnumerateArray())
        {
            byId[cat.GetProperty("id").GetString()!] = cat;
        }
        Assert.Equal(7, byId.Count);

        Assert.True(byId["appearance"].GetProperty("available").GetBoolean());
        Assert.Equal("#242324", byId["appearance"].GetProperty("accentColor").GetString());
        Assert.Equal("particles", byId["appearance"].GetProperty("background").GetString());

        Assert.True(byId["y70Layout"].GetProperty("available").GetBoolean());
        Assert.Equal(9, byId["y70Layout"].GetProperty("widgets").GetInt32());
        Assert.Equal(6, byId["y70Layout"].GetProperty("mappedWidgets").GetInt32());

        Assert.True(byId["q60Face"].GetProperty("available").GetBoolean());
        Assert.Equal("clock", byId["q60Face"].GetProperty("face").GetString());
        Assert.Equal(1, byId["q60Face"].GetProperty("stashedFaces").GetInt32());

        Assert.True(byId["wallpapers"].GetProperty("available").GetBoolean());
        Assert.Equal(1, byId["wallpapers"].GetProperty("count").GetInt32());

        Assert.True(byId["gallerySources"].GetProperty("available").GetBoolean());
        Assert.Equal(2, byId["gallerySources"].GetProperty("count").GetInt32());
        Assert.Equal(1, byId["gallerySources"].GetProperty("missing").GetInt32());

        Assert.Equal(DisplayOrientations.PortraitFlipped, byId["rotation"].GetProperty("value").GetString());
        Assert.Equal("de", byId["language"].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Apply_PatchesTheExistingY70RecordInsteadOfAllocatingANewOne()
    {
        var existing = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });
        var countBefore = Panels.List().Count;

        var res = await PostApply(new[] { "y70Layout" }, replaceCustomizedLayout: false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("applied", doc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());

        Assert.Equal(countBefore, Panels.List().Count);
        Assert.NotNull(Panels.Get(existing.Id)!.Layout);
    }

    [Fact]
    public async Task Apply_Y70Layout_NeedsConfirmWhenAlreadyCustomizedThenAppliesWithTheReplaceFlag()
    {
        var existing = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });
        Panels.Patch(existing.Id, new PanelDevicePatch { Layout = new PanelLayoutDto { LayoutSchemaVersion = 2, Surface = "y70" } });

        var blocked = await PostApply(new[] { "y70Layout" }, replaceCustomizedLayout: false);
        using var blockedDoc = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        var blockedResult = blockedDoc.RootElement.GetProperty("results")[0];
        Assert.Equal("needsConfirm", blockedResult.GetProperty("status").GetString());
        Assert.Equal("layout-customized", blockedResult.GetProperty("detail").GetString());

        var forced = await PostApply(new[] { "y70Layout" }, replaceCustomizedLayout: true);
        using var forcedDoc = JsonDocument.Parse(await forced.Content.ReadAsStringAsync());
        Assert.Equal("applied", forcedDoc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Apply_Q60Face_SetsTheActiveWidgetAndStashesTheNonActivePage()
    {
        var q60 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Q60 });

        var res = await PostApply(new[] { "q60Face" }, replaceCustomizedLayout: false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("applied", doc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());

        var updated = Panels.Get(q60.Id)!;
        Assert.Equal("clock", updated.Layout!.Pages[0].Widgets[0].Type);
        Assert.NotNull(updated.Layout.SingleWidgetConfigs);
        Assert.True(updated.Layout.SingleWidgetConfigs!.ContainsKey("monitoring"));
        Assert.Equal("#1236ff", updated.AccentColor);
        Assert.False(updated.AccentSyncWithDesktop);
    }

    [Fact]
    public async Task Apply_Q60Face_NeedsConfirmWhenAlreadyCustomizedThenAppliesWithTheReplaceFlag()
    {
        var q60 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Q60 });
        Panels.Patch(q60.Id, new PanelDevicePatch { Layout = new PanelLayoutDto { LayoutSchemaVersion = 2, Surface = "q60" } });

        var blocked = await PostApply(new[] { "q60Face" }, replaceCustomizedLayout: false);
        using var blockedDoc = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        var blockedResult = blockedDoc.RootElement.GetProperty("results")[0];
        Assert.Equal("needsConfirm", blockedResult.GetProperty("status").GetString());
        Assert.Equal("layout-customized", blockedResult.GetProperty("detail").GetString());

        var forced = await PostApply(new[] { "q60Face" }, replaceCustomizedLayout: true);
        using var forcedDoc = JsonDocument.Parse(await forced.Content.ReadAsStringAsync());
        Assert.Equal("applied", forcedDoc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Apply_Rotation_SetsQSeriesOrientationFromTheStoreRoot()
    {
        var res = await PostApply(new[] { "rotation" }, false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("applied", doc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(DisplayOrientations.PortraitFlipped, Store.Load().QSeries.Orientation);
    }

    [Fact]
    public async Task Apply_Language_SetsThemeLanguage()
    {
        var res = await PostApply(new[] { "language" }, false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("applied", doc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("de", Store.Load().Theme.Language);
    }

    [Fact]
    public async Task Apply_SetsTheCompletedFlagOnceAtLeastOneCategoryApplies()
    {
        Assert.False(Store.Load().Nexus2MigrationCompleted);
        await PostApply(new[] { "language" }, false);
        Assert.True(Store.Load().Nexus2MigrationCompleted);
    }

    [Fact]
    public async Task Apply_SkipsCategoriesWithNoMatchingDeviceRecord()
    {
        var res = await PostApply(new[] { "appearance", "q60Face" }, false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal("skipped", results[0].GetProperty("status").GetString());
        Assert.Equal("no-y70-record", results[0].GetProperty("detail").GetString());
        Assert.Equal("skipped", results[1].GetProperty("status").GetString());
        Assert.Equal("no-q60-record", results[1].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Apply_GallerySources_AddsOnlyTheFilesThatStillExist()
    {
        var res = await PostApply(new[] { "gallerySources" }, false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("applied", doc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());

        var sources = _factory.Services.GetRequiredService<GalleryLibrary>().ListSources();
        Assert.Equal(2, sources.Count);
    }

    [Fact]
    public async Task Apply_Wallpaper_StagesAndCommitsThroughPanelBgImporter()
    {
        if (Nexus.Service.Platform.FfmpegResolver.Path is null)
        {
            return;
        }

        var q60 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Q60 });
        var res = await PostApply(new[] { "wallpapers" }, false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("applied", doc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());

        var updated = Panels.Get(q60.Id)!;
        Assert.Equal("media", updated.BackgroundMode);
        Assert.False(string.IsNullOrEmpty(updated.BackgroundMediaId));
    }

    // 2-frame 8x8 gif with a transparent background, copied from
    // PanelBgTransparencyTests: the bundled ffmpeg has no lavfi, so test media is
    // authored as literal base64 rather than generated. The two copies only need
    // to stay transparent, not identical.
    private const string TransparentGifBase64 =
        "R0lGODlhCAAIAIEAAP8A/yjIUAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQJCgAAACwAAAAACAAIAAAIGwADCAwAoCCAgQQNIjR4cCDDhQodRhT4UGLBgAAh+QQJCgAAACwEAAAABAAIAIH/AP8oyFAAAAAAAAAIDAADCBxIsKDBgwQDAgA7";

    /// <summary>
    /// A transparent source bakes to media.gif, and the record has to say so:
    /// without the alpha flag the panel picks &lt;video&gt; for a gif and renders
    /// nothing.
    /// </summary>
    [Nexus.Service.Tests.FfmpegFact]
    public async Task Apply_Wallpaper_CarriesTheBakedAlphaFlagOntoTheRecord()
    {
        // The checked-in fixture's wallpaper is opaque and is pinned byte-for-byte
        // against SimulatedNexus2.ConfigJson, so point the reader at a copy.
        var configDir = Path.Combine(_tempDir, "nexus2-alpha");
        var userMedia = Path.Combine(configDir, "q60", "web", "user-media");
        Directory.CreateDirectory(userMedia);
        File.WriteAllBytes(Path.Combine(userMedia, "alpha.gif"), Convert.FromBase64String(TransparentGifBase64));

        var reader = (Nexus.Service.Tests.Migration.FakeNexus2ConfigReader)
            _factory.Services.GetRequiredService<INexus2ConfigReader>();
        reader.ConfigText = reader.ConfigText!.Replace("sunset.jpg", "alpha.gif");
        reader.ConfigDir = configDir;

        var q60 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Q60 });
        var res = await PostApply(new[] { "wallpapers" }, false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("applied", doc.RootElement.GetProperty("results")[0].GetProperty("status").GetString());

        var updated = Panels.Get(q60.Id)!;
        Assert.Equal("animated", updated.BackgroundMediaType);
        Assert.True(updated.BackgroundMediaAlpha);
    }

    private PanelBgLibrary BgLibrary => _factory.Services.GetRequiredService<PanelBgLibrary>();

    /// <summary>Points the reader at a throwaway AppData tree (Roaming\HYTE Nexus beside
    /// Local\Programs\HYTE Nexus, as on a real install) with the Y70 bg set; returns the AppData dir.</summary>
    private string UseNexus2Tree(string bg)
    {
        var appData = Path.Combine(_tempDir, "profile", "AppData");
        var configDir = Path.Combine(appData, "Roaming", "HYTE Nexus");
        Directory.CreateDirectory(configDir);
        var reader = (Nexus.Service.Tests.Migration.FakeNexus2ConfigReader)
            _factory.Services.GetRequiredService<INexus2ConfigReader>();
        reader.ConfigText = reader.ConfigText!.Replace("\"bg\": \"\"", $"\"bg\": {JsonSerializer.Serialize(bg)}");
        reader.ConfigDir = configDir;
        return appData;
    }

    private async Task<JsonElement> ApplyAppearance()
    {
        var res = await PostApply(new[] { "appearance" }, false);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("results")[0].Clone();
    }

    [Nexus.Service.Tests.FfmpegFact]
    public async Task Apply_Appearance_CopiesTheCustomVideoUnconvertedAndShowsItOnThePanel()
    {
        var source = Path.Combine(_tempDir, "custom-Y70-bg-1.mp4");
        Directory.CreateDirectory(_tempDir);
        File.Copy(Path.Combine(FixtureDir, "clip-landscape.mp4"), source);
        UseNexus2Tree(source);
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });

        Assert.Equal("applied", (await ApplyAppearance()).GetProperty("status").GetString());

        var updated = Panels.Get(y70.Id)!;
        Assert.Equal("#242324", updated.AccentColor);
        Assert.False(updated.AccentSyncWithDesktop);
        Assert.Equal("theme", updated.Backdrop);
        Assert.Equal("media", updated.BackgroundMode);
        Assert.Equal("animated", updated.BackgroundMediaType);
        var item = BgLibrary.GetItem(y70.Id, updated.BackgroundMediaId!)!;
        Assert.Equal(".mp4", item.MediaExt);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(BgLibrary.GetMediaPath(y70.Id, item.Id, ".mp4")));
        Assert.True(File.Exists(source));
    }

    [Nexus.Service.Tests.FfmpegFact]
    public async Task Apply_Appearance_ExtractsThePortraitParticlesVideoFromAppAsar()
    {
        var appData = UseNexus2Tree("");
        var portrait = File.ReadAllBytes(Path.Combine(FixtureDir, "clip-portrait.mp4"));
        Nexus.Service.Tests.Migration.AsarTestWriter.Write(
            Path.Combine(appData, "Local", "Programs", "HYTE Nexus", "resources", "app.asar"), "assets",
            new[] { ("particles-desktop.mp4", File.ReadAllBytes(Path.Combine(FixtureDir, "clip-landscape.mp4"))), ("particles-y70.mp4", portrait) });
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });

        Assert.Equal("applied", (await ApplyAppearance()).GetProperty("status").GetString());

        var updated = Panels.Get(y70.Id)!;
        var item = BgLibrary.GetItem(y70.Id, updated.BackgroundMediaId!)!;
        Assert.Equal("particles-y70.mp4", item.Name);
        Assert.Equal(portrait, File.ReadAllBytes(BgLibrary.GetMediaPath(y70.Id, item.Id, ".mp4")));
    }

    [Nexus.Service.Tests.FfmpegFact]
    public async Task Apply_Appearance_RunTwiceReusesTheImportedBackground()
    {
        var source = Path.Combine(_tempDir, "custom-Y70-bg-1.mp4");
        Directory.CreateDirectory(_tempDir);
        File.Copy(Path.Combine(FixtureDir, "clip-landscape.mp4"), source);
        UseNexus2Tree(source);
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });

        await ApplyAppearance();
        var firstId = Panels.Get(y70.Id)!.BackgroundMediaId;
        await ApplyAppearance();

        Assert.Single(BgLibrary.ListItems(y70.Id));
        Assert.Equal(firstId, Panels.Get(y70.Id)!.BackgroundMediaId);
    }

    [Nexus.Service.Tests.FfmpegFact]
    public async Task Apply_Appearance_AReplacedFileWithTheSameNameImportsAgain()
    {
        var source = Path.Combine(_tempDir, "custom-Y70-bg-1.mp4");
        Directory.CreateDirectory(_tempDir);
        File.Copy(Path.Combine(FixtureDir, "clip-landscape.mp4"), source);
        UseNexus2Tree(source);
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });

        await ApplyAppearance();
        var firstId = Panels.Get(y70.Id)!.BackgroundMediaId;
        File.Copy(Path.Combine(FixtureDir, "clip-portrait.mp4"), source, overwrite: true);
        File.AppendAllText(source, "x");
        await ApplyAppearance();

        Assert.Equal(2, BgLibrary.ListItems(y70.Id).Count);
        Assert.NotEqual(firstId, Panels.Get(y70.Id)!.BackgroundMediaId);
    }

    [Nexus.Service.Tests.FfmpegFact]
    public async Task Apply_Appearance_DrawsAGradientIntoAStillAndReusesIt()
    {
        UseNexus2Tree("linear-gradient(181deg, rgba(255, 255, 0, 1) 0%, rgba(0, 188, 212, 1) 50%, rgba(238, 130, 238, 1) 100%)");
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70, CssWidth = 40, CssHeight = 140 });

        Assert.Equal("applied", (await ApplyAppearance()).GetProperty("status").GetString());
        var updated = Panels.Get(y70.Id)!;
        Assert.Equal("theme", updated.Backdrop);
        Assert.Equal("media", updated.BackgroundMode);
        Assert.Equal("static", updated.BackgroundMediaType);
        var item = BgLibrary.GetItem(y70.Id, updated.BackgroundMediaId!)!;
        Assert.StartsWith("nexus2-gradient-", item.Name, StringComparison.Ordinal);
        Assert.Equal((40, 140), (item.Width, item.Height));

        await ApplyAppearance();
        Assert.Single(BgLibrary.ListItems(y70.Id));
    }

    [Fact]
    public async Task Apply_Appearance_TransparentBackgroundMakesTheY70SeeThrough()
    {
        var reader = (Nexus.Service.Tests.Migration.FakeNexus2ConfigReader)
            _factory.Services.GetRequiredService<INexus2ConfigReader>();
        reader.ConfigText = reader.ConfigText!.Replace("\"transparent\": false", "\"transparent\": true");
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });

        Assert.Equal("applied", (await ApplyAppearance()).GetProperty("status").GetString());
        Assert.Equal("desktop", Panels.Get(y70.Id)!.Backdrop);
    }

    [Fact]
    public async Task Apply_Appearance_WithoutTheParticlesVideoStillAppliesTheAccent()
    {
        var y70 = Panels.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 });

        var result = await ApplyAppearance();
        Assert.Equal("applied", result.GetProperty("status").GetString());
        Assert.Equal("background-not-imported", result.GetProperty("detail").GetString());

        var updated = Panels.Get(y70.Id)!;
        Assert.Equal("#242324", updated.AccentColor);
        Assert.False(updated.AccentSyncWithDesktop);
        Assert.Equal(0.7, updated.WidgetOpacity);
        Assert.Null(updated.Backdrop);
        Assert.Null(updated.BackgroundMediaId);
    }
}
