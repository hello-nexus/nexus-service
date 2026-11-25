using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The per-screen clip frame rate cap through the real wireless and TL screen routes. The Flex
/// route shares the same checks but refuses a screen the hardware has not reported, so it cannot
/// be reached without a unit; its DTO and storage use the same helpers these tests pin.
/// </summary>
public sealed class FanLcdFrameRateRoutesTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public FanLcdFrameRateRoutesTests(NexusAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static async Task<bool> PostRefused(HttpClient client, string route, int fpsCap)
    {
        var res = await client.PostAsJsonAsync(route, new { serial = "S1", fpsCap });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("error").GetBoolean();
    }

    private int? StoredWireless() =>
        _factory.Services.GetRequiredService<IConfigStore>().Load().Devices.LianLiWireless.Screens["S1"].FpsCap;

    private int? StoredTl() =>
        _factory.Services.GetRequiredService<IConfigStore>().Load().Devices.TlLcdScreens["S1"].FpsCap;

    [Theory]
    [InlineData("/devices/lianli-wireless/screen/settings")]
    [InlineData("/devices/lianli-tl/screen/settings")]
    public async Task A_negative_cap_is_refused_and_stores_nothing(string route)
    {
        Assert.True(await PostRefused(Client(), route, -1));
        var devices = _factory.Services.GetRequiredService<IConfigStore>().Load().Devices;
        Assert.False(devices.LianLiWireless.Screens.ContainsKey("S1"));
        Assert.False(devices.TlLcdScreens.ContainsKey("S1"));
    }

    [Fact]
    public async Task The_wireless_route_stores_off_as_zero_a_cap_clamped_and_reports_the_effective_cap()
    {
        var hub = _factory.Services.GetRequiredService<Slv3LcdHub>();
        hub.UpdateScreens(new[] { new Slv3LcdScreenInfo { Serial = "S1", Position = 0 } });
        var client = Client();

        async Task<int> ReportedCap()
        {
            var body = await client.GetFromJsonAsync<JsonElement>("/devices/lianli-wireless/screens");
            return body.GetProperty("screens")[0].GetProperty("fpsCap").GetInt32();
        }

        Assert.Equal(FanLcdClipTiming.DefaultFps, await ReportedCap());

        Assert.False(await PostRefused(client, "/devices/lianli-wireless/screen/settings", 0));
        Assert.Equal(0, StoredWireless());
        Assert.Equal(0, await ReportedCap());

        Assert.False(await PostRefused(client, "/devices/lianli-wireless/screen/settings", 60));
        Assert.Equal(60, StoredWireless());
        Assert.Equal(60, await ReportedCap());

        Assert.False(await PostRefused(client, "/devices/lianli-wireless/screen/settings", 100000));
        Assert.Equal(PanelDeviceRegistry.MaxFpsCap, StoredWireless());
    }

    [Fact]
    public async Task The_tl_route_stores_off_as_zero_and_a_cap_clamped()
    {
        var client = Client();
        const string route = "/devices/lianli-tl/screen/settings";

        Assert.False(await PostRefused(client, route, 0));
        Assert.Equal(0, StoredTl());
        Assert.False(await PostRefused(client, route, 45));
        Assert.Equal(45, StoredTl());
        Assert.False(await PostRefused(client, route, 100000));
        Assert.Equal(PanelDeviceRegistry.MaxFpsCap, StoredTl());
    }
}
