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
            $$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":55,"z":66,"yaw":30},{"id":"case","x":-10,"z":20,"yaw":180}]}""");

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
            $$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":1,"z":1,"yaw":1},{"id":"ghost","x":2,"z":2,"yaw":2}]}""");

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
            $$"""{"baseRev":{{rev}},"places":[{"id":"new-item","x":1,"z":1,"yaw":1}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);

        // A patch that carries an items list, or a case, has no effect: the body shape has no such field.
        var smuggle = await Patch(Desktop(), "/system/setup/placements",
            $$"""{"baseRev":{{rev}},"places":[],"items":[],"setup":{{SetupJson(items: "[]")}}}""");
        Assert.Equal(HttpStatusCode.OK, smuggle.StatusCode);

        var now = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup");
        Assert.Equal(["kb", "mon"], now.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Patch_validates_places()
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
        Assert.Equal("duplicate_id", await Code($$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":1,"z":1,"yaw":1},{"id":"kb","x":2,"z":2,"yaw":2}]}"""));
        Assert.Equal("non_finite_number", await Code($$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":1e30,"z":1,"yaw":1}]}"""));
    }

    [Fact]
    public async Task Patch_with_a_stale_base_rev_is_409_with_the_current_record()
    {
        var rev = await Seed();

        var res = await Patch(Desktop(), "/system/setup/placements",
            $$"""{"baseRev":{{rev - 1}},"places":[{"id":"kb","x":1,"z":1,"yaw":1}]}""");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await Body(res);
        Assert.Equal(rev, body.GetProperty("rev").GetInt32());
        Assert.Equal(10, body.GetProperty("setup").GetProperty("items")[0].GetProperty("place").GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task Patch_moves_the_matching_resolved_objects_itself_and_ignores_a_resolved_list_in_the_body()
    {
        var rev = await Seed();
        var smuggled = """{"objects":[{"id":"extra","kind":"box","source":"user","position":[0,0,0],"yaw":0,"size":[1,1,1],"anchors":[]}]}""";

        var res = await Patch(Desktop(), "/system/setup/placements",
            $$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":77,"z":88,"yaw":15}],"resolved":{{smuggled}}}""");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var resolved = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup").GetProperty("resolved").GetProperty("objects");
        var only = Assert.Single(resolved.EnumerateArray());
        Assert.Equal("kb", only.GetProperty("id").GetString());
        Assert.Equal(77, only.GetProperty("position")[0].GetInt32());
        Assert.Equal(0, only.GetProperty("position")[1].GetInt32());
        Assert.Equal(88, only.GetProperty("position")[2].GetInt32());
        Assert.Equal(15, only.GetProperty("yaw").GetInt32());
        Assert.Equal(440, only.GetProperty("size")[0].GetInt32());
        Assert.Equal(1, only.GetProperty("anchors").GetArrayLength());
    }

    [Fact]
    public async Task Patch_checks_the_total_record_size_before_saving()
    {
        // A record over the cap can only exist through a path that skipped validation (a hand-edited file), so seed it directly.
        var big = new PcSetup();
        big.Items.Add(new PcSetupItem { Id = "kb", Kind = "keyboard", Place = new PcSetupPlace() });
        big.Parts = JsonDocument.Parse("\"" + new string('x', PcSetupValidation.MaxRecordBytes) + "\"").RootElement.Clone();
        Assert.True(Scene.Setup.TrySave(0, big, out var saved));

        var res = await Patch(Desktop(), "/system/setup/placements", $$"""{"baseRev":{{saved.Rev}},"places":[{"id":"kb","x":1,"z":1,"yaw":1}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("record_too_large", (await Body(res)).GetProperty("code").GetString());
        Assert.Equal(0f, Scene.Setup.Load().Setup.Items[0].Place!.X);
    }

    [Fact]
    public async Task Patching_an_l_desk_carries_its_return_object_and_rejects_the_return_id_as_a_place()
    {
        const string desk = """{"id":"desk","kind":"desk","source":"user","position":[0,0,0],"yaw":0,"size":[1600,30,800],"anchors":[]}""";
        const string ret = """{"id":"desk.return","kind":"desk","source":"user","position":[-1000,0,500],"yaw":0,"size":[600,30,1200],"anchors":[]}""";
        var items = """[{"id":"desk","kind":"desk","owned":true,"custom":{"name":"Desk","widthMm":1600,"depthMm":800,"shape":"l","returnWidthMm":600,"returnDepthMm":1200,"returnSide":"left"},"place":{"x":0,"z":0,"yaw":0}}]""";
        var put = await Desktop().PutAsync("/system/setup", Json(Put(0, SetupJson(items, $"[{desk},{ret}]"))));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // A quarter turn counter-clockwise from above: local +X goes to -Z, local +Z goes to +X.
        var res = await Patch(Desktop(), "/system/setup/placements", """{"baseRev":1,"places":[{"id":"desk","x":100,"z":200,"yaw":90}]}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var objs = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup").GetProperty("resolved").GetProperty("objects");
        var moved = objs.EnumerateArray().Single(o => o.GetProperty("id").GetString() == "desk.return");
        Assert.Equal(90, moved.GetProperty("yaw").GetInt32());
        Assert.Equal(100 + 500, Math.Round(moved.GetProperty("position")[0].GetDouble()));
        Assert.Equal(200 + 1000, Math.Round(moved.GetProperty("position")[2].GetDouble()));
        Assert.Equal(2, objs.GetArrayLength());

        var bad = await Patch(Desktop(), "/system/setup/placements", """{"baseRev":2,"places":[{"id":"desk.return","x":1,"z":1,"yaw":1}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("unknown_id", (await Body(bad)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_with_an_unknown_desk_shape_is_400()
    {
        var items = """[{"id":"desk","kind":"desk","custom":{"name":"Desk","shape":"u"}}]""";
        var res = await Desktop().PutAsync("/system/setup", Json(Put(0, SetupJson(items, "[]"))));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_item", (await Body(res)).GetProperty("code").GetString());
    }

    private const string CurvedMonitor = """
        {"id":"mon","kind":"monitor","source":"user","position":[0,0,-200],"yaw":0,"size":[800,450,60],"curve":{"radiusMm":1500},"anchors":[]}
        """;

    [Fact]
    public async Task A_monitor_curve_round_trips_through_put_and_scene_get()
    {
        var put = await Desktop().PutAsync("/system/setup", Json(Put(0, SetupJson(resolved: $"[{CurvedMonitor}]"))));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup").GetProperty("resolved").GetProperty("objects")[0];
        Assert.Equal(1500, get.GetProperty("curve").GetProperty("radiusMm").GetInt32());
        var scene = (await Body(await Desktop().GetAsync("/lighting/scene"))).GetProperty("objects")[0];
        Assert.Equal(1500, scene.GetProperty("curve").GetProperty("radiusMm").GetInt32());
    }

    [Fact]
    public async Task A_patch_moves_a_curved_monitor_and_keeps_its_curve()
    {
        const string items = """[{"id":"mon","kind":"monitor","owned":true,"custom":{"name":"Screen"},"place":{"x":0,"z":-200,"yaw":0}}]""";
        var put = await Desktop().PutAsync("/system/setup", Json(Put(0, SetupJson(items, $"[{CurvedMonitor}]"))));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var patch = await Patch(Desktop(), "/system/setup/placements", """{"baseRev":1,"places":[{"id":"mon","x":50,"z":-150,"yaw":20}]}""");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var obj = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup").GetProperty("resolved").GetProperty("objects")[0];
        Assert.Equal(50, obj.GetProperty("position")[0].GetInt32());
        Assert.Equal(20, obj.GetProperty("yaw").GetInt32());
        Assert.Equal(1500, obj.GetProperty("curve").GetProperty("radiusMm").GetInt32());
    }

    [Theory]
    [InlineData("monitor", "0")]
    [InlineData("monitor", "299")]
    [InlineData("monitor", "10001")]
    [InlineData("keyboard", "1500")]
    public async Task A_bad_curve_is_400_invalid_curve(string kind, string radius)
    {
        var obj = $$"""{"id":"o","kind":"{{kind}}","source":"user","position":[0,0,0],"yaw":0,"size":[800,450,60],"curve":{"radiusMm":{{radius}}},"anchors":[]}""";
        var res = await Desktop().PutAsync("/system/setup", Json(Put(0, SetupJson(resolved: $"[{obj}]"))));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_curve", (await Body(res)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_curve_bounds_are_accepted_and_a_scene_put_validates_curves_too()
    {
        foreach (var r in new[] { "300", "10000" })
        {
            var obj = CurvedMonitor.Replace("1500", r);
            Assert.Equal(HttpStatusCode.OK, (await Desktop().PutAsync("/lighting/scene", Json($$"""{"objects":[{{obj}}],"bindings":[]}"""))).StatusCode);
        }
        var bad = CurvedMonitor.Replace("1500", "50");
        Assert.Equal(HttpStatusCode.BadRequest, (await Desktop().PutAsync("/lighting/scene", Json($$"""{"objects":[{{bad}}],"bindings":[]}"""))).StatusCode);
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

        await Patch(client, "/system/setup/placements",
            $$"""{"baseRev":{{rev}},"places":[{"id":"kb","x":210,"z":150,"yaw":0}]}""");

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

    // ---- PATCH radiator ----

    private static string RadiatorSetup(string? mount) =>
        SetupJson().Replace("\"case\":{", "\"case\":{" + (mount is null ? "" : $"\"radiatorMount\":\"{mount}\",")); 

    [Fact]
    public async Task Radiator_patch_sets_only_the_mount_and_bumps_the_rev()
    {
        var rev = await Seed();
        var before = await Body(await Desktop().GetAsync("/system/setup"));

        var res = await Patch(Desktop(), "/system/setup/radiator", $$"""{"baseRev":{{rev}},"mount":"right"}""");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(rev + 1, (await Body(res)).GetProperty("rev").GetInt32());
        var after = await Body(await Desktop().GetAsync("/system/setup"));
        var b = before.GetProperty("setup");
        var a = after.GetProperty("setup");
        Assert.Equal("right", a.GetProperty("case").GetProperty("radiatorMount").GetString());
        Assert.Equal(b.GetProperty("items").GetRawText(), a.GetProperty("items").GetRawText());
        Assert.Equal(b.GetProperty("resolved").GetRawText(), a.GetProperty("resolved").GetRawText());
        Assert.Equal(b.GetProperty("parts").GetRawText(), a.GetProperty("parts").GetRawText());
        Assert.Equal(b.GetProperty("case").GetProperty("place").GetRawText(), a.GetProperty("case").GetProperty("place").GetRawText());
    }

    [Fact]
    public async Task Radiator_patch_with_a_stale_rev_is_409_with_the_current_record()
    {
        var rev = await Seed();

        var res = await Patch(Desktop(), "/system/setup/radiator", $$"""{"baseRev":{{rev - 1}},"mount":"top"}""");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await Body(res);
        Assert.Equal(rev, body.GetProperty("rev").GetInt32());
        Assert.False(body.GetProperty("setup").GetProperty("case").TryGetProperty("radiatorMount", out _));
    }

    [Fact]
    public async Task Radiator_patch_without_a_saved_record_is_400_no_record()
    {
        var res = await Patch(Desktop(), "/system/setup/radiator", """{"baseRev":0,"mount":"top"}""");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("no_record", (await Body(res)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("\"Top\"")]
    [InlineData("\"\"")]
    [InlineData("\"top2\"")]
    [InlineData("\"a b\"")]
    public async Task Radiator_patch_with_a_bad_mount_is_400_and_changes_nothing(string mount)
    {
        var rev = await Seed();

        var res = await Patch(Desktop(), "/system/setup/radiator", $$"""{"baseRev":{{rev}},"mount":{{mount}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(rev, (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("rev").GetInt32());
    }

    [Fact]
    public async Task Radiator_patch_with_a_mount_over_32_characters_is_400()
    {
        var rev = await Seed();

        var res = await Patch(Desktop(), "/system/setup/radiator", $$"""{"baseRev":{{rev}},"mount":"{{new string('a', 33)}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Radiator_patch_with_a_null_mount_clears_it()
    {
        await Seed();
        var set = await Patch(Desktop(), "/system/setup/radiator", """{"baseRev":1,"mount":"top"}""");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var res = await Patch(Desktop(), "/system/setup/radiator", """{"baseRev":2,"mount":null}""");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var setup = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup");
        Assert.False(setup.GetProperty("case").TryGetProperty("radiatorMount", out _));
    }

    [Fact]
    public async Task Radiator_patch_requires_a_base_rev_and_the_desktop_token()
    {
        await Seed();

        var missing = await Patch(Desktop(), "/system/setup/radiator", """{"mount":"top"}""");
        var anon = await Patch(_factory.CreateClient(), "/system/setup/radiator", """{"baseRev":1,"mount":"top"}""");

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }

    [Fact]
    public async Task Put_round_trips_the_radiator_mount_and_rejects_a_bad_one()
    {
        var ok = await Desktop().PutAsync("/system/setup", Json(Put(0, RadiatorSetup("top"))));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var setup = (await Body(await Desktop().GetAsync("/system/setup"))).GetProperty("setup");
        Assert.Equal("top", setup.GetProperty("case").GetProperty("radiatorMount").GetString());

        var bad = await Desktop().PutAsync("/system/setup", Json(Put(1, RadiatorSetup("Top-1"))));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public void A_radiator_patch_pings_the_system_setup_topic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-pc-setup-radiator-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LightingSceneStore(dir);
            store.Setup.TrySave(0, new PcSetup(), out _);
            var hub = new MultiplexHub();
            PcSetupRoutes.WireTopic(store.Setup, hub);
            var captured = new List<string>();
            hub.OnBroadcastForTest += (_, payload) => captured.Add(Encoding.UTF8.GetString(payload.ToArray()));
            using var sub = hub.AddTestSubscription(PanelTopics.SystemSetup);
            var next = store.Setup.Load().Setup;
            next.Case.RadiatorMount = "top";

            store.Setup.TrySave(1, next, out _);

            Assert.Single(captured);
            Assert.Contains("\"rev\":2", captured[0]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* never created */ }
        }
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

public sealed class PcSetupProfileFileTests : IClassFixture<LightingSceneAppFactory>
{
    private readonly LightingSceneAppFactory _factory;

    public PcSetupProfileFileTests(LightingSceneAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        Scene.Setup.Reset();
    }

    private LightingSceneStore Scene => _factory.Services.GetRequiredService<LightingSceneStore>();

    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static PcSetup Setup(string id)
    {
        var s = new PcSetup();
        s.Items.Add(new PcSetupItem { Id = id, Kind = "keyboard", Custom = new PcSetupCustom { Name = "Keys" } });
        return s;
    }

    private async Task<(string Json, string ProfileId)> Export()
    {
        var client = Desktop();
        var created = await client.PostAsync("/profiles/create", new StringContent($$"""{"name":"SetupFile{{Guid.NewGuid():N}}"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var made = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = made.RootElement.GetProperty("profile").GetProperty("id").GetString()!;
        var json = await client.GetStringAsync($"/profiles/{id}/export");
        await client.DeleteAsync($"/profiles/{id}");
        return (json, id);
    }

    private static string Rename(string json, string name)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["name"] = name;
        return node.ToJsonString();
    }

    private async Task<JsonElement> Import(string json, string name, bool replace = true)
    {
        var res = await Desktop().PostAsync("/profiles/import" + (replace ? "?replace=true" : ""), new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        // The shared host caps the profile library, so each import cleans up after itself.
        await Desktop().DeleteAsync("/profiles/" + doc.RootElement.GetProperty("profile").GetProperty("id").GetString());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task An_exported_file_carries_the_record_and_this_machine_and_imports_back_here()
    {
        Scene.Setup.TrySave(0, Setup("kb"), out _);
        var (json, _) = await Export();
        using var doc = JsonDocument.Parse(json);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("installDigest").GetString()));
        Assert.Equal("kb", doc.RootElement.GetProperty("appData").GetProperty("nexus.pc-setup").GetProperty("setup").GetProperty("items")[0].GetProperty("id").GetString());

        Scene.Setup.TrySave(1, Setup("changed"), out _);
        var res = await Import(Rename(json, "Copy"), "copy");

        Assert.Equal("applied", res.GetProperty("pcSetup").GetString());
        Assert.Equal(["kb"], Scene.Setup.Load().Setup.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task The_file_names_the_machine_by_a_digest_never_the_install_id()
    {
        Scene.Setup.TrySave(0, Setup("kb"), out _);
        var (json, _) = await Export();
        var installId = _factory.Services.GetRequiredService<Nexus.Service.Cloud.CloudAccountService>().ResolveStableInstallId();

        Assert.DoesNotContain(installId, json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(Nexus.Service.Cloud.PcSetupBackup.MachineDigest(installId), doc.RootElement.GetProperty("installDigest").GetString());
        Assert.NotEqual(installId, doc.RootElement.GetProperty("installDigest").GetString());
    }

    [Fact]
    public async Task A_file_imported_as_a_new_profile_never_touches_the_machine_setup()
    {
        Scene.Setup.TrySave(0, Setup("mine"), out _);
        var (json, _) = await Export();
        Scene.Setup.TrySave(1, Setup("changed"), out _);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["name"] = "Fresh";
        node.Remove("installDigest");

        var res = await Import(node.ToJsonString(), "fresh", replace: false);

        Assert.Equal("skipped_new_profile", res.GetProperty("pcSetup").GetString());
        Assert.Equal(["changed"], Scene.Setup.Load().Setup.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_file_from_another_machine_skips_the_record_and_one_naming_no_machine_applies_and_reports_it()
    {
        Scene.Setup.TrySave(0, Setup("mine"), out _);
        var (json, _) = await Export();
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["name"] = "Other";
        node["installDigest"] = "some-other-install";
        var other = await Import(node.ToJsonString(), "other");
        Assert.Equal("skipped_other_machine", other.GetProperty("pcSetup").GetString());
        Assert.Equal(["mine"], Scene.Setup.Load().Setup.Items.Select(i => i.Id));

        node["name"] = "Anon";
        node.Remove("installDigest");
        node["appData"]!["nexus.pc-setup"]!["setup"]!["items"]![0]!["id"] = "anon";
        var anon = await Import(node.ToJsonString(), "anon");
        Assert.Equal("applied_unknown_machine", anon.GetProperty("pcSetup").GetString());
        Assert.Equal(["anon"], Scene.Setup.Load().Setup.Items.Select(i => i.Id));
    }
}
