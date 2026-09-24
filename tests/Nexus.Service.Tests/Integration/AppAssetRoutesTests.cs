using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// One host whose AppRegistry is backed by a temp fixture bundle carrying a
/// <c>.nxpack</c> asset container, its <c>.key</c> dev sidecar, and a
/// disallowed-extension file, all under the same bundle directory. Drives the
/// real <c>GET /apps-api/installed/{id}/asset/{**path}</c> route.
/// </summary>
public sealed class AppAssetFixtureFactory : NexusAppFactory
{
    public const string AppId = "com.test.nxpack";

    public static readonly byte[] NxpackBytes = BuildDeterministicBytes(600, seed: 0x4E);
    public static readonly byte[] KeyBytes = BuildDeterministicBytes(64, seed: 0x4B);
    public static readonly byte[] DisallowedBytes = BuildDeterministicBytes(32, seed: 0x44);

    private readonly string _fixtureRoot;

    public AppAssetFixtureFactory()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "nexus-nxpack-fixture-" + Guid.NewGuid().ToString("N"));
        var bundle = Path.Combine(_fixtureRoot, AppId);
        Directory.CreateDirectory(bundle);

        File.WriteAllText(Path.Combine(bundle, "manifest.json"), $$"""
        {
          "schema": "nexus.app/1",
          "id": "{{AppId}}",
          "name": "Nxpack Fixture",
          "version": "1.0.0",
          "runtime": "sdk",
          "surfaces": ["dashboard"],
          "sizes": ["2x2"],
          "default_size": "2x2"
        }
        """);
        File.WriteAllText(Path.Combine(bundle, "widget.mjs"), "export const mount = () => {};");

        File.WriteAllBytes(Path.Combine(bundle, "avatar.nxpack"), NxpackBytes);
        File.WriteAllBytes(Path.Combine(bundle, "avatar.key"), KeyBytes);
        File.WriteAllBytes(Path.Combine(bundle, "payload.dat"), DisallowedBytes);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppRegistry>();
            services.AddSingleton(new AppRegistry(() => new List<AppInstallPaths.Root>
            {
                new(_fixtureRoot, AppInstallPaths.Source.User),
            }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_fixtureRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static byte[] BuildDeterministicBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((seed + i * 7) % 256);
        }
        return bytes;
    }
}

[Collection("NexusHost")]
public sealed class AppAssetRoutesTests : IClassFixture<AppAssetFixtureFactory>
{
    private readonly AppAssetFixtureFactory _factory;

    public AppAssetRoutesTests(AppAssetFixtureFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task Nxpack_asset_serves_full_bytes_as_octet_stream()
    {
        var client = AuthedClient();

        var res = await client.GetAsync($"/apps-api/installed/{AppAssetFixtureFactory.AppId}/asset/avatar.nxpack");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/octet-stream", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(AppAssetFixtureFactory.NxpackBytes, body);
    }

    [Fact]
    public async Task Key_sidecar_asset_serves_as_octet_stream()
    {
        var client = AuthedClient();

        var res = await client.GetAsync($"/apps-api/installed/{AppAssetFixtureFactory.AppId}/asset/avatar.key");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/octet-stream", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(AppAssetFixtureFactory.KeyBytes, body);
    }

    [Fact]
    public async Task Nxpack_asset_supports_range_requests()
    {
        var client = AuthedClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/apps-api/installed/{AppAssetFixtureFactory.AppId}/asset/avatar.nxpack");
        request.Headers.Range = new RangeHeaderValue(0, 3);

        var res = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.NotNull(res.Content.Headers.ContentRange);
        var body = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(AppAssetFixtureFactory.NxpackBytes[..4], body);
    }

    [Fact]
    public async Task Disallowed_extension_is_not_found_even_though_file_exists_on_disk()
    {
        var client = AuthedClient();

        var res = await client.GetAsync($"/apps-api/installed/{AppAssetFixtureFactory.AppId}/asset/payload.dat");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
