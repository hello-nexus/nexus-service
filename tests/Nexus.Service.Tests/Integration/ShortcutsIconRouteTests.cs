using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The /shortcuts/icon route's ETag/304 contract, exercised through the real
/// pipeline against a fake IShortcutsProvider so this runs on every platform -
/// the real Windows extraction/disk-cache pipeline is Windows-only and covered
/// separately by IconDiskCacheTests.
/// </summary>
public sealed class ShortcutsIconRouteTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly FakeShortcutsProvider _shortcuts = new();

    public ShortcutsIconRouteTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IShortcutsProvider>();
                services.AddSingleton<IShortcutsProvider>(_shortcuts);
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private sealed class FakeShortcutsProvider : IShortcutsProvider
    {
        public byte[]? IconBytes = Array.Empty<byte>();

        public IReadOnlyList<Shortcut> GetAll() => Array.Empty<Shortcut>();
        public Shortcut? GetById(string targetId) => null;
        public byte[]? GetIcon(string targetId) => IconBytes;
        public bool Launch(string targetId) => false;
        public string ResolveProcessName(string targetId) => "";
    }

    [Fact]
    public async Task MissingTargetId_Is400()
    {
        var res = await Client().GetAsync("/shortcuts/icon");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task EmptyIcon_Is404()
    {
        _shortcuts.IconBytes = Array.Empty<byte>();

        var res = await Client().GetAsync("/shortcuts/icon?targetId=missing-app");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ExtractionUnavailable_Is503_Not404()
    {
        _shortcuts.IconBytes = null;

        var res = await Client().GetAsync("/shortcuts/icon?targetId=app-1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task IconPresent_Returns200_WithEtagAndCacheControl()
    {
        _shortcuts.IconBytes = new byte[] { 1, 2, 3, 4, 5 };

        var res = await Client().GetAsync("/shortcuts/icon?targetId=app-1");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(_shortcuts.IconBytes, await res.Content.ReadAsByteArrayAsync());
        Assert.NotNull(res.Headers.ETag);
        Assert.NotNull(res.Headers.CacheControl);
        Assert.True(res.Headers.CacheControl!.MustRevalidate);
    }

    [Fact]
    public async Task MatchingIfNoneMatch_Returns304_WithEmptyBody()
    {
        _shortcuts.IconBytes = new byte[] { 9, 9, 9 };
        var client = Client();

        var first = await client.GetAsync("/shortcuts/icon?targetId=app-1");
        var etag = first.Headers.ETag!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/shortcuts/icon?targetId=app-1");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task StaleIfNoneMatch_AfterContentChanges_Returns200_WithNewEtag()
    {
        _shortcuts.IconBytes = new byte[] { 1, 1, 1 };
        var client = Client();

        var first = await client.GetAsync("/shortcuts/icon?targetId=app-1");
        var staleEtag = first.Headers.ETag!;

        _shortcuts.IconBytes = new byte[] { 2, 2, 2, 2 };
        var request = new HttpRequestMessage(HttpMethod.Get, "/shortcuts/icon?targetId=app-1");
        request.Headers.IfNoneMatch.Add(staleEtag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(_shortcuts.IconBytes, await second.Content.ReadAsByteArrayAsync());
        Assert.NotEqual(staleEtag, second.Headers.ETag);
    }
}
