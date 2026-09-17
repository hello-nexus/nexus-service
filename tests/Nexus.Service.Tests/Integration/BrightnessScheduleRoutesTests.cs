using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class BrightnessScheduleRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public BrightnessScheduleRoutesTests(StubDeviceHostFactory factory)
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
    public async Task Get_is_off_with_the_default_curve_and_carries_the_defaults()
    {
        var res = await _client.GetAsync("/lighting/brightness-schedule");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(12, doc.RootElement.GetProperty("points").GetArrayLength());
        Assert.Equal(12, doc.RootElement.GetProperty("defaults").GetArrayLength());
        Assert.Equal(100, doc.RootElement.GetProperty("points")[6].GetProperty("brightness").GetInt32());
    }

    [Fact]
    public async Task Post_persists_sorted_and_clamped_points()
    {
        var res = await _client.PostAsync("/lighting/brightness-schedule", Json("""
            {"enabled":true,"points":[{"hour":24,"brightness":140},{"hour":-1,"brightness":-5},{"hour":12,"brightness":80}]}
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var saved = Store.Load().Lighting.BrightnessSchedule;
        Assert.True(saved.Enabled);
        Assert.Equal(new[] { 0, 12, 23 }, saved.Points.Select(p => p.Hour));
        Assert.Equal(new[] { 0, 80, 100 }, saved.Points.Select(p => p.Brightness));
    }

    [Fact]
    public async Task Post_keeps_one_point_per_hour_the_last_sent_winning()
    {
        var res = await _client.PostAsync("/lighting/brightness-schedule", Json("""
            {"enabled":true,"points":[{"hour":12,"brightness":50},{"hour":0,"brightness":10},{"hour":12,"brightness":80}]}
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var saved = Store.Load().Lighting.BrightnessSchedule;
        Assert.Equal(new[] { 0, 12 }, saved.Points.Select(p => p.Hour));
        Assert.Equal(80, saved.Points[1].Brightness);
    }

    [Fact]
    public async Task Get_survives_a_null_schedule_in_settings()
    {
        Store.Update(s => s.Lighting.BrightnessSchedule = new() { Enabled = true, Points = null! });

        var res = await _client.GetAsync("/lighting/brightness-schedule");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("points").GetArrayLength());

        Store.Update(s => s.Lighting.BrightnessSchedule = null!);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/lighting/brightness-schedule")).StatusCode);
    }

    [Fact]
    public async Task Post_refuses_fewer_than_two_points()
    {
        var res = await _client.PostAsync("/lighting/brightness-schedule", Json("""
            {"enabled":true,"points":[{"hour":12,"brightness":80}]}
            """));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(Store.Load().Lighting.BrightnessSchedule.Enabled);
    }

    [Fact]
    public async Task Schedule_is_not_captured_into_the_active_preset()
    {
        var create = await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"P"}"""));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        await _client.PostAsync("/lighting/brightness-schedule", Json("""
            {"enabled":true,"points":[{"hour":0,"brightness":10},{"hour":12,"brightness":90}]}
            """));
        await _client.PostAsync("/lighting/global-brightness", Json("""{"value":0.5}"""));

        var look = Store.Load().Lighting.LayoutPresets.Single().Look!;
        Assert.Equal(0.5f, look.GlobalBrightness);
        Assert.True(Store.Load().Lighting.BrightnessSchedule.Enabled);
    }
}
