using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class IdleDimRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public IdleDimRoutesTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private IConfigStore Store =>
        _factory.Services.GetRequiredService<IConfigStore>();

    [Fact]
    public async Task Get_defaults_to_off_screen_off_timeout_and_ten_percent()
    {
        var res = await _client.GetAsync("/lighting/idle-dim");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, root.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(10, root.GetProperty("level").GetInt32());
        var desktop = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(desktop, root.GetProperty("screenOffSupported").GetBoolean());
        if (desktop)
        {
            Assert.True(root.GetProperty("supported").GetBoolean());
        }
        Assert.True(root.TryGetProperty("osScreenOffSeconds", out _));
    }

    [Fact]
    public async Task Get_returns_a_stored_zero_as_is()
    {
        await _client.PostAsync("/lighting/idle-dim", Json("""{"enabled":true,"timeoutSeconds":0,"level":10}"""));

        using var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/idle-dim")).Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task Post_persists_the_setting()
    {
        var res = await _client.PostAsync("/lighting/idle-dim", Json("""{"enabled":true,"timeoutSeconds":300,"level":25}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var saved = Store.Load().Lighting.IdleDim;
        Assert.True(saved.Enabled);
        Assert.Equal(300, saved.TimeoutSeconds);
        Assert.Equal(25, saved.Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(86400)]
    public async Task Post_accepts_zero_and_the_range_bounds(int timeout)
    {
        var res = await _client.PostAsync("/lighting/idle-dim", Json($$"""{"enabled":true,"timeoutSeconds":{{timeout}},"level":10}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(timeout, Store.Load().Lighting.IdleDim.TimeoutSeconds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(59)]
    [InlineData(86401)]
    public async Task Post_refuses_timeouts_outside_zero_or_the_range(int timeout)
    {
        var res = await _client.PostAsync("/lighting/idle-dim", Json($$"""{"enabled":true,"timeoutSeconds":{{timeout}},"level":10}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(Store.Load().Lighting.IdleDim.Enabled);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(140, 100)]
    public async Task Post_clamps_the_level(int sent, int saved)
    {
        var res = await _client.PostAsync("/lighting/idle-dim", Json($$"""{"enabled":true,"timeoutSeconds":0,"level":{{sent}}}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(saved, Store.Load().Lighting.IdleDim.Level);
    }

    [Fact]
    public async Task Idle_dim_is_not_captured_into_the_active_preset()
    {
        var create = await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"P"}"""));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        await _client.PostAsync("/lighting/idle-dim", Json("""{"enabled":true,"timeoutSeconds":300,"level":5}"""));
        await _client.PostAsync("/lighting/global-brightness", Json("""{"value":0.5}"""));

        Assert.True(Store.Load().Lighting.IdleDim.Enabled);
        Assert.Equal(0.5f, Store.Load().Lighting.LayoutPresets.Single().Look!.GlobalBrightness);
    }
}
