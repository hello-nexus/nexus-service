using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

[Collection("SunShift")]
public sealed class ScheduleFollowRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public ScheduleFollowRoutesTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private static JsonElement Find(JsonElement root, string key)
    {
        foreach (var d in root.GetProperty("devices").EnumerateArray())
        {
            if (d.GetProperty("key").GetString() == key)
            {
                return d;
            }
        }
        throw new Xunit.Sdk.XunitException($"no device {key}");
    }

    [Fact]
    public async Task Get_lists_the_first_party_devices_not_following_by_default()
    {
        var res = await _client.GetAsync("/lighting/brightness-schedule/devices");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var key in new[] { "y70", "corsair-lcd", "nzxt-kraken-lcd" })
        {
            var device = Find(doc.RootElement, key);
            Assert.False(device.GetProperty("follows").GetBoolean());
            Assert.Equal(100, device.GetProperty("levelPercent").GetInt32());
        }
    }

    [Fact]
    public async Task Post_opts_a_device_in_and_out_and_get_reflects_it()
    {
        var on = await _client.PostAsync("/lighting/brightness-schedule/devices", Json("""{"key":"y70","follows":true}"""));
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        using (var doc = JsonDocument.Parse(await on.Content.ReadAsStringAsync()))
        {
            Assert.Equal("y70", doc.RootElement.GetProperty("key").GetString());
            Assert.True(doc.RootElement.GetProperty("follows").GetBoolean());
        }
        Assert.Contains("y70", Store.Load().Devices.ScheduleFollowing);

        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule/devices")).Content.ReadAsStringAsync()))
        {
            Assert.True(Find(doc.RootElement, "y70").GetProperty("follows").GetBoolean());
        }

        await _client.PostAsync("/lighting/brightness-schedule/devices", Json("""{"key":"y70","follows":false}"""));
        Assert.DoesNotContain("y70", Store.Load().Devices.ScheduleFollowing);
    }

    [Theory]
    [InlineData("panel-lcd:abc")]
    [InlineData("slv3-aio:AABBCCDDEEFF")]
    [InlineData("not-a-device")]
    public async Task Post_refuses_a_key_the_service_does_not_know(string key)
    {
        var response = await _client.PostAsync("/lighting/brightness-schedule/devices", Json($$"""{"key":"{{key}}","follows":true}"""));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(key, Store.Load().Devices.ScheduleFollowing);
    }

    [Theory]
    [InlineData("""{"key":"","follows":false}""")]
    [InlineData("""{"follows":false}""")]
    public async Task Post_without_a_key_is_refused(string body)
    {
        var res = await _client.PostAsync("/lighting/brightness-schedule/devices", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Applied_percent_is_the_stored_slider_times_the_level()
    {
        Store.Update(s => s.Y70.Brightness = 80);

        using var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule/devices")).Content.ReadAsStringAsync());

        Assert.Equal(80, Find(doc.RootElement, "y70").GetProperty("appliedPercent").GetInt32());
    }

    [Fact]
    public async Task Schedule_post_without_lightingFollows_keeps_it_and_get_reports_true_by_default()
    {
        await _client.PostAsync("/lighting/brightness-schedule", Json("""{"enabled":true,"points":[{"hour":0,"brightness":50},{"hour":12,"brightness":50}]}"""));
        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule")).Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("lightingFollows").GetBoolean());
        }

        await _client.PostAsync("/lighting/brightness-schedule", Json("""{"enabled":true,"lightingFollows":false,"points":[{"hour":0,"brightness":50},{"hour":12,"brightness":50}]}"""));
        await _client.PostAsync("/lighting/brightness-schedule", Json("""{"enabled":true,"points":[{"hour":0,"brightness":50},{"hour":12,"brightness":50}]}"""));
        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule")).Content.ReadAsStringAsync()))
        {
            Assert.False(doc.RootElement.GetProperty("lightingFollows").GetBoolean());
        }
    }

    [Fact]
    public async Task Cap_percent_is_null_while_the_schedule_is_off_and_the_level_when_it_is_on()
    {
        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule/devices")).Content.ReadAsStringAsync()))
        {
            Assert.False(Find(doc.RootElement, "y70").TryGetProperty("capPercent", out var cap) && cap.ValueKind != JsonValueKind.Null);
        }

        Store.Update(s => s.Lighting.BrightnessSchedule = new() { Enabled = true, SunShift = false, Points = new() { new() { Hour = 0, Brightness = 100 }, new() { Hour = 12, Brightness = 100 } } });
        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule/devices")).Content.ReadAsStringAsync()))
        {
            Assert.False(Find(doc.RootElement, "y70").TryGetProperty("capPercent", out var notFollowing) && notFollowing.ValueKind != JsonValueKind.Null);
        }

        Store.Update(s => s.Devices.ScheduleFollowing.Add("y70"));
        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule/devices")).Content.ReadAsStringAsync()))
        {
            Assert.Equal(100, Find(doc.RootElement, "y70").GetProperty("capPercent").GetInt32());
        }
    }

    [Fact]
    public async Task Opting_in_reports_the_curve_level_as_the_cap_in_the_answer_and_the_next_get()
    {
        Store.Update(s =>
        {
            s.Y70.Brightness = 100;
            s.Lighting.BrightnessSchedule = new() { Enabled = true, SunShift = false, Points = new() { new() { Hour = 0, Brightness = 59 }, new() { Hour = 12, Brightness = 59 } } };
        });

        var answer = await _client.PostAsync("/lighting/brightness-schedule/devices", Json("""{"key":"y70","follows":true}"""));
        using (var doc = JsonDocument.Parse(await answer.Content.ReadAsStringAsync()))
        {
            Assert.Equal(59, doc.RootElement.GetProperty("capPercent").GetInt32());
            Assert.Equal(59, doc.RootElement.GetProperty("appliedPercent").GetInt32());
        }

        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule/devices")).Content.ReadAsStringAsync()))
        {
            var y70 = Find(doc.RootElement, "y70");
            Assert.Equal(59, y70.GetProperty("capPercent").GetInt32());
            Assert.Equal(59, y70.GetProperty("levelPercent").GetInt32());
        }
    }
}
