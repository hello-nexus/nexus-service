using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests.Integration;

public sealed class PawnIoRoutesTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public PawnIoRoutesTests(NexusAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Empty() => new("{}", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Get_keeps_existing_fields_and_adds_the_gate_fields_with_null_errorCode()
    {
        var res = await AuthedClient().GetAsync("/pawnio");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("installed", out _));
        Assert.True(root.TryGetProperty("open", out _));
        Assert.Equal("ok", root.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("errorCode").ValueKind);
        Assert.Equal("none", root.GetProperty("fixAction").GetString());
        Assert.False(root.GetProperty("fixing").GetBoolean());
        Assert.False(root.GetProperty("restarting").GetBoolean());
    }

    [Fact]
    public async Task Fix_returns_the_status_body()
    {
        var res = await AuthedClient().PostAsync("/pawnio/fix", Empty());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Mutating_routes_require_auth()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/pawnio/fix", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/pawnio/restart-pc", Empty())).StatusCode);
    }

    [Fact]
    public async Task RestartPc_is_refused_when_no_restart_is_needed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var res = await AuthedClient().PostAsync("/pawnio/restart-pc", Empty());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task RestartPc_is_refused_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var res = await AuthedClient().PostAsync("/pawnio/restart-pc", Empty());
        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
    }
}
