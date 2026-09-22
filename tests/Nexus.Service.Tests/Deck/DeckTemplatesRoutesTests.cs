using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Integration;
using Nexus.Service.Tests.StreamDeck;
using Xunit;

namespace Nexus.Service.Tests.Deck;

public sealed class FakeShortcuts : IShortcutsProvider
{
    public readonly List<Shortcut> All = new();
    public IReadOnlyList<Shortcut> GetAll() => All;
    public Shortcut? GetById(string targetId) => All.Find(s => s.Id == targetId);
    public byte[] GetIcon(string targetId) => Array.Empty<byte>();
    public bool Launch(string targetId) => true;
    public string ResolveProcessName(string targetId) => GetById(targetId)?.ProcessName ?? "";
}

/// <summary>Class-shared host for GET /deck/templates (installed-app resolution) and the export/import routes, mirroring DeckRoutesHostFactory plus a swappable IShortcutsProvider.</summary>
public sealed class DeckTemplatesHostFactory : NexusAppFactory
{
    public string DeckImagesDir { get; } =
        Path.Combine(Path.GetTempPath(), "nexus-deck-templates-images-" + Guid.NewGuid().ToString("N")[..8]);

    public FakeShortcuts Shortcuts { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, Nexus.Service.Tests.StreamDeck.LoopbackConnectionFilter>();
            s.RemoveAll<DeckImageStore>();
            s.AddSingleton(new DeckImageStore(DeckImagesDir));
            s.RemoveAll<IShortcutsProvider>();
            s.AddSingleton<IShortcutsProvider>(Shortcuts);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(DeckImagesDir, recursive: true); } catch { /* best effort */ }
        }
    }
}

public sealed class DeckTemplatesRoutesTests : IClassFixture<DeckTemplatesHostFactory>
{
    private readonly DeckTemplatesHostFactory _host;

    public DeckTemplatesRoutesTests(DeckTemplatesHostFactory host)
    {
        _host = host;
        _host.ResetSettings();
        _host.Shortcuts.All.Clear();
    }

