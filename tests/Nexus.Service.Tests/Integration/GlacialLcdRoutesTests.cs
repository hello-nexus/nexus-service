using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Xunit;

namespace Nexus.Service.Tests.Integration;

public class GlacialLcdRoutesTests
{
    private static HttpClient Client(NexusAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task With_the_nexus_panel_off_the_state_says_so_and_the_panel_mode_is_refused()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var state = await client.GetAsync("/devices/asus-glacial-lcd/state");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        using var doc = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("panelModeAvailable").GetBoolean());

        var put = await client.PutAsync("/devices/asus-glacial-lcd/settings",
            new StringContent("{\"mode\":\"panel\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Theory]
    [InlineData("quick", "summary/cpu-usage", HttpStatusCode.OK)]
    [InlineData("network", "Network Total", HttpStatusCode.OK)]
    [InlineData("fps", "fps/current", HttpStatusCode.OK)]
    [InlineData("nonsense", "x", HttpStatusCode.BadRequest)]
    public async Task Any_picker_category_is_accepted_for_a_widget(string category, string sensor, HttpStatusCode expected)
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var put = await client.PutAsync("/devices/asus-glacial-lcd/settings",
            new StringContent($"{{\"slots\":[{{\"category\":\"{category}\",\"sensor\":\"{sensor}\",\"label\":\"W\"}}]}}", Encoding.UTF8, "application/json"));
        Assert.Equal(expected, put.StatusCode);
    }
}
