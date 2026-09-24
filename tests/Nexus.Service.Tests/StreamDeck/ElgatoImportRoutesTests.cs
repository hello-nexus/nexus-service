using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck.ElgatoImport;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Integration;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// GET/POST /streamdeck/elgato/profiles[...] against the synthetic fixture
/// store, plus POST /streamdeck/decks/{serial}/presets with a supplied
/// Config (the importer's save path). Reuses LoopbackConnectionFilter from
/// StreamDeckRoutesTests.cs - every /streamdeck/* route is LocalhostOnly.
/// </summary>
public sealed class ElgatoImportRoutesTests : IDisposable
{
    private readonly string _imagesDir = Path.Combine(Path.GetTempPath(), "nexus-elgato-route-images-" + Guid.NewGuid().ToString("N")[..8]);

    private static string FixtureStoreRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ElgatoStore");
    private static string V2OnlyStoreRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ElgatoStoreV2Only");

    public void Dispose()
    {
        try { Directory.Delete(_imagesDir, recursive: true); } catch { /* best effort */ }
    }

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot(string? elgatoRootOverride)
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.AddTransient<IStartupFilter, LoopbackConnectionFilter>();
                s.RemoveAll<DeckImageStore>();
                s.AddSingleton(new DeckImageStore(_imagesDir));
                s.RemoveAll<ElgatoProfileLocator>();
                s.AddSingleton(new ElgatoProfileLocator(elgatoRootOverride));
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Profiles_ReturnsNotFoundStatusWhenTheElgatoDirDoesNotExist()
    {
        var (factory, client) = Boot(Path.Combine(FixtureStoreRoot, "does-not-exist"));
        using (factory)
        {
            var res = await client.GetAsync("/streamdeck/elgato/profiles");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal("notFound", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, doc.RootElement.GetProperty("profiles").GetArrayLength());
        }
    }

    [Fact]
    public async Task Profiles_ReturnsUnsupportedVersionForAV2OnlyStore()
    {
        var (factory, client) = Boot(V2OnlyStoreRoot);
        using (factory)
        {
            var res = await client.GetAsync("/streamdeck/elgato/profiles");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal("unsupportedVersion", doc.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Profiles_ListsTheFixtureBundleWithModelLabelAndCounts()
    {
        var (factory, client) = Boot(FixtureStoreRoot);
        using (factory)
        {
            var res = await client.GetAsync("/streamdeck/elgato/profiles");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            var profiles = root.GetProperty("profiles");
            Assert.Equal(1, profiles.GetArrayLength());
            var p = profiles[0];
            Assert.Equal("testbundle", p.GetProperty("id").GetString());
            Assert.Equal("Test Profile", p.GetProperty("name").GetString());
            Assert.Equal("20GAA9901", p.GetProperty("model").GetString());
            Assert.Equal("Stream Deck", p.GetProperty("modelLabel").GetString());
            Assert.Equal(3, p.GetProperty("pageCount").GetInt32());
            Assert.Equal(29, p.GetProperty("keyCount").GetInt32());
        }
    }

    [Fact]
    public async Task Import_ReturnsNotFoundForAnUnknownId()
    {
        var (factory, client) = Boot(FixtureStoreRoot);
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/elgato/profiles/does-not-exist/import", null);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Import_ReturnsNotFoundWhenTheStoreIsMissing()
    {
        var (factory, client) = Boot(Path.Combine(FixtureStoreRoot, "does-not-exist"));
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/elgato/profiles/testbundle/import", null);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Import_ReturnsConfigAndReportWithoutPersistingAnything()
    {
        var (factory, client) = Boot(FixtureStoreRoot);
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/elgato/profiles/testbundle/import", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var report = root.GetProperty("report");
            Assert.Equal(29, report.GetProperty("totalKeys").GetInt32());
            Assert.Equal(22, report.GetProperty("mappedKeys").GetInt32());
            Assert.Equal(10, report.GetProperty("unmapped").GetArrayLength());
            var config = root.GetProperty("config");
            Assert.Equal(2, config.GetProperty("pages").GetArrayLength());

            // Nothing was saved as a live deck config for any serial.
            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Empty(store.Load().StreamDeck.Decks);
        }
    }

    /// <summary>
    /// Presets are host-wide since schema v18 (deck-modes): the importer's
    /// save path is now POST /deck/presets with a Deck field - there is no
    /// per-serial "live deck" to snapshot instead, since a preset IS the
    /// config (see DeckRoutes.cs).
    /// </summary>
    [Fact]
    public async Task CreateDeckPreset_WithSuppliedDeckConfig_UsesIt()
    {
        var (factory, client) = Boot(FixtureStoreRoot);
        using (factory)
        {
            var importedConfig = """{"pages":[{"slots":[{"label":"Imported","action":{"type":"openUrl","url":"https://example.com"}}]}]}""";
            var res = await client.PostAsync(
                "/deck/presets",
                Json("{\"name\":\"Imported\",\"cols\":5,\"rows\":3,\"deck\":" + importedConfig + "}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("preset").GetProperty("id").GetString();

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var preset = store.Load().StreamDeck.Presets.Find(p => p.Id == id)!;
            Assert.Equal("Imported", preset.Deck.Pages[0].Slots[0].Label);
        }
    }
}
