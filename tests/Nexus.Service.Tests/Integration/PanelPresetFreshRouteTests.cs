using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelPresetFreshRouteTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public PanelPresetFreshRouteTests(NexusAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static PanelLayoutDto Layout() => new()
    {
        LayoutSchemaVersion = 2,
        Surface = "y70",
        Pages = { new PanelPageDto { Id = "p", Widgets = { new PanelWidgetDto { Id = "w", Type = "clock" } } } },
    };

    [Fact]
    public async Task Create_fresh_with_an_active_preset_empties_the_panel_and_keeps_the_old_preset()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var id = "fresh-route-" + Guid.NewGuid().ToString("N")[..8];
        store.Update(s => s.PanelDevices[id] = new PanelDeviceRecord
        {
            Id = id,
            Layout = Layout(),
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 },
        });
        var client = AuthedClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/panel/devices/{id}/presets", Json("""{"name":"Desk"}"""))).StatusCode);

        var res = await client.PostAsync($"/panel/devices/{id}/presets", Json("""{"name":"Blank","fresh":true}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var record = store.Load().PanelDevices[id];
        Assert.Empty(record.Layout!.Pages.SelectMany(p => p.Widgets));
        Assert.Equal("clock", record.Presets!.Single(p => p.Name == "Desk").Layout!.Pages[0].Widgets[0].Type);
        Assert.Empty(record.Presets!.Single(p => p.Name == "Blank").Layout!.Pages.SelectMany(p => p.Widgets));
    }
}
