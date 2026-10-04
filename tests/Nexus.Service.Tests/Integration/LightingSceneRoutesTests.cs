using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>Throwaway scene directory, and the scene service started so view and scene writes reach the engine.</summary>
public sealed class LightingSceneAppFactory : NexusAppFactory
{
    public string SceneDir { get; } = Path.Combine(Path.GetTempPath(), "nexus-scene-itest-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<LightingSceneStore>();
            services.AddSingleton(new LightingSceneStore(SceneDir));
            services.RemoveAll<Nexus.Service.Devices.ILightingDeviceProvider>();
            services.AddSingleton<Nexus.Service.Devices.ILightingDeviceProvider>(sp =>
                new Nexus.Service.Devices.StubDeviceProvider(sp.GetRequiredService<IConfigStore>()));
            services.AddHostedService(sp => sp.GetRequiredService<LightingSceneService>());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(SceneDir, recursive: true); } catch { /* already gone */ }
    }
}

public sealed class LightingSceneRoutesTests : IClassFixture<LightingSceneAppFactory>
{
    private readonly LightingSceneAppFactory _factory;

    public LightingSceneRoutesTests(LightingSceneAppFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        Scene.SaveModel(null);
        Scene.Save(new LightingSceneDoc());
        _factory.Services.GetRequiredService<LightingSceneService>().ClearDraft();
    }

    private LightingSceneStore Scene => _factory.Services.GetRequiredService<LightingSceneStore>();
    private LightingEngine Engine => _factory.Services.GetRequiredService<LightingEngine>();
    private IConfigStore Config => _factory.Services.GetRequiredService<IConfigStore>();

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

    private const string Keyboard = """
        {"id":"kb","kind":"keyboard","source":"user","position":[0,0,150],"yaw":0,"size":[440,35,140],
         "anchors":[{"id":"top","kind":"surface","center":[0,35,0],"right":[1,0,0],"up":[0,0,-1],"width":440,"height":140,"shape":"rect"}]}
        """;

    private static string SceneBody(string objects, string bindings) => $$"""{"objects":[{{objects}}],"bindings":[{{bindings}}]}""";

    private const string KeyboardBinding = """{"deviceId":"kbd-1","targets":[{"objectId":"kb","anchorId":"top"}],"rotation":0,"flip":false}""";

    private const string FrontCamera = """{"position":[0,600,1200],"target":[0,0,0],"fov":40}""";

    private static byte[] Glb(string json)
    {
        while (Encoding.UTF8.GetByteCount(json) % 4 != 0) json += " ";
        var chunk = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[20 + chunk.Length];
        BitConverter.GetBytes(0x46546C67u).CopyTo(bytes, 0);
        BitConverter.GetBytes(2u).CopyTo(bytes, 4);
        BitConverter.GetBytes((uint)bytes.Length).CopyTo(bytes, 8);
        BitConverter.GetBytes((uint)chunk.Length).CopyTo(bytes, 12);
        BitConverter.GetBytes(0x4E4F534Au).CopyTo(bytes, 16);
        chunk.CopyTo(bytes, 20);
        return bytes;
    }

    private static string CaseObject(string anchorId = "fan:front:140:0") => $$"""
        {"id":"case","kind":"case","position":[0,0,0],"yaw":0,"size":[230,480,460],
         "anchors":[{"id":"{{anchorId}}","kind":"fan","center":[0,240,230],"right":[1,0,0],"up":[0,1,0],"width":140,"height":140,"shape":"ring"}]}
        """;

