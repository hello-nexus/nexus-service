using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.Integration;

/// <summary>The HydroShift II screen routes answer only for a HydroShift II the controller lists.</summary>
public sealed class Slv3AioScreenRouteTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();
    private readonly HttpClient _client;

    public Slv3AioScreenRouteTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task A_malformed_mac_is_a_bad_request()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/devices/lianli-wireless/aio-screen/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/devices/lianli-wireless/aio-screen/nope", new { brightness = 50 })).StatusCode);
    }

    [Fact]
    public async Task An_aio_the_controller_does_not_list_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/devices/lianli-wireless/aio-screen/112233445566")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/devices/lianli-wireless/aio-screen/112233445566", new { brightness = 50 })).StatusCode);
    }

    private const string AioMac = "AABBCCDDEEFF";

    private void ListAHydroShift() =>
        _factory.Services.GetRequiredService<Slv3Hub>().State.Fans = new[]
        {
            new Slv3FanInfo { Mac = AioMac, DevType = 10, BoundToUs = true },
        };

    [Fact]
    public async Task A_listed_aio_starts_on_the_default_screen_and_keeps_unsent_fields()
    {
        ListAHydroShift();
        using (var first = JsonDocument.Parse(await _client.GetStringAsync($"/devices/lianli-wireless/aio-screen/{AioMac}")))
        {
            Assert.Equal(Slv3Protocol.AioLcdBrightness, first.RootElement.GetProperty("brightness").GetInt32());
            Assert.Equal(Slv3Protocol.AioThemeCount, first.RootElement.GetProperty("themeCount").GetInt32());
        }

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"/devices/lianli-wireless/aio-screen/{AioMac.ToLowerInvariant()}", new { theme = 4, valueColor = "#00FF00", showFanSpeed = true, brightness = 250 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"/devices/lianli-wireless/aio-screen/{AioMac}", new { showCpuTemp = false })).StatusCode);

        using var saved = JsonDocument.Parse(await _client.GetStringAsync($"/devices/lianli-wireless/aio-screen/{AioMac}"));
        var root = saved.RootElement;
        Assert.Equal(4, root.GetProperty("theme").GetInt32());
        Assert.Equal("#00FF00", root.GetProperty("valueColor").GetString());
        Assert.True(root.GetProperty("showFanSpeed").GetBoolean());
        Assert.False(root.GetProperty("showCpuTemp").GetBoolean());
        Assert.Equal(100, root.GetProperty("brightness").GetInt32());
    }

    [Theory]
    [InlineData("{\"theme\":13}")]
    [InlineData("{\"theme\":-1}")]
    [InlineData("{\"labelColor\":\"red\"}")]
    [InlineData("{\"unitColor\":\"#12345\"}")]
    public async Task An_out_of_range_theme_or_malformed_colour_is_refused(string body)
    {
        ListAHydroShift();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsync($"/devices/lianli-wireless/aio-screen/{AioMac}", content)).StatusCode);
    }
}
