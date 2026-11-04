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

    private static SceneObject Obj(string id, string kind) => new() { Id = id, Kind = kind, Anchors = { new SceneAnchor { Id = "surface", Kind = "surface" } } };

    private static SceneBinding Bind(string device, string objectId) =>
        new() { DeviceId = device, Targets = { new SceneTarget { ObjectId = objectId, AnchorId = "surface" } } };

    private LightingSceneStore StoreWith(List<SceneBinding> bindings, params SceneObject[] objects)
    {
        var store = new LightingSceneStore(_dir);
        store.Save(new LightingSceneDoc { Objects = objects.ToList(), Bindings = bindings });
        return store;
    }

    [Fact]
    public void A_missing_id_heals_onto_the_only_object_of_its_kind_and_persists_once()
    {
        var store = StoreWith([Bind("m", "mouse-1")], Obj("p-mouse-9", "mouse"), Obj("p-keyboard-2", "keyboard"));
        var changes = 0;
        store.Changed += () => changes++;

        Assert.Equal("p-mouse-9", store.LoadEffective().Bindings[0].Targets[0].ObjectId);
        Assert.Equal("p-mouse-9", new LightingSceneStore(_dir).Load().Bindings[0].Targets[0].ObjectId);
        store.LoadEffective();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Two_objects_of_the_kind_leave_the_binding_unbound()
    {
        var store = StoreWith([Bind("k", "keyboard-1")], Obj("p-keyboard-a", "keyboard"), Obj("p-keyboard-b", "keyboard"));

        Assert.Equal("keyboard-1", store.LoadEffective().Bindings[0].Targets[0].ObjectId);
    }

    [Fact]
    public void Two_missing_ids_with_one_target_heal_only_the_first()
    {
        var store = StoreWith([Bind("a", "mouse-1"), Bind("b", "mouse-2")], Obj("p-mouse-9", "mouse"));

        var bindings = store.LoadEffective().Bindings;

        Assert.Equal("p-mouse-9", bindings[0].Targets[0].ObjectId);
        Assert.Equal("mouse-2", bindings[1].Targets[0].ObjectId);
    }

    [Fact]
    public void A_valid_binding_is_untouched_and_a_held_object_is_not_taken()
    {
        var store = StoreWith([Bind("a", "p-mouse-9"), Bind("b", "mouse-1")], Obj("p-mouse-9", "mouse"));
        var changes = 0;
        store.Changed += () => changes++;

        var bindings = store.LoadEffective().Bindings;

        Assert.Equal("p-mouse-9", bindings[0].Targets[0].ObjectId);
        Assert.Equal("mouse-1", bindings[1].Targets[0].ObjectId);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_removed_setup_item_is_not_re_homed()
    {
        var store = StoreWith([Bind("m", "p-mouse-old")], Obj("p-mouse-new", "mouse"));

        Assert.Equal("p-mouse-old", store.LoadEffective().Bindings[0].Targets[0].ObjectId);
    }

    [Fact]
    public void A_failed_persist_still_returns_the_healed_view()
    {
        var store = StoreWith([Bind("m", "mouse-1")], Obj("p-mouse-9", "mouse"));
        var path = Path.Combine(_dir, "lighting-scene.json");
        File.Delete(path);
        Directory.CreateDirectory(path);
        var changes = 0;
        store.Changed += () => changes++;

        var doc = store.LoadEffective();

        Assert.Equal("p-mouse-9", doc.Bindings[0].Targets[0].ObjectId);
        Assert.Equal(0, changes);
    }
}
