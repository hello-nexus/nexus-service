using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class DesktopMigrationRouteTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public DesktopMigrationRouteTests(NexusAppFactory factory)
    {
        _factory = factory;
        factory.ResetSettings();
    }

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Theory]
    [InlineData("/overlay/widgets")]
    [InlineData("/overlay/widgets/x")]
    [InlineData("/overlay/widgets/lock")]
    public async Task The_legacy_widget_routes_are_gone(string path)
    {
        var client = AuthedClient();

        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.DeleteAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Overlay_state_carries_no_desktop_widget_fields()
    {
        using var state = JsonDocument.Parse(await AuthedClient().GetStringAsync("/overlay/state"));
        foreach (var gone in new[] { "overlayEnabled", "alwaysOnTop", "monitor", "pinned" })
            Assert.False(state.RootElement.TryGetProperty(gone, out _), gone);
        Assert.True(state.RootElement.TryGetProperty("assignments", out _));
    }

    [Fact]
    public async Task Monitors_post_needs_the_service_token_and_stores_the_map()
    {
        var map = _factory.Services.GetRequiredService<OverlayMonitorMap>();
        var body = new StringContent("{\"monitors\":[{\"index\":2,\"displayId\":\"DEL-9\",\"primary\":true}]}", Encoding.UTF8, "application/json");

        var anonymous = await _factory.CreateClient().PostAsync("/overlay/monitors", body);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var ok = await AuthedClient().PostAsync("/overlay/monitors",
            new StringContent("{\"monitors\":[{\"index\":2,\"displayId\":\"DEL-9\",\"primary\":true}]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("DEL-9", map.DisplayIdForIndex(2));
        Assert.Equal("DEL-9", map.PrimaryDisplayId());
    }
}
