using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Routes;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

public sealed class CoolingLayoutRoutesTests : IClassFixture<LightingSceneAppFactory>
{
    private readonly LightingSceneAppFactory _factory;

    public CoolingLayoutRoutesTests(LightingSceneAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        Scene.Cooling.Reset();
    }

    private LightingSceneStore Scene => _factory.Services.GetRequiredService<LightingSceneStore>();

    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static string Quoted(IEnumerable<string> items) => string.Join(",", items.Select(i => $"\"{i}\""));

    private static string Layout(string directions = "{}", string bindings = "[]", string tests = "[]") =>
        $$"""{"directions":{{directions}},"bindings":{{bindings}},"tests":{{tests}}}""";

    private static string Put(int baseRev, string layout) => $$"""{"baseRev":{{baseRev}},"layout":{{layout}}}""";

    private const string Full = """
        {"directions":{"top-1":"exhaust","front-2":"intake"},
         "bindings":[{"channelId":"ch-1","slotKeys":["top-1","top-2"]}],
         "tests":[{"id":"t1","name":"Swap","moves":[{"from":"top-1","to":"rear-1"}],"directions":{"rear-1":"exhaust"}}]}
        """;

    private async Task<HttpResponseMessage> PutLayout(int baseRev, string layout) =>
        await Desktop().PutAsync("/cooling/layout", Json(Put(baseRev, layout)));

    private async Task AssertRejected(string layout)
    {
        var res = await PutLayout(0, layout);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True((await Body(res)).GetProperty("error").GetBoolean());
        Assert.Equal(0, (await Body(await Desktop().GetAsync("/cooling/layout"))).GetProperty("rev").GetInt32());
    }

