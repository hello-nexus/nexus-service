using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>The wired Uni hub routes address a hub past the first with ?hub= and keep its settings apart.</summary>
public sealed class LianLiHubRouteTests : IDisposable
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

    [Theory]
    [InlineData("lianli9")]
    [InlineData("lianli-wireless")]
    [InlineData("lianli2:port0")]
    public async Task An_unknown_hub_is_not_found(string hub)
    {
        var res = await Client().GetAsync($"/devices/lianli/state?hub={hub}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_second_hub_keeps_its_own_fan_counts()
    {
        var client = Client();
        var put = await client.PutAsJsonAsync("/devices/lianli/fan-count?hub=lianli2", new { port = 1, count = 3 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var second = JsonDocument.Parse(await client.GetStringAsync("/devices/lianli/state?hub=lianli2"));
        using var primary = JsonDocument.Parse(await client.GetStringAsync("/devices/lianli/state"));
        Assert.Equal(3, second.RootElement.GetProperty("fansPerPort")[1].GetInt32());
        Assert.NotEqual(3, primary.RootElement.GetProperty("fansPerPort")[1].GetInt32());

        var devices = _factory.Services.GetRequiredService<IConfigStore>().Load().Devices;
        Assert.Equal(3, devices.LianLiExtraHubs["lianli2"].Fans.GetFans(1));
    }
}
