using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The SPA's Lighting page keeps its device list (and every card's LED count)
/// in sync solely through the `lighting` topic - a mutation that persists
/// without broadcasting leaves the cards stale until an unrelated mutation
/// happens to fire. This pins the broadcast on the LED-count resize route.
/// </summary>
public sealed class LightingDeviceMutationBroadcastTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public LightingDeviceMutationBroadcastTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    [Fact]
    public async Task ZoneSize_BroadcastsLightingTopic()
    {
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);
        using var sub = hub.AddTestSubscription(PanelTopics.Lighting);

        var res = await _client.PostAsync(
            "/devices/lighting-devices/zone-size",
            new StringContent("""{"id":"stub-zone","count":24}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(PanelTopics.Lighting, captured);
    }
}