    private (SharedHost factory, HttpClient client) Boot()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Services.GetRequiredService<TokenService>().Token);
        return (new SharedHost(_host.Services), client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task GetTemplates_NoInstalledApps_ReturnsAllTenUnresolved()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/templates");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var templates = doc.RootElement.GetProperty("templates");
            Assert.Equal(10, templates.GetArrayLength());
            foreach (var t in templates.EnumerateArray())
            {
                Assert.False(t.TryGetProperty("installedAppId", out _));
            }
        }
    }

    [Fact]
    public async Task GetTemplates_InstalledAppMatchesProcessName_ResolvesIt()
    {
        _host.Shortcuts.All.Add(new Shortcut { Id = "s1", Name = "Discord", Path = "C:\\Discord.exe", ProcessName = "Discord" });
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/templates");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var discord = FindTemplate(doc, "discord");
            Assert.Equal("s1", discord.GetProperty("installedAppId").GetString());
            Assert.Equal("Discord", discord.GetProperty("installedAppName").GetString());
            Assert.Equal("Discord", discord.GetProperty("processName").GetString());
        }
    }

    [Fact]
    public async Task GetTemplates_InstalledAppMatchesDisplayNameOnly_ResolvesIt()
    {
        // No ProcessName resolution (UWP-style entry) - falls back to display name.
        _host.Shortcuts.All.Add(new Shortcut { Id = "s2", Name = "Spotify", Path = "", ProcessName = "" });
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/templates");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var spotify = FindTemplate(doc, "spotify");
            Assert.Equal("s2", spotify.GetProperty("installedAppId").GetString());
        }
    }

    [Fact]
    public async Task GetTemplates_VersionedMacBundleName_ResolvesByDisplayNamePrefix()
    {
        _host.Shortcuts.All.Add(new Shortcut { Id = "s3", Name = "Adobe Photoshop 2025", Path = "/Applications/Adobe Photoshop 2025.app", ProcessName = "Adobe Photoshop 2025" });
        _host.Shortcuts.All.Add(new Shortcut { Id = "s4", Name = "zoom.us", Path = "/Applications/zoom.us.app", ProcessName = "zoom.us" });
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/templates");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal("s3", FindTemplate(doc, "photoshop").GetProperty("installedAppId").GetString());
            Assert.Equal("s4", FindTemplate(doc, "zoom").GetProperty("installedAppId").GetString());
        }
    }

    [Fact]
    public async Task GetTemplates_AnotherAppSharingThePrefix_DoesNotResolve()
    {
        _host.Shortcuts.All.Add(new Shortcut { Id = "s5", Name = "Adobe Photoshop Lightroom Classic", Path = "", ProcessName = "Lightroom" });
        _host.Shortcuts.All.Add(new Shortcut { Id = "s6", Name = "ZoomIt", Path = "", ProcessName = "zoomit64" });
        _host.Shortcuts.All.Add(new Shortcut { Id = "s7", Name = "Code::Blocks", Path = "", ProcessName = "codeblocks" });
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/templates");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.False(FindTemplate(doc, "photoshop").TryGetProperty("installedAppId", out _));
            Assert.False(FindTemplate(doc, "zoom").TryGetProperty("installedAppId", out _));
            Assert.False(FindTemplate(doc, "vscode").TryGetProperty("installedAppId", out _));
        }
    }

    [Fact]
    public async Task Import_PackageWithKeysMac_FoldsItForTheHostAndStripsIt()
    {
        const string presetJson = """
{"format":1,"id":"pkg","name":"Pkg","cols":1,"rows":1,"deck":{"pages":[{"slots":[
  {"label":"Mute","action":{"type":"hotkey","keys":"ctrl+shift+m","keysMac":"cmd+shift+m"}}]}]}}
""";
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("preset.json").Open();
            entry.Write(Encoding.UTF8.GetBytes(presetJson));
        }
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets/import?allowPrivileged=1", new ByteArrayContent(zipStream.ToArray())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/zip") },
            });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("keysMac", body);
            using var doc = JsonDocument.Parse(body);
            var keys = doc.RootElement.GetProperty("preset").GetProperty("deck").GetProperty("pages")[0].GetProperty("slots")[0]
                .GetProperty("action").GetProperty("keys").GetString();
            Assert.Equal(OperatingSystem.IsMacOS() ? "cmd+shift+m" : "ctrl+shift+m", keys);
        }
    }

    [Fact]
    public async Task CreateFromTemplate_StoresOneComboPerKey_NeverKeysMac()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets",
                new StringContent("{\"templateId\":\"discord\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("keysMac", body);
            using var doc = JsonDocument.Parse(body);
            var mute = doc.RootElement.GetProperty("preset").GetProperty("deck").GetProperty("pages")[0].GetProperty("slots")[0]
                .GetProperty("action").GetProperty("keys").GetString();
            Assert.Equal(OperatingSystem.IsMacOS() ? "cmd+shift+m" : "ctrl+shift+m", mute);
        }
    }

    [Fact]
    public async Task Export_UnknownId_Returns404()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/presets/not-a-real-id/export");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task ExportThenImport_RoundTrips()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var created = await client.PostAsync("/deck/presets", Json(
                """{"name":"Export Me","cols":2,"rows":2,"deck":{"pages":[{"slots":[{"label":"Hi","action":{"type":"openUrl","url":"https://hellonexus.com"}}]}]}}"""));
            using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = createdDoc.RootElement.GetProperty("preset").GetProperty("id").GetString();

            var exportRes = await client.GetAsync($"/deck/presets/{id}/export");
            Assert.True(exportRes.IsSuccessStatusCode);
            Assert.Equal("application/zip", exportRes.Content.Headers.ContentType?.MediaType);
            var zipBytes = await exportRes.Content.ReadAsByteArrayAsync();

            var deleted = await client.DeleteAsync($"/deck/presets/{id}");
            Assert.True(deleted.IsSuccessStatusCode);

            var importRes = await client.PostAsync("/deck/presets/import", new ByteArrayContent(zipBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/zip") },
            });
            Assert.True(importRes.IsSuccessStatusCode);
            using var importDoc = JsonDocument.Parse(await importRes.Content.ReadAsStringAsync());
            var imported = importDoc.RootElement.GetProperty("preset");
            Assert.Equal("Export Me", imported.GetProperty("name").GetString());
            Assert.NotEqual(id, imported.GetProperty("id").GetString());
            Assert.False(imported.TryGetProperty("templateId", out _));
            Assert.Equal("Hi", imported.GetProperty("deck").GetProperty("pages")[0].GetProperty("slots")[0].GetProperty("label").GetString());
        }
    }

    [Fact]
    public async Task Import_NameCollision_Returns409()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var created = await client.PostAsync("/deck/presets", Json(
                """{"name":"Dup","cols":2,"rows":2,"deck":{"pages":[{"slots":[]}]}}"""));
            using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = createdDoc.RootElement.GetProperty("preset").GetProperty("id").GetString();
            var exportRes = await client.GetAsync($"/deck/presets/{id}/export");
            var zipBytes = await exportRes.Content.ReadAsByteArrayAsync();

            var importRes = await client.PostAsync("/deck/presets/import", new ByteArrayContent(zipBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/zip") },
            });
            Assert.Equal(HttpStatusCode.Conflict, importRes.StatusCode);
        }
    }

    [Fact]
    public async Task Import_PrivilegedWithoutFlag_Returns403_ThenSucceedsWithFlag()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var created = await client.PostAsync("/deck/presets", Json(
                """{"name":"Hotkey Preset","cols":2,"rows":2,"deck":{"pages":[{"slots":[{"action":{"type":"hotkey","keys":"ctrl+shift+m"}}]}]}}"""));
            Assert.True(created.IsSuccessStatusCode);
            using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = createdDoc.RootElement.GetProperty("preset").GetProperty("id").GetString();
            var exportRes = await client.GetAsync($"/deck/presets/{id}/export");
            var zipBytes = await exportRes.Content.ReadAsByteArrayAsync();
            await client.DeleteAsync($"/deck/presets/{id}");

            var blocked = await client.PostAsync("/deck/presets/import", new ByteArrayContent(zipBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/zip") },
            });
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Contains("deck_action_requires_desktop", await blocked.Content.ReadAsStringAsync());

            var allowed = await client.PostAsync("/deck/presets/import?allowPrivileged=1", new ByteArrayContent(zipBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/zip") },
            });
            Assert.True(allowed.IsSuccessStatusCode);
        }
    }

    [Fact]
    public async Task Import_InvalidZip_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets/import", new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/zip") },
            });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    private static JsonElement FindTemplate(JsonDocument doc, string id)
    {
        foreach (var t in doc.RootElement.GetProperty("templates").EnumerateArray())
        {
            if (t.GetProperty("id").GetString() == id)
            {
                return t;
            }
        }
        throw new Xunit.Sdk.XunitException($"template '{id}' not found in response");
    }
}
