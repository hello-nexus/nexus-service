using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class DevicePinRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public DevicePinRoutesTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private Task<HttpResponseMessage> Pin(string ids, string pin) =>
        _client.PostAsync("/devices/lighting-devices/pins", Json($$"""{"ids":[{{ids}}],"pin":{{pin}}}"""));

    [Fact]
    public async Task A_bulk_pin_lands_on_every_id_and_the_get_route_lists_it()
    {
        var res = await Pin("\"pin-a1\",\"pin-a2\"", """{"kind":"animate","effect":"plasma"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("plasma", Store.Load().Lighting.DevicePins["pin-a1"].Effect);
        Assert.Equal("plasma", Store.Load().Lighting.DevicePins["pin-a2"].Effect);

        var get = await _client.GetAsync("/devices/lighting-devices/pins");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var pin = doc.RootElement.GetProperty("pins").GetProperty("pin-a1");
        Assert.Equal("animate", pin.GetProperty("kind").GetString());
        Assert.Equal("plasma", pin.GetProperty("effect").GetString());
        Assert.False(pin.TryGetProperty("color", out _));
    }

    [Fact]
    public async Task A_new_pin_replaces_the_old_one_and_a_null_pin_clears()
    {
        await Pin("\"pin-b1\"", """{"kind":"animate","effect":"plasma"}""");
        await Pin("\"pin-b1\"", """{"kind":"static","effect":"flat","color":"#112233","slot":2}""");
        var held = Store.Load().Lighting.DevicePins["pin-b1"];
        Assert.Equal("static", held.Kind);
        Assert.Equal("#112233", held.Color);
        Assert.Equal(2, held.Slot);

        await Pin("\"pin-b1\"", """{"kind":"mirror"}""");
        Assert.Equal("mirror", Store.Load().Lighting.DevicePins["pin-b1"].Kind);

        var cleared = await Pin("\"pin-b1\"", "null");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False(Store.Load().Lighting.DevicePins.ContainsKey("pin-b1"));
    }

    [Theory]
    [InlineData("""{"kind":"bogus"}""")]
    [InlineData("""{"kind":""}""")]
    [InlineData("""{"kind":"animate","effect":"gradientlinear"}""")]
    [InlineData("""{"kind":"animate","effect":"simplered"}""")]
    [InlineData("""{"kind":"animate","effect":"not-an-effect"}""")]
    [InlineData("""{"kind":"animate"}""")]
    [InlineData("""{"kind":"static","effect":"plasma"}""")]
    [InlineData("""{"kind":"static"}""")]
    [InlineData("""{"kind":"mirror","effect":"plasma"}""")]
    [InlineData("""{"kind":"mirror","hue":0.5}""")]
    [InlineData("""{"kind":"media","effect":"plasma"}""")]
    [InlineData("""{"kind":"media","hue":0.5}""")]
    [InlineData("""{"kind":"media","color":"#112233"}""")]
    [InlineData("""{"kind":"media","params":{"x":1}}""")]
    [InlineData("""{"kind":"media","slot":1}""")]
    public async Task An_invalid_pin_is_refused_and_stores_nothing(string pin)
    {
        var res = await Pin("\"pin-c1\"", pin);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(Store.Load().Lighting.DevicePins.ContainsKey("pin-c1"));
    }

    [Fact]
    public async Task A_media_pin_is_accepted_stored_and_listed_with_no_other_fields()
    {
        var res = await Pin("\"pin-g1\"", """{"kind":"media"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("media", Store.Load().Lighting.DevicePins["pin-g1"].Kind);

        var get = await _client.GetAsync("/devices/lighting-devices/pins");
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var pin = doc.RootElement.GetProperty("pins").GetProperty("pin-g1");
        Assert.Equal("media", pin.GetProperty("kind").GetString());
        Assert.False(pin.TryGetProperty("effect", out _));
        Assert.False(pin.TryGetProperty("color", out _));
    }

    [Fact]
    public async Task A_preset_round_trips_a_media_pin()
    {
        await Pin("\"pin-h1\"", """{"kind":"media"}""");
        var createRes = await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"Media pinned"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
        Assert.Equal("media", Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!.DevicePins!["pin-h1"].Kind);

        await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"Open"}"""));
        await Pin("\"pin-h1\"", "null");
        Assert.False(Store.Load().Lighting.DevicePins.ContainsKey("pin-h1"));

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));
        Assert.Equal("media", Store.Load().Lighting.DevicePins["pin-h1"].Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"keepMode":true}""")]
    public async Task Media_play_keeps_its_responses_with_or_without_a_body(string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/media/no-such-item/play");
        if (body.Length > 0) req.Content = Json(body);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(req)).StatusCode);

        var bad = new HttpRequestMessage(HttpMethod.Post, "/media/bad.id/play");
        if (body.Length > 0) bad.Content = Json(body);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(bad)).StatusCode);
    }

    [Theory]
    [InlineData("static", "")]
    [InlineData("media", "keep-me")]
    public async Task Deleting_the_selected_media_clears_it_unless_global_media_is_active(string sync, string expected)
    {
        var lib = _factory.Services.GetRequiredService<Nexus.Service.Media.MediaLibrary>();
        var id = "del-" + sync;
        lib.SaveMeta(new Nexus.Service.Models.Media.MediaItem { Id = id, Name = id, Type = "static", Frames = 1, Fps = 1, Width = 4, Height = 4 });
        Store.Update(s => { s.Lighting.Sync = sync; s.Lighting.LastMediaId = id; });
        var expectedId = expected == "keep-me" ? id : "";

        var res = await _client.DeleteAsync($"/media/{id}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(expectedId, Store.Load().Lighting.LastMediaId);
        Assert.Equal(sync, Store.Load().Lighting.Sync);
    }

    [Fact]
    public async Task Deleting_an_item_that_is_no_longer_selected_leaves_the_selection()
    {
        var lib = _factory.Services.GetRequiredService<Nexus.Service.Media.MediaLibrary>();
        lib.SaveMeta(new Nexus.Service.Models.Media.MediaItem { Id = "old-one", Name = "o", Type = "static", Frames = 1, Fps = 1, Width = 4, Height = 4 });
        Store.Update(s => { s.Lighting.Sync = "static"; s.Lighting.LastMediaId = "newer-one"; });

        var res = await _client.DeleteAsync("/media/old-one");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("newer-one", Store.Load().Lighting.LastMediaId);
    }

    [Fact]
    public async Task A_simple_mode_sweep_is_accepted_as_an_animate_pin()
    {
        var res = await Pin("\"pin-f1\"", """{"kind":"animate","effect":"sweeprainbow"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task A_malformed_static_color_answers_like_an_unknown_effect()
    {
        var res = await _client.PostAsync("/lighting/static/headless-start", Json("""{"color":"not-a-color"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"error\":true", await res.Content.ReadAsStringAsync());
        var unknown = await _client.PostAsync("/lighting/static/headless-start", Json("""{"effect":"nope"}"""));
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Contains("\"error\":true", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_static_effect_from_the_catalog_is_accepted()
    {
        var res = await Pin("\"pin-d1\"", """{"kind":"static","effect":"gradientlinear","hue":0.25,"params":{"u_angle":90}}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var held = Store.Load().Lighting.DevicePins["pin-d1"];
        Assert.Equal(0.25f, held.Hue!.Value, 3);
        Assert.Equal(90f, held.Params!["u_angle"], 3);
    }

    [Fact]
    public async Task The_removed_routes_are_gone()
    {
        foreach (var (method, path) in new[]
        {
            ("GET", "/devices/lighting-devices/static-looks"),
            ("POST", "/devices/lighting-devices/static-lock"),
            ("GET", "/devices/lighting-devices/animate-locks"),
            ("POST", "/devices/lighting-devices/animate-lock"),
        })
        {
            var req = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") req.Content = Json("{}");
            var res = await _client.SendAsync(req);
            Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {path} -> {res.StatusCode}");
        }
    }

    [Fact]
    public async Task A_preset_round_trips_the_pins()
    {
        await Pin("\"pin-e1\"", """{"kind":"mirror"}""");
        var createRes = await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"Pinned"}"""));
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("preset").GetProperty("id").GetString()!;
        Assert.Equal("mirror", Store.Load().Lighting.LayoutPresets.Find(p => p.Id == id)!.DevicePins!["pin-e1"].Kind);

        await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"Open"}"""));
        await Pin("\"pin-e1\"", "null");
        Assert.False(Store.Load().Lighting.DevicePins.ContainsKey("pin-e1"));

        await _client.PostAsync($"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));
        Assert.Equal("mirror", Store.Load().Lighting.DevicePins["pin-e1"].Kind);
    }
}
