using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.IdleDim;
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
    public async Task Activating_a_preset_leaves_idle_dim_alone()
    {
        await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"P"}"""));
        var id = Store.Load().Lighting.LayoutPresets.Single().Id;
        await _client.PostAsync("/lighting/idle-dim", Json("""{"enabled":true,"timeoutSeconds":300,"level":5}"""));
        await _client.PostAsync("/lighting/global-brightness", Json("""{"value":0.5}"""));

        var res = await _client.PostAsync($"/devices/lighting-devices/layout-presets/{id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var idle = Store.Load().Lighting.IdleDim;
        Assert.True(idle.Enabled);
        Assert.Equal(300, idle.TimeoutSeconds);
        Assert.Equal(5, idle.Level);
    }
}

/// <summary>
/// Drives the DI-registered controller into a dim and reads the real static
/// <see cref="MasterBrightness.IdleRamp"/> through the one-argument
/// <c>Effective</c> every frame writer calls. Serialized because that ramp is
/// process-wide and other tests read brightness through it.
/// </summary>
[Collection("IdleRamp")]
public sealed class IdleDimFramePathTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;

    public IdleDimFramePathTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
    }

    private sealed class FakeWatch : IIdleDimWatch
    {
        public void SetInputWatch(int thresholdSeconds) { }
        public void SetDisplayWatch(bool armed) { }
    }

    [Fact]
    public async Task A_dimmed_controller_caps_the_one_argument_Effective_and_release_restores_it()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var controller = _factory.Services.GetRequiredService<IdleDimController>();
        controller.Watch = new FakeWatch();
        controller.Start();
        try
        {
            store.Update(s =>
            {
                s.Lighting.GlobalBrightness = 1f;
                s.Lighting.IdleDim = new() { Enabled = true, TimeoutSeconds = 300, Level = 0 };
            });

            controller.OnInputIdle(true);
            await Task.Delay(400);
            Assert.True(MasterBrightness.Effective(store.Load().Lighting) < 0.9f);

            controller.OnInputIdle(false);
            await Task.Delay(1000);
            Assert.Equal(1f, MasterBrightness.Effective(store.Load().Lighting));
        }
        finally
        {
            controller.Stop();
            controller.Watch = null;
            MasterBrightness.IdleRamp.RampTo(1f, TimeSpan.Zero);
        }
    }
}

[CollectionDefinition("IdleRamp", DisableParallelization = true)]
public sealed class IdleRampCollection;
