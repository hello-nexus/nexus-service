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

    private void SeedOverlay(bool migrated)
    {
        _factory.Services.GetRequiredService<IConfigStore>().Update(s =>
        {
            s.Overlay.Enabled = true;
            s.Overlay.MigratedToPanels = migrated;
            s.Overlay.Layout.Clear();
            s.Overlay.Layout.Add(new OverlayWidgetDto { Id = "w1", Type = "clock" });
        });
    }

    [Theory]
    [InlineData(false, true, 1, 1)]
    [InlineData(true, false, 0, 0)]
    public async Task Overlay_state_and_widgets_are_gated_by_the_migration_marker(bool migrated, bool enabled, int pinned, int widgets)
    {
        SeedOverlay(migrated);
        var client = AuthedClient();

        using var state = JsonDocument.Parse(await client.GetStringAsync("/overlay/state"));
        Assert.Equal(enabled, state.RootElement.GetProperty("overlayEnabled").GetBoolean());
        Assert.Equal(pinned, state.RootElement.GetProperty("pinned").GetInt32());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/overlay/widgets"));
        Assert.Equal(widgets, list.RootElement.GetArrayLength());
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
