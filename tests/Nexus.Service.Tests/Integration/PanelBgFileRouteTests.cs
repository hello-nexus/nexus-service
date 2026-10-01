using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The animated-background file route through the real pipeline. A Chromium
/// &lt;video&gt; cannot resume a body it did not finish, so a served mp4 has to
/// advertise byte ranges and honour them; Results.File does neither unless
/// enableRangeProcessing is passed, and its default is false.
///
/// The media is written straight into the library layout rather than baked:
/// the route only checks the file exists, and ffmpeg is not what is under test.
/// </summary>
public sealed class PanelBgFileRouteTests : IDisposable
{
    private const string DeviceId = "test-panel";
    private const string AssetId = "test-asset";

    // Distinct bytes so a ranged slice is checkable by value, not just length.
    private static readonly byte[] MediaBytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public PanelBgFileRouteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-bgfile-itest-" + Guid.NewGuid().ToString("N")[..8]);
        var library = new PanelBgLibrary(Path.Combine(_tempDir, "panel-bg"));

        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<PanelBgLibrary>();
                services.AddSingleton(library);
            }));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);

        Directory.CreateDirectory(library.GetItemDir(DeviceId, AssetId));
        File.WriteAllBytes(library.GetMediaPath(DeviceId, AssetId, ".mp4"), MediaBytes);
        library.SaveMeta(DeviceId, new PanelBgItem
        {
            Id = AssetId,
            Name = "clip.mp4",
            Type = "animated",
            Width = 720,
            Height = 1280,
        });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private const string FileUrl = $"/panel/devices/{DeviceId}/background-media/{AssetId}/file";

    [Fact]
    public async Task Full_get_advertises_byte_ranges()
    {
        var res = await _client.GetAsync(FileUrl);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("bytes", res.Headers.AcceptRanges);
        Assert.Equal("video/mp4", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(MediaBytes, await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Ranged_get_returns_partial_content()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, FileUrl);
        req.Headers.Range = new RangeHeaderValue(8, 15);

        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(MediaBytes[8..16], await res.Content.ReadAsByteArrayAsync());
        var contentRange = res.Content.Headers.ContentRange;
        Assert.NotNull(contentRange);
        Assert.Equal(8, contentRange!.From);
        Assert.Equal(15, contentRange.To);
        Assert.Equal(MediaBytes.Length, contentRange.Length);
    }

    [Fact]
    public async Task Open_ended_range_serves_the_tail()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, FileUrl);
        req.Headers.Range = new RangeHeaderValue(60, null);

        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(MediaBytes[60..], await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_unconverted_copy_is_served_under_its_own_extension_and_type()
    {
        var library = _factory.Services.GetRequiredService<PanelBgLibrary>();
        const string webmId = "webm-asset";
        Directory.CreateDirectory(library.GetItemDir(DeviceId, webmId));
        File.WriteAllBytes(library.GetMediaPath(DeviceId, webmId, ".webm"), MediaBytes);
        library.SaveMeta(DeviceId, new PanelBgItem { Id = webmId, Name = "custom.webm", Type = "animated", MediaExt = ".webm" });

        var res = await _client.GetAsync($"/panel/devices/{DeviceId}/background-media/{webmId}/file");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("video/webm", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(MediaBytes, await res.Content.ReadAsByteArrayAsync());
    }
}