    [Fact]
    public async Task Every_verb_requires_the_desktop_token()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/lighting/scene")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/lighting/scene", Json(SceneBody("", "")))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/lighting/scene/view", Json("""{"enabled":true}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/lighting/scene/model")).StatusCode);
    }

    [Fact]
    public async Task Fresh_scene_is_empty_with_the_view_off()
    {
        var body = await Body(await Desktop().GetAsync("/lighting/scene"));
        Assert.Equal(0, body.GetProperty("objects").GetArrayLength());
        Assert.Equal(0, body.GetProperty("bindings").GetArrayLength());
        Assert.False(body.GetProperty("view").GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Desktop().GetAsync("/lighting/scene/model")).StatusCode);
    }

    [Fact]
    public async Task Put_persists_to_the_scene_file_not_settings()
    {
        var res = await Desktop().PutAsync("/lighting/scene", Json(SceneBody(Keyboard, KeyboardBinding)));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(Scene.Load().Objects);
        Assert.True(File.Exists(Path.Combine(_factory.SceneDir, "lighting-scene.json")));
        Config.FlushNow();
        Assert.DoesNotContain("\"kbd-1\"", File.Exists(_factory.SettingsPath) ? File.ReadAllText(_factory.SettingsPath) : "");
    }

    [Theory]
    [InlineData("""{"id":"a b","kind":"box","source":"user","position":[0,0,0],"yaw":0,"size":[1,1,1],"anchors":[]}""")]
    [InlineData("""{"id":"a","kind":"box","source":"user","position":[0,0],"yaw":0,"size":[1,1,1],"anchors":[]}""")]
    [InlineData("""{"id":"a","kind":"box","source":"user","position":[0,0,0],"yaw":0,"size":[0,1,1],"anchors":[]}""")]
    [InlineData("""{"id":"a","kind":"box","source":"cloud","position":[0,0,0],"yaw":0,"size":[1,1,1],"anchors":[]}""")]
    [InlineData("""{"id":"a","kind":"box","source":"user","position":[0,0,0],"yaw":0,"size":[1,1,1],"anchors":[{"id":"x","kind":"fan","center":[0,0,0],"right":[0,0,0],"up":[0,1,0],"width":1,"height":1,"shape":"ring"}]}""")]
    public async Task Put_rejects_malformed_objects(string obj)
    {
        var res = await Desktop().PutAsync("/lighting/scene", Json(SceneBody(obj, "")));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(Scene.Load().Objects);
    }

    [Fact]
    public async Task Put_rejects_two_bindings_for_one_device()
    {
        var res = await Desktop().PutAsync("/lighting/scene", Json(SceneBody(Keyboard, KeyboardBinding + "," + KeyboardBinding)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Committed_view_reaches_the_engine_and_turning_it_off_clears_it()
    {
        var client = Desktop();
        await client.PutAsync("/lighting/scene", Json(SceneBody(Keyboard, KeyboardBinding)));
        Assert.Null(Engine.Scene);

        var on = await client.PutAsync("/lighting/scene/view", Json($$"""{"enabled":true,"camera":{{FrontCamera}}}"""));
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.NotNull(Engine.Scene);
        Assert.True(Engine.Scene!.Placements.ContainsKey("kbd-1"));
        Assert.True(Config.Load().Lighting.SceneView.Enabled);

        await client.PutAsync("/lighting/scene/view", Json("""{"enabled":false}"""));
        Assert.Null(Engine.Scene);
        // The camera survives the view going off.
        Assert.NotNull(Config.Load().Lighting.SceneView.Camera);
    }

    [Fact]
    public async Task Draft_camera_moves_the_engine_without_saving_and_a_commit_clears_it()
    {
        var client = Desktop();
        await client.PutAsync("/lighting/scene", Json(SceneBody(Keyboard, KeyboardBinding)));
        await client.PutAsync("/lighting/scene/view", Json($$"""{"enabled":true,"camera":{{FrontCamera}}}"""));

        var draft = await client.PutAsync("/lighting/scene/view", Json("""{"draft":true,"camera":{"position":[900,300,0],"target":[0,0,0],"fov":30}}"""));
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        Assert.Equal(900f, Engine.Scene!.Camera.Position.X);
        Assert.Equal(0f, Config.Load().Lighting.SceneView.Camera!.Position[0]);

        await client.PutAsync("/lighting/scene/view", Json("""{"enabled":true}"""));
        Assert.Equal(0f, Engine.Scene!.Camera.Position.X);
    }

    [Theory]
    [InlineData("""{"draft":true}""")]
    [InlineData("""{"camera":{"position":[0,0,0],"target":[0,0,0],"fov":200}}""")]
    [InlineData("""{"camera":{"position":[0,0],"target":[0,0,0],"fov":40}}""")]
    public async Task View_rejects_bad_cameras(string body)
    {
        var res = await Desktop().PutAsync("/lighting/scene/view", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Import_stores_the_model_keeps_hand_placed_objects_and_the_moved_case()
    {
        var client = Desktop();
        var model = Glb("""{"asset":{"version":"2.0"}}""");
        var first = await client.PutAsync("/lighting/scene/import", Json($$"""{"caseId":"case-123","objects":[{{CaseObject()}}],"modelBase64":"{{Convert.ToBase64String(model)}}"}"""));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The user moves the case on the desk and adds a keyboard bound to its surface.
        var scene = Scene.Load();
        var moved = scene.Objects.Single(o => o.Id == "case");
        Assert.True(moved.HasModel);
        Assert.Equal("build", moved.Source);
        var objects = $$"""{{CaseObject().Replace("\"position\":[0,0,0]", "\"position\":[-500,0,0]").Replace("\"kind\":\"case\",", "\"kind\":\"case\",\"source\":\"build\",")}},{{Keyboard}}""";
        var fanBinding = """{"deviceId":"fan-1","targets":[{"objectId":"case","anchorId":"fan:front:140:0"}]}""";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/lighting/scene", Json(SceneBody(objects, KeyboardBinding + "," + fanBinding)))).StatusCode);

        // A re-export whose front fan slot moved to another key.
        var second = await client.PutAsync("/lighting/scene/import", Json($$"""{"caseId":"case-123","objects":[{{CaseObject("fan:front:120:0")}}],"modelBase64":"{{Convert.ToBase64String(model)}}"}"""));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var after = Scene.Load();
        Assert.Equal(-500f, after.Objects.Single(o => o.Id == "case").Position[0]);
        Assert.Contains(after.Objects, o => o.Id == "kb");
        Assert.Contains(after.Bindings, b => b.DeviceId == "kbd-1");
        Assert.DoesNotContain(after.Bindings, b => b.DeviceId == "fan-1");

        var get = await client.GetAsync("/lighting/scene/model");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("model/gltf-binary", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal(model, await get.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("""{"asset":{"version":"2.0"},"buffers":[{"byteLength":4,"uri":"https://example.com/x.bin"}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"images":[{"uri":"data:image/png;base64,AAAA"}]}""")]
    public async Task Import_refuses_a_model_that_points_outside_itself(string json)
    {
        var res = await Desktop().PutAsync("/lighting/scene/import", Json($$"""{"objects":[],"modelBase64":"{{Convert.ToBase64String(Glb(json))}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Null(Scene.LoadModel());
    }

    [Fact]
    public async Task Import_refuses_bytes_that_are_not_a_gltf_binary()
    {
        var res = await Desktop().PutAsync("/lighting/scene/import", Json($$"""{"objects":[],"modelBase64":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes("definitely not a model file"))}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Delete_import_removes_the_case_and_its_model_and_keeps_desk_objects()
    {
        var client = Desktop();
        await client.PutAsync("/lighting/scene/import", Json($$"""{"objects":[{{CaseObject()}}],"modelBase64":"{{Convert.ToBase64String(Glb("""{"asset":{"version":"2.0"}}"""))}}"}"""));
        var doc = Scene.Load();
        doc.Objects.Add(JsonSerializer.Deserialize(Keyboard, Nexus.Service.Serialization.PersistenceJsonContext.Default.SceneObject)!);
        Scene.Save(doc);

        var res = await client.DeleteAsync("/lighting/scene/import");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(["kb"], Scene.Load().Objects.Select(o => o.Id));
        Assert.Null(Scene.LoadModel());
    }

    [Fact]
    public async Task Layout_preset_captures_the_view_and_activating_it_restores_it()
    {
        var client = Desktop();
        await client.PutAsync("/lighting/scene", Json(SceneBody(Keyboard, KeyboardBinding)));
        await client.PutAsync("/lighting/scene/view", Json($$"""{"enabled":true,"camera":{{FrontCamera}}}"""));
        var created = await Body(await client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"Desk view"}""")));
        var id = created.GetProperty("preset").GetProperty("id").GetString();

        await client.PutAsync("/lighting/scene/view", Json("""{"enabled":false}"""));
        Assert.Null(Engine.Scene);

        var activate = await client.PostAsync($"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        Assert.True(Config.Load().Lighting.SceneView.Enabled);
        Assert.NotNull(Engine.Scene);
    }
}
