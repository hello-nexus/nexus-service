using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>Swaps in throwaway case and preview stores and a fake cloud client so the real data root is never touched.</summary>
public sealed class SystemPreviewAppFactory : NexusAppFactory
{
    public FakeCloudApiClient Api { get; } = new();
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "nexus-preview-itest-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICloudApiClient>();
            services.AddSingleton<ICloudApiClient>(Api);
            services.RemoveAll<MachineCaseStore>();
            services.AddSingleton(new MachineCaseStore(Path.Combine(Dir, "system-case.json")));
            services.RemoveAll<SystemPreviewStore>();
            services.AddSingleton(new SystemPreviewStore(Dir));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, recursive: true); } catch { /* already gone */ }
    }
}

public sealed class SystemPreviewRoutesTests : IClassFixture<SystemPreviewAppFactory>
{
    private static readonly byte[] PngHead = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly SystemPreviewAppFactory _factory;

    public SystemPreviewRoutesTests(SystemPreviewAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _factory.Services.GetRequiredService<MachineCaseStore>().Save("case-a", pending: false);
        foreach (var f in new[] { "system-preview.png", "system-preview.json" })
        {
            try { File.Delete(Path.Combine(_factory.Dir, f)); } catch { /* absent */ }
        }
    }

    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static byte[] Png(int extra = 32)
    {
        var bytes = new byte[PngHead.Length + extra];
        PngHead.CopyTo(bytes, 0);
        new Random(7).NextBytes(bytes.AsSpan(PngHead.Length));
        return bytes;
    }

    private static ByteArrayContent Body(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    [Fact]
    public async Task Both_verbs_require_the_desktop_token()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/system/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/system/preview?caseId=case-a", Body(Png()))).StatusCode);
    }

    [Fact]
    public async Task Get_with_nothing_stored_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Desktop().GetAsync("/system/preview")).StatusCode);
    }

    [Fact]
    public async Task Put_then_get_round_trips_the_bytes_with_etag_and_no_cache()
    {
        var client = Desktop();
        var png = Png();

        var put = await client.PutAsync("/system/preview?caseId=case-a", Body(png));
        var get = await client.GetAsync("/system/preview");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"" + Convert.ToHexStringLower(SHA256.HashData(png)) + "\"", get.Headers.ETag?.Tag);
        Assert.True(get.Headers.CacheControl?.NoCache);
        Assert.True(File.Exists(Path.Combine(_factory.Dir, "system-preview.png")));
        Assert.Contains("case-a", File.ReadAllText(Path.Combine(_factory.Dir, "system-preview.json")));
    }

    [Fact]
    public async Task Get_with_a_matching_if_none_match_is_304()
    {
        var client = Desktop();
        await client.PutAsync("/system/preview?caseId=case-a", Body(Png()));
        var etag = (await client.GetAsync("/system/preview")).Headers.ETag!.Tag;

        var req = new HttpRequestMessage(HttpMethod.Get, "/system/preview");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotModified, res.StatusCode);
    }

    [Fact]
    public async Task Get_after_the_case_changed_is_404_and_a_new_put_for_the_new_case_serves_again()
    {
        var client = Desktop();
        await client.PutAsync("/system/preview?caseId=case-a", Body(Png()));
        _factory.Services.GetRequiredService<MachineCaseStore>().Save("case-b", pending: false);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/system/preview")).StatusCode);

        await client.PutAsync("/system/preview?caseId=case-b", Body(Png(48)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/system/preview")).StatusCode);
    }

    [Fact]
    public async Task Put_without_a_png_signature_is_400_and_stores_nothing()
    {
        var client = Desktop();

        var res = await client.PutAsync("/system/preview?caseId=case-a", Body(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/system/preview")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?caseId=")]
    [InlineData("?caseId=has%20space")]
    [InlineData("?caseId=a%2Fb")]
    public async Task Put_with_a_missing_or_invalid_case_id_is_400(string query)
    {
        var res = await Desktop().PutAsync("/system/preview" + query, Body(Png()));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_over_two_megabytes_is_413()
    {
        var res = await Desktop().PutAsync("/system/preview?caseId=case-a", Body(Png(2 * 1024 * 1024)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task Put_over_two_megabytes_without_content_length_is_413()
    {
        var content = new StreamContent(new MemoryStream(Png(2 * 1024 * 1024)));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var req = new HttpRequestMessage(HttpMethod.Put, "/system/preview?caseId=case-a") { Content = content };
        req.Headers.TransferEncodingChunked = true;

        var res = await Desktop().SendAsync(req);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task A_panel_session_gets_403_on_both_verbs()
    {
        var panel = TestPhoneSession.CreateClient(_factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await panel.GetAsync("/system/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await panel.PutAsync("/system/preview?caseId=case-a", Body(Png()))).StatusCode);
    }

    [Fact]
    public async Task A_png_that_no_longer_matches_its_sidecar_hash_reads_as_404()
    {
        var client = Desktop();
        await client.PutAsync("/system/preview?caseId=case-a", Body(Png()));
        File.WriteAllBytes(Path.Combine(_factory.Dir, "system-preview.png"), Png(40));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/system/preview")).StatusCode);
    }

    [Fact]
    public void Save_returns_false_instead_of_throwing_when_the_disk_write_fails()
    {
        var blocker = Path.Combine(_factory.Dir, "blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        var store = new SystemPreviewStore(Path.Combine(blocker, "sub"));

        Assert.False(store.Save("case-a", Png()));
    }

    [Fact]
    public void Desktop_default_layout_seeds_the_build_widget_beside_lighting()
    {
        var layout = Nexus.Service.Panel.PanelLayoutDefaults.ForSurface("desktop");
        var widgets = layout.Pages[0].Widgets;

        Assert.Contains(widgets, w => w.Type == "build" && w.Size == "2x2" && w.Col == 4 && w.Row == 0);
        Assert.Contains(widgets, w => w.Type == "diagnostics" && w.Size == "2x2" && w.Col == 4 && w.Row == 2);
        Assert.Contains(widgets, w => w.Type == "weather" && w.Size == "2x2" && w.Col == 4 && w.Row == 4);
    }
}
