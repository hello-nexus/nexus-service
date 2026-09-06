using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// POST /system/audio/play - the touch deck widget's Play Audio press. Unlike
/// /system/pick-path (desktop-only, opens a dialog on the host), this must be
/// reachable from a paired panel session, matching every other deck-action
/// route in SystemRoutes.cs.
/// </summary>
public sealed class SystemAudioPlayRouteTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    public SystemAudioPlayRouteTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(_ => { });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private HttpClient PanelClient() => TestPhoneSession.CreateClient(_factory);

    // A missing path is a safe AudioFilePlayer no-op (see AudioFilePlayerTests),
    // so this exercises the route/DI plumbing without touching real audio.
    [Fact]
    public async Task MissingPath_StillReturnsOk()
    {
        var res = await DesktopClient().PostAsJsonAsync("/system/audio/play", new PlayAudioBody { Path = "/does/not/exist.wav", Volume = 50 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task PanelSession_Is403()
    {
        // The path is caller-named, so the route is desktop-token only; a panel
        // deck's playAudio key goes through POST /panel/deck/dispatch instead.
        var res = await PanelClient().PostAsJsonAsync("/system/audio/play", new PlayAudioBody { Path = "" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task NoToken_Is401()
    {
        var anon = _factory.CreateClient();
        var res = await anon.PostAsJsonAsync("/system/audio/play", new PlayAudioBody { Path = "" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
