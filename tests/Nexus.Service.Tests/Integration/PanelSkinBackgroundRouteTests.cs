using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelSkinBackgroundRouteTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public PanelSkinBackgroundRouteTests(NexusAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private string SeedDevice()
    {
        var id = "skin-bg-route-" + Guid.NewGuid().ToString("N")[..8];
        _factory.Services.GetRequiredService<IConfigStore>().Update(s => s.PanelDevices[id] = new PanelDeviceRecord
        {
            Id = id,
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Phone },
        });
        return id;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Patch_a_shader_entry_with_an_invalid_effect_is_a_400_invalid_background()
    {
        var id = SeedDevice();

        var res = await AuthedClient().PostAsync($"/panel/devices/{id}", Json("""{"skinOverrides":{"neon":{"backgroundMode":"shader","backgroundEffect":"Bad Effect"}}}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("invalid_background", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_a_solid_entry_with_an_invalid_effect_is_accepted()
    {
        var id = SeedDevice();

        var res = await AuthedClient().PostAsync($"/panel/devices/{id}", Json("""{"skinOverrides":{"neon":{"backgroundMode":"solid","backgroundEffect":"Bad Effect"}}}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