    [Fact]
    public async Task Every_route_needs_the_desktop_token()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/cooling/layout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/cooling/layout", Json(Put(0, Layout())))).StatusCode);
    }

    [Fact]
    public async Task Get_is_rev_zero_with_an_empty_layout_when_nothing_is_saved()
    {
        var body = await Body(await Desktop().GetAsync("/cooling/layout"));
        Assert.Equal(0, body.GetProperty("rev").GetInt32());
        var layout = body.GetProperty("layout");
        Assert.Equal(JsonValueKind.Object, layout.GetProperty("directions").ValueKind);
        Assert.Equal(0, layout.GetProperty("bindings").GetArrayLength());
        Assert.Equal(0, layout.GetProperty("tests").GetArrayLength());
        Assert.False(File.Exists(Path.Combine(_factory.SceneDir, "cooling-layout.json")));
    }

    [Fact]
    public async Task Put_saves_and_get_round_trips_the_layout_across_a_reread_of_the_file()
    {
        var res = await PutLayout(0, Full);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, (await Body(res)).GetProperty("rev").GetInt32());
        Assert.True(File.Exists(Path.Combine(_factory.SceneDir, "cooling-layout.json")));

        var body = await Body(await Desktop().GetAsync("/cooling/layout"));
        Assert.Equal(1, body.GetProperty("rev").GetInt32());
        var layout = body.GetProperty("layout");
        Assert.Equal("exhaust", layout.GetProperty("directions").GetProperty("top-1").GetString());
        Assert.Equal(["top-1", "top-2"], layout.GetProperty("bindings")[0].GetProperty("slotKeys").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("rear-1", layout.GetProperty("tests")[0].GetProperty("moves")[0].GetProperty("to").GetString());

        var reread = new LightingSceneStore(_factory.SceneDir).Cooling.Load();
        Assert.Equal(1, reread.Rev);
        Assert.Equal("ch-1", reread.Layout.Bindings[0].ChannelId);
    }

    [Fact]
    public async Task Put_with_a_stale_base_rev_is_409_with_the_current_layout_and_changes_nothing()
    {
        await PutLayout(0, Full);

        var res = await PutLayout(0, Layout("""{"x":"intake"}"""));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await Body(res);
        Assert.Equal(1, body.GetProperty("rev").GetInt32());
        Assert.Equal("exhaust", body.GetProperty("layout").GetProperty("directions").GetProperty("top-1").GetString());
        Assert.Equal(1, Scene.Cooling.Load().Rev);
    }

    [Fact]
    public async Task Put_without_a_base_rev_or_with_a_malformed_body_is_400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Desktop().PutAsync("/cooling/layout", Json($$"""{"layout":{{Layout()}}}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Desktop().PutAsync("/cooling/layout", Json("{not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Desktop().PutAsync("/cooling/layout", Json("""{"baseRev":0}"""))).StatusCode);
    }

    [Fact]
    public async Task Unknown_direction_values_are_rejected_in_the_layout_and_in_a_test()
    {
        await AssertRejected(Layout("""{"a":"sideways"}"""));
        await AssertRejected(Layout("""{"a":""}"""));
        await AssertRejected(Layout(tests: """[{"id":"t","name":"n","moves":[],"directions":{"a":"up"}}]"""));
    }

    [Fact]
    public async Task Binding_caps_are_enforced_at_the_boundary()
    {
        string Bindings(int n, int keys) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i =>
            $$"""{"channelId":"c{{i}}","slotKeys":[{{Quoted(Enumerable.Range(0, keys).Select(k => "s" + k))}}]}""")) + "]";

        Assert.Equal(HttpStatusCode.OK, (await PutLayout(0, Layout(bindings: Bindings(64, 16)))).StatusCode);
        Scene.Cooling.Reset();
        await AssertRejected(Layout(bindings: Bindings(65, 1)));
        await AssertRejected(Layout(bindings: Bindings(1, 17)));
    }

    [Fact]
    public async Task Test_and_move_caps_are_enforced_at_the_boundary()
    {
        string Tests(int n, int moves) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i =>
            $$$"""{"id":"t{{{i}}}","name":"n","moves":[{{{string.Join(",", Enumerable.Repeat("""{"from":"a","to":"b"}""", moves))}}}],"directions":{}}""")) + "]";

        Assert.Equal(HttpStatusCode.OK, (await PutLayout(0, Layout(tests: Tests(16, 64)))).StatusCode);
        Scene.Cooling.Reset();
        await AssertRejected(Layout(tests: Tests(17, 0)));
        await AssertRejected(Layout(tests: Tests(1, 65)));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Id_and_key_lengths_cap_at_128(int length, bool ok)
    {
        var key = new string('k', length);
        var layouts = new[]
        {
            Layout($$"""{"{{key}}":"intake"}"""),
            Layout(bindings: $$"""[{"channelId":"{{key}}","slotKeys":[]}]"""),
            Layout(bindings: $$"""[{"channelId":"c","slotKeys":["{{key}}"]}]"""),
            Layout(tests: $$$"""[{"id":"{{{key}}}","name":"n","moves":[],"directions":{}}]"""),
            Layout(tests: $$$"""[{"id":"t","name":"n","moves":[{"from":"{{{key}}}","to":"b"}],"directions":{}}]"""),
            Layout(tests: $$$"""[{"id":"t","name":"n","moves":[{"from":"a","to":"{{{key}}}"}],"directions":{}}]"""),
        };
        foreach (var layout in layouts)
        {
            Scene.Cooling.Reset();
            var res = await PutLayout(0, layout);
            Assert.Equal(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task Test_names_cap_at_64(int length, bool ok)
    {
        var res = await PutLayout(0, Layout(tests: $$$"""[{"id":"t","name":"{{{new string('n', length)}}}","moves":[],"directions":{}}]"""));
        Assert.Equal(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Empty_ids_and_keys_are_rejected()
    {
        await AssertRejected(Layout(bindings: """[{"channelId":"","slotKeys":[]}]"""));
        await AssertRejected(Layout(bindings: """[{"channelId":"c","slotKeys":[""]}]"""));
        await AssertRejected(Layout(tests: """[{"id":"","name":"n","moves":[],"directions":{}}]"""));
    }

    [Fact]
    public void A_save_pings_the_cooling_layout_topic_with_the_new_rev()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-cooling-layout-topic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LightingSceneStore(dir);
            var hub = new MultiplexHub();
            CoolingLayoutRoutes.WireTopic(store.Cooling, hub);
            var captured = new List<(string Topic, string Json)>();
            hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, Encoding.UTF8.GetString(payload.ToArray())));
            using var sub = hub.AddTestSubscription(PanelTopics.CoolingLayout);

            store.Cooling.TrySave(0, new Nexus.Service.Cooling.CoolingLayout(), out _);
            store.Cooling.TrySave(0, new Nexus.Service.Cooling.CoolingLayout(), out _);
            store.Cooling.TrySave(1, new Nexus.Service.Cooling.CoolingLayout(), out _);

            Assert.Equal("cooling/layout", PanelTopics.CoolingLayout);
            Assert.Equal(2, captured.Count);
            Assert.StartsWith("{\"t\":\"cooling/layout\"", captured[0].Json);
            Assert.Contains("\"rev\":1", captured[0].Json);
            Assert.Contains("\"rev\":2", captured[1].Json);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* never created */ }
        }
    }
}
