using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

[Collection("SunShift")]
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
        var resolver = _factory.Services.GetRequiredService<Nexus.Service.Lighting.Sun.SunLocationResolver>();
        await resolver.StartAsync(default);
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
            using var done = await GetDoc();
            Assert.Equal("Oslo, Norway", done.RootElement.GetProperty("sun").GetProperty("autoLocation").GetProperty("label").GetString());
        }
        finally
        {
            await resolver.StopAsync(default);
            Nexus.Service.Lighting.Sun.SunShift.ResetForTests();
            _factory.Services.GetRequiredService<Nexus.Service.Tests.Integration.FakeWeatherProvider>().Approximate = null;
        }
    }

    [Fact]
    public async Task No_ip_lookup_runs_while_the_schedule_is_off()
    {
        Nexus.Service.Lighting.Sun.SunShift.ResetForTests();
        var weather = _factory.Services.GetRequiredService<Nexus.Service.Tests.Integration.FakeWeatherProvider>();
        weather.Approximate = new() { Lat = 1, Lon = 2, Label = "X", Cc = "" };
        var resolver = _factory.Services.GetRequiredService<Nexus.Service.Lighting.Sun.SunLocationResolver>();
        var baseline = weather.ApproximateCalls;
        var firstPass = resolver.NextPass;
        await resolver.StartAsync(default);
        try
        {
            await firstPass.WaitAsync(TimeSpan.FromSeconds(10));
            await _client.PostAsync("/lighting/brightness-schedule", Json(
                "{\"enabled\":false,\"sunShift\":true,\"points\":" + TwoPoints + ",\"location\":null}"));
            var next = resolver.NextPass;
            resolver.Request(force: true);
            await next.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(baseline, weather.ApproximateCalls);
            Assert.Null(Nexus.Service.Lighting.Sun.SunShift.AutoLocation);
        }
        finally
        {
            await resolver.StopAsync(default);
            weather.Approximate = null;
        }
    }

    [Fact]
    public async Task Get_reports_the_resolved_default_for_an_unchosen_sun_shift()
    {
        Store.Update(s => s.Lighting.BrightnessSchedule = new() { Points = new() { new() { Hour = 0, Brightness = 5 }, new() { Hour = 12, Brightness = 50 } } });

        using var custom = await GetDoc();

        Assert.False(custom.RootElement.GetProperty("sunShift").GetBoolean());
    }

    [Fact]
    public async Task Editing_the_curve_does_not_flip_an_unchosen_sun_shift()
    {
        await _client.PostAsync("/lighting/brightness-schedule", Json("{\"enabled\":true,\"points\":" + TwoPoints + "}"));

        Assert.True(Store.Load().Lighting.BrightnessSchedule.SunShift);
    }

    [Theory]
    [InlineData("""{"lon":0,"label":"x"}""")]
    [InlineData("""{"lat":0,"label":"x"}""")]
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

    [Fact]
    public async Task Get_reports_the_stock_night_and_day_levels()
    {
        using var doc = await GetDoc();

        Assert.Equal(10, doc.RootElement.GetProperty("sunNight").GetInt32());
        Assert.Equal(100, doc.RootElement.GetProperty("sunDay").GetInt32());
    }

    [Fact]
    public async Task Post_sets_the_levels_and_the_sun_points_follow()
    {
        var res = await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"sunNight\":30,\"sunDay\":70,"
            + "\"location\":{\"lat\":59.9139,\"lon\":10.7522,\"label\":\"Oslo\",\"cc\":\"NO\"}}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(30, doc.RootElement.GetProperty("sunNight").GetInt32());
        Assert.Equal(70, doc.RootElement.GetProperty("sunDay").GetInt32());
        var levels = doc.RootElement.GetProperty("sun").GetProperty("sunPoints").EnumerateArray()
            .Select(p => p.GetProperty("brightness").GetDouble()).Order().Distinct().ToArray();
        Assert.Equal(new[] { 30.0, 50.0, 70.0 }, levels);
    }

    [Fact]
    public async Task Post_absent_levels_keep_the_stored_ones_and_a_lone_level_is_checked_against_them()
    {
        await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"sunNight\":30,\"sunDay\":70}"));

        await _client.PostAsync("/lighting/brightness-schedule", Json("{\"enabled\":true,\"points\":" + TwoPoints + "}"));
        var kept = Store.Load().Lighting.BrightnessSchedule;
        Assert.Equal(30, kept.SunNightLevel);
        Assert.Equal(70, kept.SunDayLevel);

        var over = await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"sunNight\":80}"));
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        var under = await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"sunDay\":20}"));
        Assert.Equal(HttpStatusCode.BadRequest, under.StatusCode);
        Assert.Equal(70, Store.Load().Lighting.BrightnessSchedule.SunDayLevel);

        var equal = await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"sunNight\":70}"));
        Assert.Equal(HttpStatusCode.OK, equal.StatusCode);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(10, 101)]
    [InlineData(60, 50)]
    public async Task Post_refuses_levels_out_of_range_or_inverted(int night, int day)
    {
        var res = await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"sunNight\":" + night + ",\"sunDay\":" + day + "}"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(Store.Load().Lighting.BrightnessSchedule.Enabled);
    }

    [Fact]
    public async Task Post_names_the_offending_level_and_saves_the_resolved_pair()
    {
        var bad = await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"sunDay\":150}"));
        Assert.Contains("sunDay", await bad.Content.ReadAsStringAsync());

        await _client.PostAsync("/lighting/brightness-schedule", Json(
            "{\"enabled\":true,\"points\":" + TwoPoints + ",\"sunNight\":40}"));
        var saved = Store.Load().Lighting.BrightnessSchedule;
        Assert.Equal(40, saved.SunNight);
        Assert.Equal(100, saved.SunDay);
    }
}
