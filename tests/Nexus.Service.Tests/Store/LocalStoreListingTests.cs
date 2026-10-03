#if DEV_TOOLS
using System.Text.Json.Nodes;
using Nexus.Service.Store;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Store;

public sealed class LocalStoreListingTests : IDisposable
{
    private const string Id = "com.test.listing";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-localstore-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dir;

    public LocalStoreListingTests()
    {
        _dir = Path.Combine(_root, Id);
        Directory.CreateDirectory(Path.Combine(_dir, "store", "screenshots"));
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), """
        {
          "schema": "nexus.app/1", "id": "com.test.listing", "name": "Listing App", "version": "1.2.0",
          "description": "Base description", "tagline": "A tagline that is far longer\nthan thirty characters",
          "min_nexus_version": "3.0.0", "runtime": "sdk", "surfaces": ["dashboard", "page"],
          "sizes": ["2x2", "4x2"], "page": true, "requires_touch": true,
          "capabilities": { "telemetry": true, "net.fetch": ["example.com"] },
          "locales": { "de": { "name": "Listen-App", "tagline": "Deutsch" }, "pt-BR": { "description": "Descricao" } }
        }
        """);
        File.WriteAllText(Path.Combine(_dir, "widget.mjs"), "export const mount = () => {};");
        File.WriteAllBytes(Path.Combine(_dir, "store", "icon.png"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(_dir, "store", "screenshots", "02-b.png"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(_dir, "store", "screenshots", "01-a.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "store", "screenshots", ".DS_Store"), new byte[] { 0 });
        File.WriteAllText(Path.Combine(_dir, "store", "whats-new.md"), "Base notes\n");
        File.WriteAllText(Path.Combine(_dir, "store", "whats-new.de.md"), "Neu\n");
        File.WriteAllText(Path.Combine(_root, "secret.png"), "secret");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppEntry Entry()
    {
        Assert.True(new AppRegistry(() => new List<AppInstallPaths.Root> { new(_root, AppInstallPaths.Source.User) })
            .TryGet(Id, out var entry));
        return entry;
    }

    [Fact]
    public void Detail_mirrors_the_registered_listing()
    {
        var d = LocalStoreListing.Detail(Entry(), null)!;

        Assert.Equal(Id, d["id"]!.GetValue<string>());
        Assert.Equal("Listing App", d["name"]!.GetValue<string>());
        Assert.Equal("A tagline that is far longer t", d["tagline"]!.GetValue<string>());
        Assert.Equal("Base description", d["description"]!.GetValue<string>());
        Assert.Equal("other", d["category"]!.GetValue<string>());
        Assert.True(d["localPreview"]!.GetValue<bool>());
        Assert.Equal(0, d["rating"]!["count"]!.GetValue<int>());
        Assert.Equal($"/apps-api/store/local-media/{Id}/icon.png", d["iconUrl"]!.GetValue<string>());
        Assert.Equal(
            new[] { $"/apps-api/store/local-media/{Id}/screenshots/01-a.png", $"/apps-api/store/local-media/{Id}/screenshots/02-b.png" },
            d["screenshots"]!.AsArray().Select(n => n!.GetValue<string>()));

        var latest = d["latest"]!;
        Assert.Equal("1.2.0", latest["version"]!.GetValue<string>());
        Assert.Equal("", latest["sha256"]!.GetValue<string>());
        Assert.True(latest["hasWidget"]!.GetValue<bool>());
        Assert.True(latest["hasPage"]!.GetValue<bool>());
        Assert.True(latest["requiresTouch"]!.GetValue<bool>());
        Assert.Equal("3.0.0", latest["minNexusVersion"]!.GetValue<string>());
        Assert.Equal(new[] { "2x2", "4x2" }, latest["sizes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(latest["capabilities"]!["telemetry"]!.GetValue<bool>());
        Assert.Equal("Base notes", latest["notes"]!.GetValue<string>());

        var version = d["versions"]!.AsArray().Single()!;
        Assert.Equal("1.2.0", version["version"]!.GetValue<string>());
        Assert.Equal("Base notes", version["notes"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("de", "Listen-App", "Deutsch", "Base description", "Neu")]
    [InlineData("de-AT", "Listen-App", "Deutsch", "Base description", "Neu")]
    [InlineData("pt-BR", "Listing App", "A tagline that is far longer t", "Descricao", "Base notes")]
    [InlineData("fr", "Listing App", "A tagline that is far longer t", "Base description", "Base notes")]
    [InlineData(null, "Listing App", "A tagline that is far longer t", "Base description", "Base notes")]
    public void Locale_overrides_apply_per_field_with_base_language_fallback(
        string? locale, string name, string tagline, string description, string notes)
    {
        var d = LocalStoreListing.Detail(Entry(), locale)!;
        Assert.Equal(name, d["name"]!.GetValue<string>());
        Assert.Equal(tagline, d["tagline"]!.GetValue<string>());
        Assert.Equal(description, d["description"]!.GetValue<string>());
        Assert.Equal(notes, d["latest"]!["notes"]!.GetValue<string>());
    }

    [Fact]
    public void Translated_notes_are_ignored_without_the_base_notes_file()
    {
        File.Delete(Path.Combine(_dir, "store", "whats-new.md"));
        Assert.Equal("", LocalStoreListing.Detail(Entry(), "de")!["latest"]!["notes"]!.GetValue<string>());
    }

    [Fact]
    public void List_appends_only_installed_apps_with_a_store_dir_that_the_cloud_lacks()
    {
        var entry = Entry();
        var merged = JsonNode.Parse(LocalStoreListing.AppendLocal("""{"apps":[{"id":"cloud.app"}]}""", new[] { entry }, null))!;
        var apps = merged["apps"]!.AsArray();
        Assert.Equal(2, apps.Count);
        Assert.Equal(Id, apps[1]!["id"]!.GetValue<string>());
        Assert.True(apps[1]!["localPreview"]!.GetValue<bool>());

        var already = JsonNode.Parse(LocalStoreListing.AppendLocal($$"""{"apps":[{"id":"{{Id}}"}]}""", new[] { entry }, null))!;
        Assert.Single(already["apps"]!.AsArray());

        Directory.Delete(Path.Combine(_dir, "store"), recursive: true);
        var none = JsonNode.Parse(LocalStoreListing.AppendLocal("""{"apps":[]}""", new[] { entry }, null))!;
        Assert.Empty(none["apps"]!.AsArray());
    }

    [Fact]
    public void Media_serves_store_files_with_an_image_type()
    {
        var media = LocalStoreListing.ReadMedia(Entry(), "screenshots/01-a.png")!.Value;
        Assert.Equal("image/png", media.ContentType);
        Assert.Equal(new byte[] { 1 }, media.Bytes);
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("screenshots/../../secret.png")]
    [InlineData("..%2Fsecret.png")]
    [InlineData("/etc/passwd.png")]
    [InlineData("..\\secret.png")]
    [InlineData("../manifest.json")]
    [InlineData("whats-new.md")]
    [InlineData("screenshots/.DS_Store")]
    [InlineData("missing.png")]
    [InlineData("")]
    [InlineData(null)]
    public void Media_refuses_traversal_non_images_and_missing_files(string? path)
    {
        Assert.Null(LocalStoreListing.ReadMedia(Entry(), path));
    }

    [Fact]
    public void Media_refuses_a_symlink_out_of_the_store_dir()
    {
        var link = Path.Combine(_dir, "store", "link.png");
        try { File.CreateSymbolicLink(link, Path.Combine(_root, "secret.png")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        Assert.Null(LocalStoreListing.ReadMedia(Entry(), "link.png"));
    }
}
#endif
