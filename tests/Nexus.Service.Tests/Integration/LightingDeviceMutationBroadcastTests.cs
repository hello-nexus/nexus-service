using System.Linq;
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

    [Fact]
    public async Task Rename_PersistsAndBroadcastsLightingTopic()
    {
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);
        using var sub = hub.AddTestSubscription(PanelTopics.Lighting);

        var res = await _client.PostAsync(
            "/devices/lighting-devices/name",
            new StringContent("""{"id":"stub-zone","name":"  Top intake  "}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(PanelTopics.Lighting, captured);
        var store = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>();
        Assert.Equal("Top intake", store.Load().Lighting.DeviceNames["stub-zone"]);
    }

    [Fact]
    public async Task Rename_WithAnEmptyNameClearsTheStoredRename()
    {
        await _client.PostAsync(
            "/devices/lighting-devices/name",
            new StringContent("""{"id":"stub-zone","name":"Top intake"}""", Encoding.UTF8, "application/json"));

        var res = await _client.PostAsync(
            "/devices/lighting-devices/name",
            new StringContent("""{"id":"stub-zone","name":""}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var store = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>();
        Assert.False(store.Load().Lighting.DeviceNames.ContainsKey("stub-zone"));
    }

    [Fact]
    public async Task PutGroups_PersistsCapsAndBroadcastsLightingTopic()
    {
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);
        using var sub = hub.AddTestSubscription(PanelTopics.Lighting);

        var groups = string.Join(",", Enumerable.Range(0, 14)
            .Select(i => $$"""{"id":"g{{i}}","name":"G{{i}}","members":["card-{{i}}"]}"""));
        var res = await _client.PutAsync(
            "/devices/lighting-devices/groups",
            new StringContent($$"""{"groups":[{{groups}}]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(PanelTopics.Lighting, captured);
        var stored = _factory.Services.GetRequiredService<Nexus.Service.Persistence.IConfigStore>()
            .Load().Lighting.DeviceGroups;
        Assert.Equal(Nexus.Service.Common.DeviceGroupList.MaxGroups, stored.Count);
        Assert.Equal("g0", stored[0].Id);
    }

    [Fact]
    public async Task PutStacks_PersistsBroadcastsAndRidesGetAll()
    {
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);
        using var sub = hub.AddTestSubscription(PanelTopics.Lighting);

        var res = await _client.PutAsync(
            "/devices/lighting-devices/stacks",
            new StringContent("""{"stacks":[{"id":"l1","name":"Keeb","members":["stub-zone","other"]}]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(PanelTopics.Lighting, captured);
        var all = await _client.GetAsync("/devices/lighting-devices/all");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var json = await all.Content.ReadAsStringAsync();
        Assert.Contains("\"stacks\":[{\"id\":\"l1\"", json);
        Assert.Contains("\"members\":[\"stub-zone\",\"other\"]", json);
    }

    [Fact]
    public async Task GetAll_CarriesTheStoredGroups()
    {
        await _client.PutAsync(
            "/devices/lighting-devices/groups",
            new StringContent("""{"groups":[{"id":"g1","name":"Desk","members":["stub-zone"]}]}""", Encoding.UTF8, "application/json"));

        var res = await _client.GetAsync("/devices/lighting-devices/all");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"id\":\"g1\"", await res.Content.ReadAsStringAsync());
    }
}
