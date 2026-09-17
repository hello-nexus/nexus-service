using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Klipy;
using Nexus.Service.Models.Klipy;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// POST /media/klipy/import over the real pipeline with the catalog swapped at
/// the DI seam, so the failure paths are exercised without a live Klipy call.
/// </summary>
public sealed class KlipyImportRouteTests
{
    private sealed class StubCatalog : IKlipyCatalog
    {
        public bool DownloadResult { get; set; }
        public string? DownloadedSlug { get; private set; }

        public Task<KlipySearchResponse> SearchAsync(string? query, int page, CancellationToken ct) =>
            Task.FromResult(new KlipySearchResponse());

        public Task<byte[]> GetThumbAsync(string slug) => Task.FromResult(Array.Empty<byte>());

        public KlipyResolvedGif? Resolve(string slug) => null;

        public Task<bool> DownloadAsync(string slug, string destPath, CancellationToken ct)
        {
            DownloadedSlug = slug;
            if (DownloadResult)
            {
                File.WriteAllBytes(destPath, new byte[] { 0x47, 0x49, 0x46 });
            }
            return Task.FromResult(DownloadResult);
        }

        public Task TriggerShareAsync(string slug) => Task.CompletedTask;
    }

    private readonly StubCatalog _catalog = new();

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IKlipyCatalog>();
                s.AddSingleton<IKlipyCatalog>(_catalog);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Invalid_slug_is_refused_with_a_message()
    {
        var (factory, client) = Boot();
        using var _ = factory;

        var res = await client.PostAsync("/media/klipy/import", Json("{\"slug\":\"../etc\",\"crop\":\"0,0,1,1\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("invalid slug", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"slug\":\"../etc\",\"crop\":\"0,0,1,1\",\"w\":720,\"h\":1280}", "invalid slug")]
    [InlineData("{\"slug\":\"happy-cat\",\"crop\":\"\",\"w\":720,\"h\":1280}", "crop")]
    [InlineData("{\"slug\":\"happy-cat\",\"crop\":\"0,0,1,1\",\"w\":0,\"h\":1280}", "w and h")]
    [InlineData("{\"slug\":\"happy-cat\",\"crop\":\"0,0,1,1\",\"w\":720,\"h\":99999}", "w and h")]
    public async Task Panel_background_import_refuses_a_malformed_request(string body, string expected)
    {
        var (factory, client) = Boot();
        using var _ = factory;

        var res = await client.PostAsync("/panel/devices/dev1/background-media/klipy/import", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(expected, await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Panel_background_import_reports_a_failed_download()
    {
        var (factory, client) = Boot();
        using var _ = factory;
        _catalog.DownloadResult = false;

        var res = await client.PostAsync(
            "/panel/devices/dev1/background-media/klipy/import",
            Json("{\"slug\":\"happy-cat\",\"crop\":\"0,0,1,1\",\"w\":720,\"h\":1280}"));

        Assert.Equal("happy-cat", _catalog.DownloadedSlug);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Download failed", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_download_answers_with_a_message_not_an_empty_body()
    {
        var (factory, client) = Boot();
        using var _ = factory;
        _catalog.DownloadResult = false;

        var res = await client.PostAsync("/media/klipy/import", Json("{\"slug\":\"happy-cat\",\"crop\":\"0,0,1,1\"}"));
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal("happy-cat", _catalog.DownloadedSlug);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.NotEqual("", body);
    }
}
