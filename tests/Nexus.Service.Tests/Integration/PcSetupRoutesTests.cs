using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

public sealed class PcSetupRoutesTests : IClassFixture<LightingSceneAppFactory>
{
    private readonly LightingSceneAppFactory _factory;

    public PcSetupRoutesTests(LightingSceneAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        Scene.Save(new LightingSceneDoc());
        Scene.Setup.Reset();
    }

    private LightingSceneStore Scene => _factory.Services.GetRequiredService<LightingSceneStore>();
    private LightingEngine Engine => _factory.Services.GetRequiredService<LightingEngine>();

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

    private static Task<HttpResponseMessage> Patch(HttpClient c, string path, string body) =>
        c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, path) { Content = Json(body) });

    private const string KbObject = """
        {"id":"kb","kind":"keyboard","source":"user","position":[10,0,150],"yaw":0,"size":[440,35,140],
         "anchors":[{"id":"top","kind":"surface","center":[0,35,0],"right":[1,0,0],"up":[0,0,-1],"width":440,"height":140,"shape":"rect"}]}
        """;

    private static string SetupJson(string items = """[{"id":"kb","kind":"keyboard","owned":true,"custom":{"name":"Keys","widthMm":440,"depthMm":140},"place":{"x":10,"z":150,"yaw":0}},{"id":"mon","kind":"monitor","owned":true,"custom":{"name":"Screen"},"place":{"x":0,"z":-200,"yaw":0}}]""", string resolved = "[" + KbObject + "]")
        => $$$"""{"version":1,"case":{"place":{"x":-300,"z":40,"yaw":90}},"parts":{"corrections":{"a":1},"more":[]},"items":{{{items}}},"resolved":{"objects":{{{resolved}}}}}""";

    private static string Put(int baseRev, string setup) => $$"""{"baseRev":{{baseRev}},"setup":{{setup}}}""";

    private async Task<int> Seed()
    {
        var res = await Desktop().PutAsync("/system/setup", Json(Put(0, SetupJson())));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await Body(res)).GetProperty("rev").GetInt32();
    }

    // ---- auth ----

    [Fact]
    public async Task Every_route_needs_the_desktop_token()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/system/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/system/setup", Json(Put(0, SetupJson())))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Patch(anon, "/system/setup/placements", """{"baseRev":0,"places":[]}""")).StatusCode);
    }

    // ---- GET / PUT ----

    [Fact]
    public async Task Get_is_rev_zero_with_an_empty_setup_when_nothing_is_saved()
    {
        var body = await Body(await Desktop().GetAsync("/system/setup"));
        Assert.Equal(0, body.GetProperty("rev").GetInt32());
        Assert.Equal(0, body.GetProperty("setup").GetProperty("items").GetArrayLength());
        Assert.Equal(1, body.GetProperty("setup").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Get_reads_the_migrated_scene_at_rev_zero_before_any_save()
    {
        var doc = new LightingSceneDoc();
        doc.Objects.Add(new SceneObject { Id = "old-kb", Kind = "keyboard", Source = "user", Position = [5f, 0f, 9f], Label = "Old keys" });
        Scene.Save(doc);

        var body = await Body(await Desktop().GetAsync("/system/setup"));

        Assert.Equal(0, body.GetProperty("rev").GetInt32());
        Assert.Equal("old-kb", body.GetProperty("setup").GetProperty("items")[0].GetProperty("id").GetString());
        Assert.False(File.Exists(Path.Combine(_factory.SceneDir, "pc-setup.json")));
    }

    [Fact]
    public async Task Put_saves_returns_the_new_rev_and_get_round_trips_it_with_parts_untouched()
    {
        var rev = await Seed();
        Assert.Equal(1, rev);

        var body = await Body(await Desktop().GetAsync("/system/setup"));
        Assert.Equal(1, body.GetProperty("rev").GetInt32());
        var setup = body.GetProperty("setup");
        Assert.Equal(2, setup.GetProperty("items").GetArrayLength());
        Assert.Equal("""{"corrections":{"a":1},"more":[]}""", setup.GetProperty("parts").GetRawText());
        Assert.Equal(90, setup.GetProperty("case").GetProperty("place").GetProperty("yaw").GetInt32());

        var again = await Desktop().PutAsync("/system/setup", Json(Put(1, SetupJson())));
        Assert.Equal(2, (await Body(again)).GetProperty("rev").GetInt32());
    }

    [Fact]
    public async Task Put_with_a_stale_base_rev_is_409_with_the_current_record()
    {
        await Seed();

        var res = await Desktop().PutAsync("/system/setup", Json(Put(0, SetupJson(items: "[]", resolved: "[]"))));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await Body(res);
        Assert.Equal(1, body.GetProperty("rev").GetInt32());
        Assert.Equal(2, body.GetProperty("setup").GetProperty("items").GetArrayLength());
        Assert.Equal(2, (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Put_rejects_bad_records_with_400_and_a_code()
    {
        var client = Desktop();
        async Task<string> Code(string body)
        {
            var res = await client.PutAsync("/system/setup", Json(body));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var json = await Body(res);
            Assert.True(json.GetProperty("error").GetBoolean());
            Assert.False(string.IsNullOrEmpty(json.GetProperty("msg").GetString()));
            return json.GetProperty("code").GetString()!;
        }

        Assert.Equal("base_rev_required", await Code($$"""{"setup":{{SetupJson()}}}"""));
        Assert.Equal("setup_required", await Code("""{"baseRev":0}"""));
        Assert.Equal("bad_body", await Code("{ not json"));
        Assert.Equal("duplicate_id", await Code(Put(0, SetupJson(items: """[{"id":"a","kind":"keyboard"},{"id":"a","kind":"mouse"}]"""))));
        var many = string.Join(',', Enumerable.Range(0, PcSetupValidation.MaxItems + 1).Select(i => $$"""{"id":"i{{i}}","kind":"monitor"}"""));
        Assert.Equal("too_many_items", await Code(Put(0, SetupJson(items: $"[{many}]"))));
        Assert.Equal("string_too_long", await Code(Put(0, SetupJson(items: "[{\"id\":\"a\",\"kind\":\"keyboard\",\"custom\":{\"name\":\"" + new string('x', 200) + "\"}}]"))));
        Assert.Equal("non_finite_number", await Code(Put(0, SetupJson(items: """[{"id":"a","kind":"keyboard","place":{"x":1e30,"z":0,"yaw":0}}]"""))));
        var bigParts = new string('x', PcSetupValidation.MaxOpaqueParts);
        Assert.Equal("value_too_large", await Code(Put(0, SetupJson().Replace("""{"corrections":{"a":1},"more":[]}""", $"\"{bigParts}\""))));
        Assert.Equal("invalid_resolved", await Code(Put(0, SetupJson(resolved: """[{"id":"bad id","kind":"box","source":"user","position":[0,0,0],"yaw":0,"size":[1,1,1],"anchors":[]}]"""))));

        Assert.Equal(0, (await Body(await client.GetAsync("/system/setup"))).GetProperty("rev").GetInt32());
    }

    // ---- PATCH placements ----

    private static string Resolved(string objects = "[" + KbObject + "]") => $$"""{"objects":{{objects}}}""";

    [Fact]
    public async Task Patch_changes_x_z_and_yaw_of_existing_items_and_the_case_and_nothing_else()
    {
        var rev = await Seed();
        var before = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup");

        var res = await Patch(Desktop(), "/system/setup/placements",
            $$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":55,"z":66,"yaw":30},{"id":"case","x":-10,"z":20,"yaw":180}],"resolved":{{Resolved()}}}""");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(rev + 1, (await Body(res)).GetProperty("rev").GetInt32());
        var after = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup");
        var kb = after.GetProperty("items")[0];
        Assert.Equal(55, kb.GetProperty("place").GetProperty("x").GetInt32());
        Assert.Equal(66, kb.GetProperty("place").GetProperty("z").GetInt32());
        Assert.Equal(30, kb.GetProperty("place").GetProperty("yaw").GetInt32());
        Assert.Equal(180, after.GetProperty("case").GetProperty("place").GetProperty("yaw").GetInt32());
        // Everything but the placements is byte-identical.
        Assert.Equal(before.GetProperty("items")[1].GetRawText(), after.GetProperty("items")[1].GetRawText());
        Assert.Equal(before.GetProperty("parts").GetRawText(), after.GetProperty("parts").GetRawText());
        Assert.Equal(before.GetProperty("items")[0].GetProperty("custom").GetRawText(), kb.GetProperty("custom").GetRawText());
        Assert.Equal(before.GetProperty("items").GetArrayLength(), after.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Patch_rejects_an_unknown_id_and_writes_nothing()
    {
        var rev = await Seed();

        var res = await Patch(Desktop(), "/system/setup/placements",
            $$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":1,"z":1,"yaw":1},{"id":"ghost","x":2,"z":2,"yaw":2}],"resolved":{{Resolved()}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("unknown_id", (await Body(res)).GetProperty("code").GetString());
        var now = await Body(await Desktop().GetAsync("/system/setup"));
        Assert.Equal(rev, now.GetProperty("rev").GetInt32());
        Assert.Equal(10, now.GetProperty("setup").GetProperty("items")[0].GetProperty("place").GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task Patch_cannot_add_or_remove_items()
    {
        var rev = await Seed();

        // An item in the body that is not in the record is just an unknown id; nothing is created.
        var add = await Patch(Desktop(), "/system/setup/placements",
            $$"""{"baseRev":{{rev}},"places":[{"id":"new-item","x":1,"z":1,"yaw":1}],"resolved":{{Resolved()}}}""");
        Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);

        // A patch that carries an items list, or a case, has no effect: the body shape has no such field.
        var smuggle = await Patch(Desktop(), "/system/setup/placements",
            $$"""{"baseRev":{{rev}},"places":[],"items":[],"setup":{{SetupJson(items: "[]")}}}""");
        Assert.Equal(HttpStatusCode.OK, smuggle.StatusCode);

        var now = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup");
        Assert.Equal(["kb", "mon"], now.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Patch_validates_places_and_resolved()
    {
        var rev = await Seed();
        var client = Desktop();
        async Task<string> Code(string body)
        {
            var res = await Patch(client, "/system/setup/placements", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return (await Body(res)).GetProperty("code").GetString()!;
        }

        Assert.Equal("base_rev_required", await Code("""{"places":[]}"""));
        Assert.Equal("resolved_required", await Code($$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":1,"z":1,"yaw":1}]}"""));
        Assert.Equal("duplicate_id", await Code($$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":1,"z":1,"yaw":1},{"id":"kb","x":2,"z":2,"yaw":2}],"resolved":{{Resolved()}}}"""));
        Assert.Equal("non_finite_number", await Code($$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":1e30,"z":1,"yaw":1}],"resolved":{{Resolved()}}}"""));
        Assert.Equal("invalid_resolved", await Code($$$"""{"baseRev":{{{rev}}},"places":[],"resolved":{"objects":[{"id":"x y","kind":"box","source":"user","position":[0,0,0],"yaw":0,"size":[1,1,1],"anchors":[]}]}}"""));
    }

    [Fact]
    public async Task Patch_with_a_stale_base_rev_is_409_with_the_current_record()
    {
        var rev = await Seed();

        var res = await Patch(Desktop(), "/system/setup/placements",
            $$"""{"baseRev":{{rev - 1}},"places":[{"id":"kb","x":1,"z":1,"yaw":1}],"resolved":{{Resolved()}}}""");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await Body(res);
        Assert.Equal(rev, body.GetProperty("rev").GetInt32());
        Assert.Equal(10, body.GetProperty("setup").GetProperty("items")[0].GetProperty("place").GetProperty("x").GetInt32());
    }

    // ---- lighting reads from the record ----

    [Fact]
    public async Task Scene_get_falls_back_to_the_scene_files_objects_when_no_record_is_saved()
    {
        var doc = new LightingSceneDoc();
        doc.Objects.Add(new SceneObject { Id = "legacy", Kind = "keyboard", Source = "user" });
        Scene.Save(doc);

        var body = await Body(await Desktop().GetAsync("/lighting/scene"));

        Assert.Equal(["legacy"], body.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Scene_get_returns_the_records_resolved_objects_and_keeps_bindings_by_object_id()
    {
        var client = Desktop();
        var doc = new LightingSceneDoc();
        doc.Objects.Add(new SceneObject { Id = "legacy", Kind = "keyboard", Source = "user" });
        doc.Bindings.Add(new SceneBinding { DeviceId = "kbd-1", Targets = [new SceneTarget { ObjectId = "kb", AnchorId = "top" }] });
        Scene.Save(doc);
        await Seed();

        var body = await Body(await client.GetAsync("/lighting/scene"));

        Assert.Equal(["kb"], body.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.Equal("kbd-1", body.GetProperty("bindings")[0].GetProperty("deviceId").GetString());
        // The scene file keeps its own objects.
        Assert.Equal(["legacy"], Scene.Load().Objects.Select(o => o.Id));
    }

    [Fact]
    public async Task A_record_save_reapplies_the_scene_to_the_engine_and_a_patch_moves_it()
    {
        var client = Desktop();
        var doc = new LightingSceneDoc();
        doc.Bindings.Add(new SceneBinding { DeviceId = "kbd-1", Targets = [new SceneTarget { ObjectId = "kb", AnchorId = "top" }] });
        Scene.Save(doc);
        await client.PutAsync("/lighting/scene/view", Json("""{"enabled":true,"camera":{"position":[0,600,1200],"target":[0,0,0],"fov":40}}"""));
        Assert.Null(Engine.Scene);

        var rev = await Seed();
        Assert.NotNull(Engine.Scene);
        var before = Engine.Scene!.Placements["kbd-1"][0].Center;

        var moved = KbObject.Replace("\"position\":[10,0,150]", "\"position\":[210,0,150]");
        await Patch(client, "/system/setup/placements",
            $$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":210,"z":150,"yaw":0}],"resolved":{{Resolved("[" + moved + "]")}}}""");

        Assert.Equal(before.X + 200f, Engine.Scene!.Placements["kbd-1"][0].Center.X, 3);
    }

    [Fact]
    public async Task A_binding_save_with_a_record_keeps_the_records_objects_and_stores_the_bindings()
    {
        var client = Desktop();
        await Seed();

        var res = await client.PutAsync("/lighting/scene",
            Json("""{"objects":[],"bindings":[{"deviceId":"kbd-1","targets":[{"objectId":"kb","anchorId":"top"}],"rotation":0,"flip":false}]}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Body(res);
        Assert.Equal(["kb"], body.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.Equal("kbd-1", body.GetProperty("bindings")[0].GetProperty("deviceId").GetString());
    }

    // ---- topic ----

    [Fact]
    public void A_save_pings_the_system_setup_topic_with_the_new_rev()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-pc-setup-topic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LightingSceneStore(dir);
            var hub = new MultiplexHub();
            PcSetupRoutes.WireTopic(store.Setup, hub);
            var captured = new List<(string Topic, string Json)>();
            hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, Encoding.UTF8.GetString(payload.ToArray())));
            using var sub = hub.AddTestSubscription(PanelTopics.SystemSetup);

            store.Setup.TrySave(0, new PcSetup(), out _);
            store.Setup.Replace(new PcSetup());

            Assert.Equal("system/setup", PanelTopics.SystemSetup);
            Assert.Equal(2, captured.Count);
            Assert.StartsWith("{\"t\":\"system/setup\"", captured[0].Json);
            Assert.Contains("\"rev\":1", captured[0].Json);
            Assert.Contains("\"rev\":2", captured[1].Json);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* never created */ }
        }
    }
}
