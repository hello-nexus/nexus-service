using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

/// <summary>The wired hub lighting route edits one port, one ring, or the merge order.</summary>
public sealed class LianLiLightingRouteTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();
    private readonly HttpClient _client;

    public LianLiLightingRouteTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<JsonElement> Lighting()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/devices/lianli/lighting"));
        return doc.RootElement.Clone();
    }

    private Task<HttpResponseMessage> Put(object body) => _client.PutAsJsonAsync("/devices/lianli/lighting", body);

    [Fact]
    public async Task Reports_ring_catalogs_unset_ports_and_the_index_merge_order()
    {
        var l = await Lighting();
        Assert.True(l.GetProperty("ringModes").GetProperty("inner").GetArrayLength() > 0);
        Assert.True(l.GetProperty("ringModes").GetProperty("outer").GetArrayLength() > 0);
        Assert.Equal(4, l.GetProperty("ports").GetArrayLength());
        Assert.All(l.GetProperty("ports").EnumerateArray(), p => Assert.Equal(JsonValueKind.Null, p.ValueKind));
        Assert.Equal("[0,1,2,3]", l.GetProperty("mergeOrder").GetRawText());
    }

    [Fact]
    public async Task A_port_edit_gives_that_port_its_own_look_and_reset_drops_it()
    {
        var hubMode = (await Lighting()).GetProperty("mode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Put(new { port = 1, mode = "static", speed = 4 })).StatusCode);

        var l = await Lighting();
        var port = l.GetProperty("ports")[1];
        Assert.Equal("static", port.GetProperty("whole").GetProperty("mode").GetString());
        Assert.Equal(4, port.GetProperty("whole").GetProperty("speed").GetInt32());
        Assert.Equal(JsonValueKind.Null, l.GetProperty("ports")[0].ValueKind);
        Assert.Equal(hubMode, l.GetProperty("mode").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Put(new { port = 1, resetPort = true })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Lighting()).GetProperty("ports")[1].ValueKind);
    }

    [Fact]
    public async Task Split_rings_edit_each_ring_from_its_own_catalog()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { ring = "outer", mode = "static" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(new { splitRings = true })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Put(new { ring = "outer", mode = "reflect" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { ring = "outer", mode = "taichi" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(new { ring = "inner", mode = "taichi" })).StatusCode);

        var l = await Lighting();
        Assert.Equal("taichi", l.GetProperty("innerRing").GetProperty("mode").GetString());
        Assert.Equal("reflect", l.GetProperty("outerRing").GetProperty("mode").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Put(new { splitRings = false })).StatusCode);
        // A null ring is left out of the response.
        Assert.False((await Lighting()).TryGetProperty("innerRing", out _));
    }

    [Fact]
    public async Task A_ring_edit_on_a_port_without_its_own_look_starts_from_the_hubs_rings()
    {
        Assert.Equal(HttpStatusCode.OK, (await Put(new { splitRings = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(new { ring = "inner", mode = "taichi" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Put(new { port = 2, ring = "outer", mode = "reflect" })).StatusCode);

        var port = (await Lighting()).GetProperty("ports")[2];
        Assert.Equal("taichi", port.GetProperty("innerRing").GetProperty("mode").GetString());
        Assert.Equal("reflect", port.GetProperty("outerRing").GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, (await Lighting()).GetProperty("ports")[0].ValueKind);
    }

    [Fact]
    public async Task Splitting_one_port_leaves_the_hub_whole()
    {
        Assert.Equal(HttpStatusCode.OK, (await Put(new { port = 1, splitRings = true })).StatusCode);

        var l = await Lighting();
        Assert.True(l.GetProperty("ports")[1].TryGetProperty("innerRing", out _));
        Assert.False(l.TryGetProperty("innerRing", out _));
    }

    [Fact]
    public async Task A_look_edit_cannot_carry_hub_settings()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { port = 1, mode = "static", merge = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { splitRings = true, argbSync = false })).StatusCode);
    }

    [Theory]
    [InlineData(new[] { 0, 0, 1, 2 })]
    [InlineData(new[] { 0, 1, 2 })]
    [InlineData(new[] { 0, 1, 2, 4 })]
    public async Task A_merge_order_must_name_every_port_once(int[] order)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { mergeOrder = order })).StatusCode);
    }

    [Fact]
    public async Task A_merge_order_is_saved()
    {
        Assert.Equal(HttpStatusCode.OK, (await Put(new { mergeOrder = new[] { 3, 2, 1, 0 } })).StatusCode);
        Assert.Equal("[3,2,1,0]", (await Lighting()).GetProperty("mergeOrder").GetRawText());
    }

    [Fact]
    public async Task A_port_cannot_take_the_lighting_page_mode()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { port = 0, mode = "custom" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new { port = 4, mode = "static" })).StatusCode);
    }
}
