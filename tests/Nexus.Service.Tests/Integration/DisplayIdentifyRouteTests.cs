using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Tests.Integration;

public sealed class DisplayIdentifyRouteTests : IClassFixture<NexusAppFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private sealed class OneDisplayProvider : IDisplayTopologyProvider
    {
        public bool PositionsAvailable => true;
        public IReadOnlyList<RawDisplayInfo>? Enumerate() => new List<RawDisplayInfo>
        {
            new() { Id = "route-test-display", Number = 1, Name = "Left", Width = 1920, Height = 1080, ResolutionWidth = 1920, ResolutionHeight = 1080, Scale = 1, IsPrimary = true, Orientation = "Landscape" },
        };
    }

    public DisplayIdentifyRouteTests(NexusAppFactory factory) => _factory = factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IDisplayTopologyProvider>();
            s.AddSingleton<IDisplayTopologyProvider>(new OneDisplayProvider());
        }));

    public void Dispose() => _factory.Dispose();

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
    public async Task Identify_drops_ids_that_match_no_display()
    {
        var res = await AuthedClient().PostAsJsonAsync("/displays/identify", new
        {
            displays = new[] { new { displayId = "ghost", label = "9", name = "Nowhere" } },
        });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Null(_factory.Services.GetRequiredService<DisplayIdentifyService>().Current());
    }

    [Fact]
    public async Task Identify_with_no_displays_is_still_204()
    {
        var res = await AuthedClient().PostAsJsonAsync("/displays/identify", new { });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }
}
