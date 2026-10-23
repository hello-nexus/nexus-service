using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

public sealed class AstralRoutesTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public AstralRoutesTests(NexusAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_astral_routes_return_the_state_contract()
    {
        var client = Client();
        var state = await Json(await client.GetAsync("/astral"));
        Assert.True(state.TryGetProperty("supported", out _));
        Assert.Equal(JsonValueKind.Array, state.GetProperty("cards").ValueKind);
        Assert.Equal(60, state.GetProperty("settings").GetProperty("overcurrentSeconds").GetInt32());
        Assert.False(state.GetProperty("heldOff").GetBoolean());

        var patched = await Json(await client.PostAsJsonAsync("/astral/settings", new { equalizer = true }));
        Assert.True(patched.GetProperty("settings").GetProperty("equalizer").GetBoolean());
        Assert.True(patched.GetProperty("settings").GetProperty("alerts").GetBoolean());

        await Json(await client.PostAsync("/astral/calibrate", null));
        await Json(await client.PostAsync("/astral/shutdown/cancel", null));
        var now = await Json(await client.PostAsync("/astral/shutdown/now", null));
        // The service omits nulls, so no countdown reads as an absent field.
        Assert.False(now.TryGetProperty("countdown", out _));
    }

#if DEV_TOOLS
    [Fact]
    public async Task The_sim_card_flows_through_state_and_the_devices_entry()
    {
        var client = Client();
        var off = await Json(await client.GetAsync("/dev/sim/astral"));
        Assert.False(off.GetProperty("active").GetBoolean());

        var on = await Json(await client.PostAsJsonAsync("/dev/sim/astral", new { pinAmps = new[] { 8f, 8f, 8f, 8f, 8f, 13f } }));
        Assert.True(on.GetProperty("active").GetBoolean());
        Assert.Equal(6, on.GetProperty("pinAmps").GetArrayLength());

        var state = await Json(await client.GetAsync("/astral"));
        Assert.True(state.GetProperty("simulated").GetBoolean());
        Assert.True(state.GetProperty("supported").GetBoolean());
        var card = Assert.Single(state.GetProperty("cards").EnumerateArray());
        Assert.Equal("ROG Astral RTX 5090 (simulated)", card.GetProperty("name").GetString());
        Assert.Equal("overcurrent", card.GetProperty("state").GetString());

        var off2 = await Json(await client.DeleteAsync("/dev/sim/astral"));
        Assert.False(off2.GetProperty("active").GetBoolean());
        var after = await Json(await client.GetAsync("/astral"));
        Assert.Equal(0, after.GetProperty("cards").GetArrayLength());
    }

    [Fact]
    public async Task The_sim_card_has_sensors_and_a_Devices_entry()
    {
        var client = Client();
        await client.PostAsJsonAsync("/dev/sim/astral", new { sag = 2f });
        var gpus = _factory.Services.GetRequiredService<Nexus.Service.Sensors.ISensorProvider>().GetGpus();
        var sim = Assert.Single(gpus, g => g.Id == "/gpu-nvidia/astral-sim");
        Assert.Contains(sim.Sensors, s => s.Id == "/gpu-nvidia/astral-sim/astral/pin6/current");
        Assert.Contains(sim.Sensors, s => s.Id == "/gpu-nvidia/astral-sim/astral/level/sag");
        var handler = _factory.Services.GetServices<Nexus.Service.Devices.IDeviceHandler>()
            .Single(h => h.Id == Nexus.Service.Devices.Handlers.AstralHandler.HandlerId);
        Assert.True(handler.IsConnected(Array.Empty<Nexus.Service.Devices.UsbDeviceEntry>()));
        await client.DeleteAsync("/dev/sim/astral");
        Assert.False(handler.IsConnected(Array.Empty<Nexus.Service.Devices.UsbDeviceEntry>()));
    }
#endif
}
