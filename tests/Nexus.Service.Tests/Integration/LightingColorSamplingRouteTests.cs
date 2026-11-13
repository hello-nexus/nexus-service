using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class LightingColorSamplingRouteTests : IClassFixture<SiblingZoneHostFactory>
{
    private readonly SiblingZoneHostFactory _factory;
    private readonly HttpClient _client;

    public LightingColorSamplingRouteTests(SiblingZoneHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private Task<HttpResponseMessage> Post(string body) =>
        _client.PostAsync("/devices/lighting-devices/color-sampling", new StringContent(body, Encoding.UTF8, "application/json"));

    private NexusSettings Settings => _factory.Services.GetRequiredService<IConfigStore>().Load();

    [Theory]
    [InlineData("vivid")]
    [InlineData("ignore-black")]
    public async Task Mode_IsStored_ReachesTheEngine_AndShowsOnTheCard(string mode)
    {
        var res = await Post($$"""{"id":"openrgb-s-OTHER","mode":"{{mode}}"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(mode, Settings.Devices.ColorSamplingModes["openrgb-s-OTHER"]);
        var engine = _factory.Services.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>();
        Assert.Equal(Nexus.Service.Lighting.Engine.ColorSamplings.Of(Settings.Devices.ColorSamplingModes, "openrgb-s-OTHER"), engine.ColorModeOf("openrgb-s-OTHER"));

        var cards = new List<Nexus.Service.Models.Devices.LightingDevice>
        {
            new() { Id = "openrgb-s-OTHER" },
            new() { Id = "other-card" },
        };
        Nexus.Service.Lighting.CompositeLightingDeviceProvider.ApplyAreaSampling(engine, cards, Settings);
        Assert.Equal(mode, cards[0].ColorSampling);
        Assert.Equal("standard", cards[1].ColorSampling);
    }

    [Fact]
    public async Task Standard_ClearsTheEntry_EvenForAnUnlistedId()
    {
        Store.Update(s => s.Devices.ColorSamplingModes = new Dictionary<string, string> { ["gone-card"] = "vivid" });
        var res = await Post("""{"id":"gone-card","mode":"standard"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(Settings.Devices.ColorSamplingModes);
    }

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    [Theory]
    [InlineData("""{"id":"openrgb-s-OTHER","mode":"sepia"}""")]
    [InlineData("""{"id":"openrgb-s-OTHER","mode":""}""")]
    [InlineData("""{"id":"","mode":"standard"}""")]
    [InlineData("""{"id":"unknown-card","mode":"vivid"}""")]
    public async Task Invalid_input_is_rejected(string body)
    {
        var res = await Post(body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(Settings.Devices.ColorSamplingModes);
    }
}
