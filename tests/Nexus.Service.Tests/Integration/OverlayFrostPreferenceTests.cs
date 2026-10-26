using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// overlay.frost over the real /preferences pipeline: off by default, set and
/// cleared by a partial patch, and untouched by a patch that omits it.
/// </summary>
public sealed class OverlayFrostPreferenceTests : IDisposable
{
    private readonly NexusAppFactory _factory;

    public OverlayFrostPreferenceTests()
    {
        _factory = new NexusAppFactory();
        _factory.Services.GetRequiredService<ProfileManager>().Initialize();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<bool> ReadFrostAsync(HttpClient client)
    {
        var prefs = await client.GetFromJsonAsync<JsonElement>("/preferences");
        return prefs.GetProperty("overlay").GetProperty("frost").GetBoolean();
    }

    [Fact]
    public async Task Frost_is_off_by_default_and_follows_a_partial_patch()
    {
        var client = AuthedClient();
        Assert.False(await ReadFrostAsync(client));

        var on = await client.PostAsJsonAsync("/preferences", new { overlay = new { frost = true } });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(await ReadFrostAsync(client));

        var other = await client.PostAsJsonAsync("/preferences", new { overlay = new { opacity = 0.5 } });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.True(await ReadFrostAsync(client));

        var off = await client.PostAsJsonAsync("/preferences", new { overlay = new { frost = false } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(await ReadFrostAsync(client));
    }
}
