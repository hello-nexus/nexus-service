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
        _client.PostAsync("/devices/lighting-devices/sampling", new StringContent(body, Encoding.UTF8, "application/json"));

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private NexusSettings Settings => _factory.Services.GetRequiredService<IConfigStore>().Load();

    [Fact]
    public async Task Toggle_PersistsAndReachesTheEngine_ThenClears()
    {
        var res = await Post("""{"id":"openrgb-s-OTHER","mode":"nearest"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("nearest", Settings.Devices.SamplingModes["openrgb-s-OTHER"]);
        Assert.True(_factory.Services.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>().AnyAreaSampling);

        await Post("""{"id":"openrgb-s-OTHER","mode":"led"}""");
        Assert.Empty(Settings.Devices.SamplingModes);
        Assert.False(_factory.Services.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>().AnyAreaSampling);
    }

    [Fact]
    public async Task Disabling_an_unlisted_id_succeeds_and_clears_it()
    {
        Store.Update(s => s.Devices.SamplingModes = new Dictionary<string, string> { ["gone-card"] = "rows" });
        var res = await Post("""{"id":"gone-card","mode":"led"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(Settings.Devices.SamplingModes);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("columns")]
    public async Task Each_mode_is_stored_by_its_name(string mode)
    {
        var res = await Post($$"""{"id":"openrgb-s-OTHER","mode":"{{mode}}"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(mode, Settings.Devices.SamplingModes["openrgb-s-OTHER"]);
    }

    [Theory]
    [InlineData("""{"id":"openrgb-s-OTHER","mode":"sideways"}""")]
    [InlineData("""{"id":"openrgb-s-OTHER","mode":""}""")]
    [InlineData("""{"id":"","mode":"led"}""")]
    [InlineData("""{"id":"","mode":"nearest"}""")]
    [InlineData("""{"id":"unknown-card","mode":"nearest"}""")]
    public async Task Invalid_id_is_rejected(string body)
    {
        var res = await Post(body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(Settings.Devices.SamplingModes);
    }

    [Fact]
    public async Task PreviewLayout_ResponseCarriesTheDraftCells_OnlyWhenAreaSamplingIsOn()
    {
        var engine = _factory.Services.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>();
        engine.UpdateDevices(new[] { new Nexus.Service.Lighting.Engine.DeviceFrame(0, "openrgb-s-OTHER", 3, 0, 0, 100, 20) });
        const string draft = """{"ledCount":3,"leds":[{"index":0,"u":0.1,"v":0.5,"disabled":false},{"index":1,"u":0.5,"v":0.5,"disabled":false},{"index":2,"u":0.9,"v":0.5,"disabled":true}]}""";

        var off = await _client.PostAsync("/devices/lighting-devices/openrgb-s-OTHER/led-preview-layout", new StringContent(draft, Encoding.UTF8, "application/json"));
        using var offDoc = System.Text.Json.JsonDocument.Parse(await off.Content.ReadAsStringAsync());
        Assert.False(offDoc.RootElement.GetProperty("error").GetBoolean());
        // The wire omits nulls (AppJsonContext WhenWritingNull), so "off" is an absent areaCells.
        Assert.False(offDoc.RootElement.TryGetProperty("areaCells", out _));

        await Post("""{"id":"openrgb-s-OTHER","mode":"nearest"}""");
        var on = await _client.PostAsync("/devices/lighting-devices/openrgb-s-OTHER/led-preview-layout", new StringContent(draft, Encoding.UTF8, "application/json"));
        using var onDoc = System.Text.Json.JsonDocument.Parse(await on.Content.ReadAsStringAsync());
        var cells = onDoc.RootElement.GetProperty("areaCells").EnumerateArray()
            .Select(c => c.EnumerateArray().Select(e => e.GetSingle()).ToArray()).ToArray();
        Assert.Equal(3, cells.Length);
        Assert.Empty(cells[2]);
        static (float min, float max) XRange(float[] poly) =>
            (Enumerable.Range(0, poly.Length / 2).Min(i => poly[i * 2]), Enumerable.Range(0, poly.Length / 2).Max(i => poly[i * 2]));
        var (a0, a1) = XRange(cells[0]);
        var (b0, b1) = XRange(cells[1]);
        Assert.Equal(0f, a0, 3);
        Assert.Equal(0.3f, a1, 3);
        Assert.Equal(0.3f, b0, 3);
        Assert.Equal(1f, b1, 3);
    }
}
