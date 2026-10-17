using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// A panel with no saved layout is served a starter. Its ids must be the same on every read:
/// clients key an open widget on them and refetch on any panel/device frame.
/// </summary>
public sealed class PanelStarterLayoutRouteTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static async Task<string[]> IdsAsync(HttpClient client, string id)
    {
        var res = await client.GetAsync($"/panel/devices/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var layout = (await res.Content.ReadFromJsonAsync<PanelDeviceRecord>())!.Layout!;
        return layout.Pages.Select(p => p.Id).Concat(layout.Pages.SelectMany(p => p.Widgets).Select(w => w.Id)).ToArray();
    }

    [Theory]
    [InlineData("y70", 1920, 515)]
    [InlineData("phone", 412, 915)]
    [InlineData("q60", 1280, 480)]
    [InlineData("monitor", 3840, 720)]
    public async Task An_unedited_record_returns_the_same_widget_ids_on_every_read(string surface, int width, int height)
    {
        var client = Client();
        var created = await client.PostAsJsonAsync("/panel/devices", new PanelDeviceCreateBody
        {
            DisplayName = "Starter",
            Capabilities = new PanelDeviceCapabilities { Surface = surface, CssWidth = width, CssHeight = height },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<PanelDeviceRecord>())!.Id;

        var first = await IdsAsync(client, id);
        var second = await IdsAsync(client, id);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }
}
