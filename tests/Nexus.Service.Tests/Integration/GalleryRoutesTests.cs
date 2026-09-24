using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Gallery;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Panel;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Gallery routes through the real pipeline: source CRUD + items/file reads,
/// the stubbed native-picker route, exclusion round-trips, and - critically
/// - the auth tiers: item reads are panel-reachable, while pick and source mutations
/// (host-filesystem surface) must reject a paired panel session.
/// </summary>
public sealed class GalleryRoutesTests : IDisposable
{
    // Canonical 67-byte 1x1 transparent PNG (real bytes, not just a valid
    // extension, so file-serving assertions compare meaningful content).
    private static readonly byte[] TinyPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    };

    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly string _tempDir;
    private readonly string _photosDir;
    private readonly StubPicker _picker = new();

    public GalleryRoutesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-gallery-itest-" + Guid.NewGuid().ToString("N")[..8]);
        _photosDir = Path.Combine(_tempDir, "photos");
        Directory.CreateDirectory(_photosDir);

        _baseFactory = new NexusAppFactory();
        var galleryRoot = Path.Combine(_tempDir, "gallery");
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<GalleryLibrary>();
                services.AddSingleton(new GalleryLibrary(galleryRoot));
                // The real picker opens an OS dialog; tests stub it and only
                // exercise the route plumbing + auth tier.
                services.RemoveAll<IFileDialogPicker>();
                services.AddSingleton<IFileDialogPicker>(_picker);
            }));
    }

    /// <summary>Records the mode it was invoked with so a test can assert the
    /// gallery route maps <c>{folder}</c> to the right <see cref="FileDialogPickMode"/>.</summary>
    private sealed class StubPicker : IFileDialogPicker
    {
        public static readonly List<string> StubPaths = new() { "/stub/a.png", "/stub/b.png" };

        public FileDialogPickMode? LastMode { get; private set; }

        public Task<FileDialogPickResult> PickAsync(FileDialogPickMode mode, CancellationToken ct)
        {
            LastMode = mode;
            return Task.FromResult(new FileDialogPickResult { Paths = StubPaths });
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    /// <summary>Mint a real paired-phone session and return a client bearing it.</summary>
    private HttpClient PanelClient()
    {
        var pairing = _factory.Services.GetRequiredService<PanelPhonePairingService>();
        pairing.CreatePairQr();
        var pairToken = pairing.GetOutstandingPairTokens()[0].Token;
        var claim = pairing.ClaimCore(pairToken, "Test Phone", "TestUA", "192.168.1.50", "itest-device",
            overRelay: false, claimedOverHttps: true);
        Assert.True(claim.Ok, claim.Error);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claim.SessionToken);
        return client;
    }

    private string WriteImage(string name)
    {
        var path = Path.Combine(_photosDir, name);
        File.WriteAllBytes(path, TinyPng);
        return path;
    }

    private string CopyClip(string name)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gallery", "clip.mp4");
        Assert.True(File.Exists(fixture), $"missing test fixture {fixture}");
        var path = Path.Combine(_photosDir, name);
        File.Copy(fixture, path, overwrite: true);
        return path;
    }

    private static async Task<T?> ReadAs<T>(HttpResponseMessage res, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => JsonSerializer.Deserialize(await res.Content.ReadAsStringAsync(), info);

    // ── Source CRUD + items + file (desktop tier) ────────────────────────────

    [Fact]
    public async Task SourceCrud_Items_And_File_RoundTrip()
    {
        var client = DesktopClient();
        WriteImage("a.png");
        WriteImage("b.jpg");

        // Add the folder.
        var add = await client.PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var added = await ReadAs(add, AppJsonContext.Default.GallerySourceMutationResponse);
        Assert.False(added!.Error);

        // Listed.
        var sources = await ReadAs(await client.GetAsync("/gallery/sources"), AppJsonContext.Default.GallerySourcesResponse);
        Assert.Single(sources!.Sources);

        // Items flattened, name-sorted.
        var items = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);
        Assert.Equal(new[] { "a.png", "b.jpg" }, items!.Items.Select(i => i.Name).ToArray());

        // Original bytes served with the right content type.
        var file = await client.GetAsync($"/gallery/items/{items.Items[0].Id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/png", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TinyPng, await file.Content.ReadAsByteArrayAsync());

        // Delete the source → items empty.
        var del = await client.DeleteAsync($"/gallery/sources/{added.Source!.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var after = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);
        Assert.Empty(after!.Items);
    }

    [Fact]
    public async Task Video_IsEnumeratedAsVideo_AndServedWithRanges()
    {
        var client = DesktopClient();
        var clipBytes = await File.ReadAllBytesAsync(CopyClip("clip.mp4"));
        WriteImage("a.png");
        await client.PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });

        var items = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);
        var clip = Assert.Single(items!.Items, i => i.Name == "clip.mp4");
        Assert.Equal(GalleryItemKinds.Video, clip.Kind);
        Assert.Equal(GalleryItemKinds.Image, Assert.Single(items.Items, i => i.Name == "a.png").Kind);

        // The clip itself: untouched bytes, video content type, and the panel's
        // <video> can seek/loop with byte ranges.
        var full = await client.GetAsync($"/gallery/items/{clip.Id}/file");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("video/mp4", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal("bytes", full.Headers.AcceptRanges.Single());
        Assert.Equal(clipBytes, await full.Content.ReadAsByteArrayAsync());

        using var rangeReq = new HttpRequestMessage(HttpMethod.Get, $"/gallery/items/{clip.Id}/file");
        rangeReq.Headers.Range = new RangeHeaderValue(0, 15);
        var partial = await client.SendAsync(rangeReq);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(clipBytes.Take(16).ToArray(), await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Video_Still_IsAPosterJpeg_OrNotFound_NeverTheClip()
    {
        var client = DesktopClient();
        CopyClip("clip.mp4");
        await client.PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });
        var items = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);

        var still = await client.GetAsync($"/gallery/items/{items!.Items[0].Id}/file?w=320");

        // With ffmpeg the first frame comes back as a JPEG; without it the
        // route must 404 rather than hand the whole clip to an <img>.
        if (FfmpegResolver.Path is null)
        {
            Assert.Equal(HttpStatusCode.NotFound, still.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, still.StatusCode);
            Assert.Equal("image/jpeg", still.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task AddSource_RelativePath_IsRejected()
    {
        var res = await DesktopClient().PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = "photos/a.png", Kind = GallerySourceKinds.File });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task File_UnknownId_Is404()
    {
        var res = await DesktopClient().GetAsync("/gallery/items/deadbeefdeadbeef/file");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── Native picker route ──────────────────────────────────────────────────

    [Fact]
    public async Task Pick_ReturnsStubbedPaths_ForDesktopClient()
    {
        var res = await DesktopClient().PostAsJsonAsync("/gallery/pick", new GalleryPickBody { Folder = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var parsed = await ReadAs(res, AppJsonContext.Default.GalleryPickResponse);
        Assert.False(parsed!.Error);
        Assert.Equal(StubPicker.StubPaths, parsed.Paths);
        // Unchanged gallery behavior: files (Folder=false) still request the
        // media-filter multiselect mode, not the deck Browse any-file mode.
        Assert.Equal(FileDialogPickMode.MediaMultiSelect, _picker.LastMode);
    }

    [Fact]
    public async Task Pick_Folder_UsesFolderMode()
    {
        var res = await DesktopClient().PostAsJsonAsync("/gallery/pick", new GalleryPickBody { Folder = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(FileDialogPickMode.Folder, _picker.LastMode);
    }

    // ── Exclusions ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Exclude_HidesItem_Restore_BringsItBack()
    {
        var client = DesktopClient();
        WriteImage("a.png");
        WriteImage("b.jpg");
        var add = await client.PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });
        var source = (await ReadAs(add, AppJsonContext.Default.GallerySourceMutationResponse))!.Source!;
        var items = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);

        var exclude = await client.PostAsJsonAsync($"/gallery/sources/{source.Id}/exclude",
            new GalleryExcludeBody { ItemId = items!.Items[0].Id });
        Assert.Equal(HttpStatusCode.OK, exclude.StatusCode);

        var after = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);
        Assert.Single(after!.Items);
        // The exclusion count rides the sources response for the UI badge.
        var sources = await ReadAs(await client.GetAsync("/gallery/sources"), AppJsonContext.Default.GallerySourcesResponse);
        Assert.Single(sources!.Sources.Single().Excluded);

        var restore = await client.PostAsJsonAsync($"/gallery/sources/{source.Id}/restore", new { });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restored = await ReadAs(await client.GetAsync("/gallery/items"), AppJsonContext.Default.GalleryItemsResponse);
        Assert.Equal(2, restored!.Items.Count);
    }

    // ── Auth tiers ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PanelSession_CanReadItems_And_File()
    {
        WriteImage("a.png");
        await DesktopClient().PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });

        var panel = PanelClient();
        var items = await panel.GetAsync("/gallery/items");
        Assert.Equal(HttpStatusCode.OK, items.StatusCode);

        var parsed = await ReadAs(items, AppJsonContext.Default.GalleryItemsResponse);
        var file = await panel.GetAsync($"/gallery/items/{parsed!.Items[0].Id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
    }

    [Fact]
    public async Task PanelSession_CannotPick_OrMutateSources()
    {
        var panel = PanelClient();

        var pick = await panel.PostAsJsonAsync("/gallery/pick", new GalleryPickBody());
        Assert.Equal(HttpStatusCode.Forbidden, pick.StatusCode);

        var add = await panel.PostAsJsonAsync("/gallery/sources",
            new AddGallerySourceBody { Path = _photosDir, Kind = GallerySourceKinds.Folder });
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);

        var sources = await panel.GetAsync("/gallery/sources");
        Assert.Equal(HttpStatusCode.Forbidden, sources.StatusCode);

        var del = await panel.DeleteAsync("/gallery/sources/whatever");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);

        var exclude = await panel.PostAsJsonAsync("/gallery/sources/whatever/exclude",
            new GalleryExcludeBody { ItemId = "deadbeefdeadbeef" });
        Assert.Equal(HttpStatusCode.Forbidden, exclude.StatusCode);

        var restore = await panel.PostAsJsonAsync("/gallery/sources/whatever/restore", new { });
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);
    }

    [Fact]
    public async Task NoToken_Is401_Everywhere()
    {
        var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/gallery/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/gallery/pick", new GalleryPickBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/gallery/sources")).StatusCode);
    }
}
