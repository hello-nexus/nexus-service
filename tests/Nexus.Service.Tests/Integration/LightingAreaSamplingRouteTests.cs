using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class LightingAreaSamplingRouteTests : IClassFixture<SiblingZoneHostFactory>
{
    private readonly SiblingZoneHostFactory _factory;
    private readonly HttpClient _client;

    public LightingAreaSamplingRouteTests(SiblingZoneHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private Task<HttpResponseMessage> Post(string body) =>
        _client.PostAsync("/devices/lighting-devices/area-sampling", new StringContent(body, Encoding.UTF8, "application/json"));

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private NexusSettings Settings => _factory.Services.GetRequiredService<IConfigStore>().Load();

    [Fact]
    public async Task Toggle_PersistsAndReachesTheEngine_ThenClears()
    {
        var res = await Post("""{"id":"openrgb-s-OTHER","enabled":true}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("openrgb-s-OTHER", Settings.Devices.AreaSamplingDevices);
        Assert.True(_factory.Services.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>().AnyAreaSampling);

        await Post("""{"id":"openrgb-s-OTHER","enabled":false}""");
        Assert.Empty(Settings.Devices.AreaSamplingDevices);
        Assert.False(_factory.Services.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>().AnyAreaSampling);
    }

    [Fact]
    public async Task Disabling_an_unlisted_id_succeeds_and_clears_it()
    {
        Store.Update(s => s.Devices.AreaSamplingDevices = new List<string> { "gone-card" });
        var res = await Post("""{"id":"gone-card","enabled":false}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(Settings.Devices.AreaSamplingDevices);
    }

    [Theory]
    [InlineData("""{"id":"","enabled":true}""")]
    [InlineData("""{"id":"unknown-card","enabled":true}""")]
    public async Task Invalid_id_is_rejected(string body)
    {
        var res = await Post(body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(Settings.Devices.AreaSamplingDevices);
    }

    [Fact]
    public async Task PreviewLayout_ResponseCarriesTheDraftBoxes_OnlyWhenAreaSamplingIsOn()
    {
        var engine = _factory.Services.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>();
        engine.UpdateDevices(new[] { new Nexus.Service.Lighting.Engine.DeviceFrame(0, "openrgb-s-OTHER", 3, 0, 0, 100, 20) });
        const string draft = """{"ledCount":3,"leds":[{"index":0,"u":0.1,"v":0.5,"disabled":false},{"index":1,"u":0.5,"v":0.5,"disabled":false},{"index":2,"u":0.9,"v":0.5,"disabled":true}]}""";

        var off = await _client.PostAsync("/devices/lighting-devices/openrgb-s-OTHER/led-preview-layout", new StringContent(draft, Encoding.UTF8, "application/json"));
        using var offDoc = System.Text.Json.JsonDocument.Parse(await off.Content.ReadAsStringAsync());
        Assert.False(offDoc.RootElement.GetProperty("error").GetBoolean());
        // The wire omits nulls (AppJsonContext WhenWritingNull), so "off" is an absent areaBoxes.
        Assert.False(offDoc.RootElement.TryGetProperty("areaBoxes", out _));

        await Post("""{"id":"openrgb-s-OTHER","enabled":true}""");
        var on = await _client.PostAsync("/devices/lighting-devices/openrgb-s-OTHER/led-preview-layout", new StringContent(draft, Encoding.UTF8, "application/json"));
        using var onDoc = System.Text.Json.JsonDocument.Parse(await on.Content.ReadAsStringAsync());
        var boxes = onDoc.RootElement.GetProperty("areaBoxes").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(12, boxes.Length);
        Assert.Equal(new float[] { 0f, 0f, 0.3f, 1f }, boxes[..4]);
        Assert.Equal(new float[] { 0.3f, 0f, 1f, 1f }, boxes[4..8]);
        Assert.Equal(new float[] { 0, 0, 0, 0 }, boxes[8..]);
    }
}
