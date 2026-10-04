using Nexus.Service.Lighting.Scene;

namespace Nexus.Service.Tests.Lighting.Scene;

public sealed class LightingSceneStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-scene-store-" + Guid.NewGuid().ToString("N"));

    public LightingSceneStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* already gone */ }
    }

    [Fact]
    public void A_hand_edited_file_with_null_lists_loads_as_empty_lists()
    {
        File.WriteAllText(Path.Combine(_dir, "lighting-scene.json"),
            """{"objects":[null,{"id":"kb","kind":"keyboard","anchors":null}],"bindings":[{"deviceId":"d","targets":null},null]}""");

        var doc = new LightingSceneStore(_dir).Load();

        Assert.Equal(["kb"], doc.Objects.Select(o => o.Id));
        Assert.Empty(doc.Objects[0].Anchors);
        Assert.Single(doc.Bindings);
        Assert.Empty(doc.Bindings[0].Targets);
        Assert.Empty(SceneMath.Placements(doc));
    }

    [Fact]
    public void A_corrupt_file_reads_as_an_empty_scene()
    {
        File.WriteAllText(Path.Combine(_dir, "lighting-scene.json"), "{ not json");
        Assert.Empty(new LightingSceneStore(_dir).Load().Objects);
    }

    [Fact]
    public void Load_hands_out_copies_so_callers_cannot_mutate_the_cache()
    {
        var store = new LightingSceneStore(_dir);
        store.Save(new LightingSceneDoc { Objects = { new SceneObject { Id = "kb" } } });
        store.Load().Objects.Clear();
        Assert.Single(store.Load().Objects);
    }
}
