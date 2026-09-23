using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

public sealed class Y70CompatibilityRenderingRouteTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public Y70CompatibilityRenderingRouteTests(NexusAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static async Task<bool> ReadBool(HttpResponseMessage res, string property)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty(property).GetBoolean();
    }

    [Fact]
    public async Task Setting_round_trips_and_reaches_the_overlay_state()
    {
        var client = AuthedClient();

        var post = await client.PostAsync("/y70/compatibility-rendering",
            new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        Assert.True(await ReadBool(await client.GetAsync("/y70/compatibility-rendering"), "enabled"));
        Assert.Equal(OperatingSystem.IsWindows(),
            await ReadBool(await client.GetAsync("/y70/compatibility-rendering"), "supported"));
        Assert.True(await ReadBool(await client.GetAsync("/overlay/state"), "y70CompatibilityRendering"));

        await client.PostAsync("/y70/compatibility-rendering",
            new StringContent("{\"enabled\":false}", Encoding.UTF8, "application/json"));
        Assert.False(await ReadBool(await client.GetAsync("/overlay/state"), "y70CompatibilityRendering"));
    }
}
