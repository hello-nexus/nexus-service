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
        Assert.Equal(6, doc.RootElement.GetProperty("points").GetArrayLength());
        Assert.Equal(6, doc.RootElement.GetProperty("defaults").GetArrayLength());
        Assert.Equal(100, doc.RootElement.GetProperty("points")[2].GetProperty("brightness").GetInt32());
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

    private async Task<JsonDocument> GetDoc()
    {
        var res = await _client.GetAsync("/lighting/brightness-schedule");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private const string TwoPoints = """[{"hour":0,"brightness":10},{"hour":12,"brightness":90}]""";

    [Fact]
    public async Task Get_carries_the_sun_block_with_no_location_until_one_is_known()
    {
        Nexus.Service.Lighting.Sun.SunShift.ResetForTests();

        using var doc = await GetDoc();

        Assert.True(doc.RootElement.GetProperty("sunShift").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("location").ValueKind);
        var sun = doc.RootElement.GetProperty("sun");
        Assert.Equal("no-location", sun.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, sun.GetProperty("sunriseMin").ValueKind);
        Assert.Equal(JsonValueKind.Null, sun.GetProperty("sunsetMin").ValueKind);
        Assert.Equal(JsonValueKind.Null, sun.GetProperty("resolvedLocation").ValueKind);
        Assert.Equal(0, sun.GetProperty("sunPoints").GetArrayLength());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", sun.GetProperty("date").GetString());
    }

    [Fact]
    public async Task Post_with_a_location_returns_the_same_shape_as_get_with_sun_points()
    {
        var res = await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"sunShift\":true,\"points\":" + TwoPoints
            + ",\"location\":{\"lat\":59.9139,\"lon\":10.7522,\"label\":\" Oslo \",\"cc\":\"no\"}}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("Oslo", doc.RootElement.GetProperty("location").GetProperty("label").GetString());
        Assert.Equal("NO", doc.RootElement.GetProperty("location").GetProperty("cc").GetString());
        var sun = doc.RootElement.GetProperty("sun");
        Assert.Equal("ok", sun.GetProperty("status").GetString());
        Assert.Equal(6, sun.GetProperty("sunPoints").GetArrayLength());
        Assert.Equal(59.9139, sun.GetProperty("resolvedLocation").GetProperty("lat").GetDouble());
        Assert.Equal(JsonValueKind.Number, sun.GetProperty("sunriseMin").ValueKind);
        Assert.False(sun.TryGetProperty("morningShiftMin", out _));
    }

    [Fact]
    public async Task Post_absent_keys_keep_and_explicit_null_location_selects_auto()
    {
        await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"sunShift\":false,\"points\":" + TwoPoints
            + ",\"location\":{\"lat\":1,\"lon\":2,\"label\":\"X\",\"cc\":\"\"}}"));

        await _client.PostAsync("/lighting/brightness-schedule", Json("{\"enabled\":true,\"points\":" + TwoPoints + "}"));
        var kept = Store.Load().Lighting.BrightnessSchedule;
        Assert.False(kept.SunShift);
        Assert.Equal("X", kept.SunLocation?.Label);

        await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"sunShift\":true,\"points\":" + TwoPoints + ",\"location\":null}"));
        var auto = Store.Load().Lighting.BrightnessSchedule;
        Assert.True(auto.SunShift);
        Assert.Null(auto.SunLocation);
    }

    [Fact]
    public async Task Auto_location_resolves_in_the_background_once_sun_shift_is_on()
    {
        Nexus.Service.Lighting.Sun.SunShift.ResetForTests();
        _factory.Services.GetRequiredService<Nexus.Service.Tests.Integration.FakeWeatherProvider>().Approximate =
            new() { Lat = 59.9139, Lon = 10.7522, Label = "Oslo, Norway", Cc = "NO" };
        try
        {
            await _client.PostAsync("/lighting/brightness-schedule", Json(
                "{\"enabled\":true,\"sunShift\":true,\"points\":" + TwoPoints + ",\"location\":null}"));

            var status = "";
            for (var i = 0; i < 50 && status != "ok"; i++)
            {
                await Task.Delay(100);
                using var doc = await GetDoc();
                status = doc.RootElement.GetProperty("sun").GetProperty("status").GetString()!;
            }
            Assert.Equal("ok", status);
        }
        finally
        {
            Nexus.Service.Lighting.Sun.SunShift.ResetForTests();
            _factory.Services.GetRequiredService<Nexus.Service.Tests.Integration.FakeWeatherProvider>().Approximate = null;
        }
    }

    [Theory]
    [InlineData("""{"lat":91,"lon":0,"label":"x","cc":""}""")]
    [InlineData("""{"lat":-91,"lon":0,"label":"x","cc":""}""")]
    [InlineData("""{"lat":0,"lon":181,"label":"x","cc":""}""")]
    [InlineData("""{"lat":0,"lon":-181,"label":"x","cc":""}""")]
    public async Task Post_refuses_an_out_of_range_location_and_saves_nothing(string location)
    {
        var res = await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"location\":" + location + "}"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(Store.Load().Lighting.BrightnessSchedule.Enabled);
    }

    [Fact]
    public async Task Post_truncates_a_long_label()
    {
        await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"location\":{\"lat\":1,\"lon\":1,\"label\":\"" + new string('a', 200) + "\",\"cc\":\"\"}}"));

        Assert.Equal(80, Store.Load().Lighting.BrightnessSchedule.SunLocation!.Label.Length);
    }
}
