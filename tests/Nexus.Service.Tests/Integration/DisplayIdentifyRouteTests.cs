using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

public sealed class DisplayIdentifyRouteTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public DisplayIdentifyRouteTests(NexusAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task Identify_without_a_token_is_rejected()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/displays/identify", new { displays = new object[0] });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Identify_returns_204_and_publishes_the_labels()
    {
        var res = await AuthedClient().PostAsJsonAsync("/displays/identify", new
        {
            displays = new[] { new { displayId = "route-test-display", label = "3", name = "Left" } },
        });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var current = _factory.Services.GetRequiredService<DisplayIdentifyService>().Current();
        Assert.NotNull(current);
        var entry = Assert.Single(current!.Displays);
        Assert.Equal("route-test-display", entry.DisplayId);
        Assert.Equal("3", entry.Label);
        Assert.Equal("Left", entry.Name);
    }

    [Fact]
    public async Task Identify_with_no_displays_is_still_204()
    {
        var res = await AuthedClient().PostAsJsonAsync("/displays/identify", new { });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }
}
