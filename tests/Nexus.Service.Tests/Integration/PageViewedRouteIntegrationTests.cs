using System.IO;
using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Tests.Integration;

public sealed class PageViewedRouteIntegrationTests : IClassFixture<NexusAppFactory>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "page-viewed-" + Guid.NewGuid().ToString("N"));
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly List<(string Event, object? Page)> _events = new();

    public PageViewedRouteIntegrationTests(NexusAppFactory factory)
    {
        Directory.CreateDirectory(_root);
        var recorder = new Recorder(_events);
        _factory = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ITelemetry>();
            s.AddSingleton<ITelemetry>(recorder);
            s.RemoveAll<AppUsageTracker>();
            s.AddSingleton(new AppUsageTracker(recorder, Path.Combine(_root, "u.json")));
        }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Recorder(List<(string Event, object? Page)> sink) : ITelemetry
    {
        public void Capture(string @event, params (string Key, object? Value)[] properties) =>
            sink.Add((@event, properties.FirstOrDefault(p => p.Key == "page").Value));
        public void Identify(params (string Key, object? Value)[] properties) { }
    }

    private async Task<int> Post(string page, bool withToken = true)
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/telemetry/page-viewed/" + page;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (withToken)
                c.Request.Headers.Authorization = "Bearer " + _factory.Services.GetRequiredService<TokenService>().Token;
        });
        return ctx.Response.StatusCode;
    }

    [Theory]
    [InlineData("Lighting")]
    [InlineData("-lighting")]
    [InlineData("light_ing")]
    [InlineData("light.ing")]
    public async Task Invalid_slugs_return_400_and_emit_nothing(string page)
    {
        Assert.Equal(400, await Post(page));
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_slug_over_forty_characters_is_rejected()
    {
        Assert.Equal(400, await Post(new string('a', 41)));
        Assert.Equal(204, await Post(new string('a', 40)));
    }

    [Fact]
    public async Task First_view_emits_and_repeat_view_returns_204_without_emitting()
    {
        Assert.Equal(204, await Post("lighting"));
        Assert.Equal(204, await Post("lighting"));
        Assert.Equal(204, await Post("cooling-2"));
        Assert.Equal(new object?[] { "lighting", "cooling-2" }, _events.Select(e => e.Page).ToArray());
        Assert.All(_events, e => Assert.Equal(TelemetryEvents.PageViewed, e.Event));
    }

    [Fact]
    public async Task Route_requires_a_token()
    {
        Assert.Equal(401, await Post("lighting", withToken: false));
        Assert.Empty(_events);
    }
}
