using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Drives the real /onboarding routes through Program.cs. LocalhostOnly +
/// token-gated, so requests set a loopback RemoteIpAddress (as the
/// auth-middleware tests do). Effect is asserted via the shared IConfigStore
/// rather than the response body. Split into two classes (read-only vs the
/// one mutating test) because IClassFixture shares a single NexusAppFactory
/// across every test method in a class, and xUnit does not order them.
/// </summary>
public sealed class OnboardingIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingIntegrationTests(NexusAppFactory factory) => _factory = factory;

    private string Token => _factory.Services.GetRequiredService<TokenService>().Token;

    private async Task<int> Send(string method, string path, bool withToken = true)
    {
        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (withToken)
            {
                c.Request.Headers.Authorization = "Bearer " + Token;
            }
        });
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Status_is_reachable_on_loopback_with_token_and_defaults_incomplete()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().FeaturesOnboardingCompleted);
        Assert.False(store.Load().LightingOnboardingCompleted);
        Assert.Equal(StatusCodes.Status200OK, await Send("GET", "/onboarding"));
    }

    [Fact]
    public async Task Status_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("GET", "/onboarding", withToken: false));

    [Fact]
    public async Task Complete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/complete", withToken: false));

    [Fact]
    public async Task FeaturesComplete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/features-complete", withToken: false));

    [Fact]
    public async Task LightingComplete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/lighting-complete", withToken: false));

    [Fact]
    public async Task PanelSwipe_status_is_reachable_and_defaults_incomplete()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.False(store.Load().PanelSwipeOnboardingCompleted);
        Assert.Equal(StatusCodes.Status200OK, await Send("GET", "/onboarding/panel-swipe"));
    }

    [Fact]
    public async Task PanelSwipe_status_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("GET", "/onboarding/panel-swipe", withToken: false));

    [Fact]
    public async Task PanelSwipeComplete_requires_a_token()
        => Assert.Equal(StatusCodes.Status401Unauthorized, await Send("POST", "/onboarding/panel-swipe/complete", withToken: false));
}

public sealed class OnboardingPanelSwipeCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingPanelSwipeCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task PanelSwipeComplete_sets_only_the_panel_swipe_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/panel-swipe/complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().PanelSwipeOnboardingCompleted);
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().FeaturesOnboardingCompleted);
        Assert.False(store.Load().LightingOnboardingCompleted);
    }
}

public sealed class OnboardingCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Complete_sets_the_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().OnboardingCompleted);
    }
}

public sealed class OnboardingLightingCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingLightingCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task LightingComplete_sets_only_the_lighting_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/lighting-complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().LightingOnboardingCompleted);
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().FeaturesOnboardingCompleted);
    }
}

public sealed class OnboardingFeaturesCompleteIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingFeaturesCompleteIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task FeaturesComplete_sets_only_the_features_flag()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/features-complete";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(store.Load().FeaturesOnboardingCompleted);
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().LightingOnboardingCompleted);
    }
}

public sealed class OnboardingResetIntegrationTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public OnboardingResetIntegrationTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Reset_clears_completed_features_and_lighting_flags()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        store.Update(s =>
        {
            s.OnboardingCompleted = true;
            s.FeaturesOnboardingCompleted = true;
            s.LightingOnboardingCompleted = true;
            s.PanelSwipeOnboardingCompleted = true;
        });

        var ctx = await _factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/onboarding/reset";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers.Authorization = "Bearer " + token;
        });

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.False(store.Load().OnboardingCompleted);
        Assert.False(store.Load().FeaturesOnboardingCompleted);
        Assert.False(store.Load().LightingOnboardingCompleted);
        Assert.False(store.Load().PanelSwipeOnboardingCompleted);
    }
}
