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
    public async Task Get_lists_the_first_party_devices_following_by_default()
    {
        var res = await _client.GetAsync("/lighting/brightness-schedule/devices");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var key in new[] { "y70", "corsair-lcd", "nzxt-kraken-lcd" })
        {
            var device = Find(doc.RootElement, key);
            Assert.True(device.GetProperty("follows").GetBoolean());
            Assert.Equal(100, device.GetProperty("levelPercent").GetInt32());
        }
    }

    [Fact]
    public async Task Post_toggles_a_device_and_get_reflects_it()
    {
        var off = await _client.PostAsync("/lighting/brightness-schedule/devices", Json("""{"key":"y70","follows":false}"""));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        using (var doc = JsonDocument.Parse(await off.Content.ReadAsStringAsync()))
        {
            Assert.Equal("y70", doc.RootElement.GetProperty("key").GetString());
            Assert.False(doc.RootElement.GetProperty("follows").GetBoolean());
        }
        Assert.Contains("y70", Store.Load().Devices.ScheduleUnfollowed);

        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule/devices")).Content.ReadAsStringAsync()))
        {
            Assert.False(Find(doc.RootElement, "y70").GetProperty("follows").GetBoolean());
        }

        await _client.PostAsync("/lighting/brightness-schedule/devices", Json("""{"key":"y70","follows":true}"""));
        Assert.DoesNotContain("y70", Store.Load().Devices.ScheduleUnfollowed);
    }

    [Fact]
    public async Task Post_accepts_an_id_the_service_has_not_seen_and_lists_it_as_unfollowed()
    {
        await _client.PostAsync("/lighting/brightness-schedule/devices", Json("""{"key":"panel-lcd:abc","follows":false}"""));

        using var doc = JsonDocument.Parse(await (await _client.GetAsync("/lighting/brightness-schedule/devices")).Content.ReadAsStringAsync());
        Assert.False(Find(doc.RootElement, "panel-lcd:abc").GetProperty("follows").GetBoolean());
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
}
