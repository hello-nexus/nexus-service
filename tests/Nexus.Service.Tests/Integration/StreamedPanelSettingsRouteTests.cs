using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class StreamedPanelSettingsRouteTests : IDisposable
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

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadCap(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("fpsCap").Clone();
    }

    [Fact]
    public async Task Get_defaults_to_30()
    {
        var response = await Client().GetAsync("/panel/streams/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(30, (await ReadCap(response)).GetInt32());
    }

    [Fact]
    public async Task Put_persists_and_null_is_serialized()
    {
        var client = Client();

        var put = await client.PutAsync("/panel/streams/settings", Body("{\"fpsCap\":5}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(5, (await ReadCap(put)).GetInt32());
        Assert.Equal(5, _factory.Services.GetRequiredService<IConfigStore>().Load().StreamedPanelFpsCap);

        var off = await client.PutAsync("/panel/streams/settings", Body("{\"fpsCap\":null}"));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadCap(off)).ValueKind);
        var get = await client.GetAsync("/panel/streams/settings");
        Assert.Equal(JsonValueKind.Null, (await ReadCap(get)).ValueKind);
    }

    [Theory]
    [InlineData("{\"fpsCap\":25}")]
    [InlineData("{\"fpsCap\":0}")]
    [InlineData("{\"fpsCap\":60}")]
    [InlineData("{}")]
    [InlineData("{\"fpscap\":null}")]
    [InlineData("{\"fpsCap\":\"20\"}")]
    [InlineData("{\"fpsCap\":20.5}")]
    public async Task Put_rejects_values_outside_the_allowed_set(string json)
    {
        var response = await Client().PutAsync("/panel/streams/settings", Body(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Requires_the_service_token()
    {
        var response = await _factory.CreateClient().GetAsync("/panel/streams/settings");

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }
}
