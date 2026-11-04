using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Xunit;

namespace Nexus.Service.Tests.Integration;

public sealed class SystemDevicesRoutesTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public SystemDevicesRoutesTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Desktop_token_gets_a_devices_array()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);

        var res = await client.GetAsync("/system/devices");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("devices").ValueKind);
    }

    [Fact]
    public async Task Request_without_a_token_is_refused()
    {
        var res = await _factory.CreateClient().GetAsync("/system/devices");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
