using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

public sealed class LogitechSpeakerRoutesTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public LogitechSpeakerRoutesTests(NexusAppFactory factory)
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

#if !DEV_TOOLS && !WINDOWS
    [Fact]
    public async Task Without_platform_support_the_routes_answer_not_found()
    {
        var client = Client();
        var state = await Json(await client.GetAsync("/devices/logitech-speaker"));
        Assert.False(state.GetProperty("present").GetBoolean());
        var res = await client.PostAsJsonAsync("/devices/logitech-speaker/eq", new { enabled = true });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
#endif

#if DEV_TOOLS
    [Fact]
    public async Task The_sim_speaker_flows_through_state_and_every_change_route()
    {
        var client = Client();
        try
        {
            var started = await Json(await client.PostAsync("/dev/sim/logitech-speaker", null));
            Assert.True(started.GetProperty("active").GetBoolean());

            var state = await Json(await client.GetAsync("/devices/logitech-speaker"));
            Assert.True(state.GetProperty("present").GetBoolean());
            Assert.True(state.GetProperty("simulated").GetBoolean());
            Assert.Equal("G560", state.GetProperty("model").GetString());
            Assert.Equal(8, state.GetProperty("channels").GetArrayLength());
            Assert.Equal(1f, state.GetProperty("channelGains")[3].GetSingle());
            Assert.False(state.GetProperty("eqEnabled").GetBoolean());
            Assert.False(state.TryGetProperty("preset", out _));

            var eq = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/eq", new { preset = "Cinematic" }));
            Assert.True(eq.GetProperty("eqEnabled").GetBoolean());
            Assert.Equal("Cinematic", eq.GetProperty("preset").GetString());
            Assert.Equal(-6f, eq.GetProperty("bands")[3].GetSingle());

            var band = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/eq",
                new { bands = new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 99f } }));
            Assert.Equal(6f, band.GetProperty("bands")[9].GetSingle());
            Assert.False(band.TryGetProperty("preset", out _));

            var surround = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/surround", new { enabled = true, room = "movie" }));
            Assert.True(surround.GetProperty("surroundEnabled").GetBoolean());
            Assert.Equal("movie", surround.GetProperty("room").GetString());

            var channel = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/channel", new { channel = 2, gain = 1.5f }));
            Assert.Equal(1.5f, channel.GetProperty("channelGains")[2].GetSingle());
            var noGain = await client.PostAsJsonAsync("/devices/logitech-speaker/channel", new { channel = 2 });
            Assert.Equal(HttpStatusCode.BadRequest, noGain.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/devices/logitech-speaker/channel", new { channel = 8, gain = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/devices/logitech-speaker/eq", new { bands = new[] { 1f, 2f } })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/devices/logitech-speaker/eq", new { preset = "nope" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/devices/logitech-speaker/surround", new { room = "attic" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/devices/logitech-speaker/volume", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/devices/logitech-speaker/presets/nope")).StatusCode);
            var reset = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/channel", new { }));
            Assert.Equal(1f, reset.GetProperty("channelGains")[2].GetSingle());

            var volume = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/volume", new { level = 0.25f }));
            Assert.Equal(0.25f, volume.GetProperty("volume").GetSingle());
            var bass = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/subwoofer", new { level = 130 }));
            Assert.Equal(100, bass.GetProperty("subwooferLevel").GetInt32());

            var saved = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/presets",
                new { name = "Mine", bands = new[] { 1f, 2f, 3f, 4f, 5f, 0f, 0f, 0f, 0f, 0f } }));
            Assert.Contains(saved.GetProperty("presets").EnumerateArray(), p => p.GetProperty("name").GetString() == "Mine" && !p.GetProperty("builtIn").GetBoolean());
            var builtInName = await client.PostAsJsonAsync("/devices/logitech-speaker/presets", new { name = "flat", bands = new float[10] });
            Assert.Equal(HttpStatusCode.BadRequest, builtInName.StatusCode);
            var deleted = await Json(await client.DeleteAsync("/devices/logitech-speaker/presets/Mine"));
            Assert.DoesNotContain(deleted.GetProperty("presets").EnumerateArray(), p => p.GetProperty("name").GetString() == "Mine");

            var test = await Json(await client.PostAsJsonAsync("/devices/logitech-speaker/test", new { channel = 5 }));
            Assert.False(test.GetProperty("error").GetBoolean());
            var badTest = await client.PostAsJsonAsync("/devices/logitech-speaker/test", new { channel = 8 });
            Assert.Equal(HttpStatusCode.BadRequest, badTest.StatusCode);
            var sim = await Json(await client.GetAsync("/dev/sim/logitech-speaker"));
            Assert.Equal(1, sim.GetProperty("testsPlayed").GetInt32());
            Assert.Equal(5, sim.GetProperty("lastTestChannel").GetInt32());

            var handler = _factory.Services.GetServices<Nexus.Service.Devices.IDeviceHandler>()
                .Single(h => h.Id == Nexus.Service.Devices.Handlers.LogitechSpeakerHandler.HandlerId);
            Assert.True(handler.IsConnected(Array.Empty<Nexus.Service.Devices.UsbDeviceEntry>()));
            Assert.Equal("Logitech G560", handler.Name);
        }
        finally
        {
            await client.DeleteAsync("/dev/sim/logitech-speaker");
        }

        var after = await Json(await client.GetAsync("/devices/logitech-speaker"));
        Assert.False(after.GetProperty("simulated").GetBoolean());
    }
#endif
}
