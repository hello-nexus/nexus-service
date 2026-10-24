using System.Numerics;
using System.Text.Json;
using Nexus.Service.Lighting.Scene;

namespace Nexus.Service.Tests.Lighting.Scene;

public sealed class PcSetupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-pc-setup-" + Guid.NewGuid().ToString("N"));

    public PcSetupStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* already gone */ }
    }

    private string ScenePath => Path.Combine(_dir, "lighting-scene.json");
    private string SetupPath => Path.Combine(_dir, "pc-setup.json");

    private static SceneObject Obj(string id, string kind, string source, float x, float z, float yaw = 0f, string? label = null, float w = 100f, float d = 50f) => new()
    {
        Id = id,
        Kind = kind,
        Source = source,
        Label = label,
        Position = [x, 0f, z],
        Yaw = yaw,
        Size = [w, 20f, d],
    };

    private static PcSetup Setup(params string[] itemIds)
    {
        var setup = new PcSetup();
        foreach (var id in itemIds)
        {
            setup.Items.Add(new PcSetupItem { Id = id, Kind = "keyboard", Custom = new PcSetupCustom { Name = id }, Place = new PcSetupPlace { X = 1f, Z = 2f, Yaw = 3f } });
        }
        return setup;
    }

    // ---- store ----

    [Fact]
    public void A_fresh_store_reads_rev_zero_and_an_empty_record_without_writing()
    {
        var store = new LightingSceneStore(_dir);

        var file = store.Setup.Load();

        Assert.Equal(0, file.Rev);
        Assert.Empty(file.Setup.Items);
        Assert.Empty(file.Setup.Resolved.Objects);
        Assert.False(File.Exists(SetupPath));
        Assert.Null(store.Setup.LoadSaved());
    }

    [Fact]
    public void Save_round_trips_and_the_rev_increments_on_every_save()
    {
        var store = new LightingSceneStore(_dir);
        var parts = JsonDocument.Parse("""{"corrections":{"gpu":{"a":[1,2,3]}},"more":[]}""").RootElement.Clone();
        var setup = Setup("kb");
        setup.Parts = parts;
        setup.Case.Place = new PcSetupPlace { X = -300f, Z = 40f, Yaw = 90f, Curved = true, KeptSize = JsonDocument.Parse("""{"w":1}""").RootElement.Clone() };
        setup.Resolved.Objects.Add(Obj("kb", "keyboard", "user", 1f, 2f));

        Assert.True(store.Setup.TrySave(0, setup, out var first));
        Assert.Equal(1, first.Rev);
        Assert.True(store.Setup.TrySave(1, setup, out var second));
        Assert.Equal(2, second.Rev);

        var reread = new LightingSceneStore(_dir).Setup.Load();
        Assert.Equal(2, reread.Rev);
        Assert.Equal("kb", Assert.Single(reread.Setup.Items).Id);
        Assert.Equal(-300f, reread.Setup.Case.Place!.X);
        Assert.True(reread.Setup.Case.Place.Curved);
        Assert.Equal(1, reread.Setup.Case.Place.KeptSize!.Value.GetProperty("w").GetInt32());
        Assert.Equal(3, reread.Setup.Parts!.Value.GetProperty("corrections").GetProperty("gpu").GetProperty("a")[2].GetInt32());
        Assert.Equal("kb", Assert.Single(reread.Setup.Resolved.Objects).Id);
    }

    [Fact]
    public void A_stale_base_rev_is_refused_and_returns_the_current_file()
    {
        var store = new LightingSceneStore(_dir);
        Assert.True(store.Setup.TrySave(0, Setup("a"), out _));

        var saved = store.Setup.TrySave(0, Setup("b"), out var current);

        Assert.False(saved);
        Assert.Equal(1, current.Rev);
        Assert.Equal("a", Assert.Single(current.Setup.Items).Id);
        Assert.Equal("a", Assert.Single(store.Setup.Load().Setup.Items).Id);
    }

    [Fact]
    public void Writes_are_atomic_files_with_no_temp_left_behind()
    {
        var store = new LightingSceneStore(_dir);
        store.Setup.TrySave(0, Setup("a"), out _);

        Assert.True(File.Exists(SetupPath));
        Assert.False(File.Exists(SetupPath + ".tmp"));
    }

    [Fact]
    public void Load_hands_out_copies_so_callers_cannot_mutate_the_cache()
    {
        var store = new LightingSceneStore(_dir);
        store.Setup.TrySave(0, Setup("a"), out _);

        store.Setup.Load().Setup.Items.Clear();

        Assert.Single(store.Setup.Load().Setup.Items);
    }

    [Fact]
    public void A_save_raises_the_scene_stores_Changed_with_no_scene_write()
    {
        var store = new LightingSceneStore(_dir);
        var changed = 0;
        store.Changed += () => changed++;
        var pinged = new List<int>();
        store.Setup.Changed += pinged.Add;

        store.Setup.TrySave(0, Setup("a"), out _);
        store.Setup.Replace(Setup("b"));

        Assert.Equal(2, changed);
        Assert.Equal([1, 2], pinged);
        Assert.False(File.Exists(ScenePath));
    }

    [Fact]
    public void A_corrupt_record_reads_as_rev_zero()
    {
        File.WriteAllText(SetupPath, "{ not json");
        Assert.Equal(0, new LightingSceneStore(_dir).Setup.Load().Rev);
    }

    // ---- scene objects come from the record ----

    [Fact]
    public void Effective_objects_fall_back_to_the_scene_file_when_no_record_is_saved()
    {
        var store = new LightingSceneStore(_dir);
        var doc = new LightingSceneDoc();
        doc.Objects.Add(Obj("kb", "keyboard", "user", 5f, 6f));
        store.Save(doc);

        Assert.Equal(["kb"], store.LoadEffective().Objects.Select(o => o.Id));
    }

    [Fact]
    public void Effective_objects_come_from_the_record_and_bindings_stay_the_scenes()
    {
        var store = new LightingSceneStore(_dir);
        var doc = new LightingSceneDoc();
        doc.Objects.Add(Obj("old", "keyboard", "user", 5f, 6f));
        doc.Bindings.Add(new SceneBinding { DeviceId = "d", Targets = [new SceneTarget { ObjectId = "new", AnchorId = "top" }] });
        store.Save(doc);
        var setup = Setup("new");
        var resolved = Obj("new", "keyboard", "user", 1f, 2f);
        resolved.Anchors.Add(new SceneAnchor { Id = "top", Up = [0f, 0f, -1f] });
        setup.Resolved.Objects.Add(resolved);
        store.Setup.TrySave(0, setup, out _);

        var effective = store.LoadEffective();

        Assert.Equal(["new"], effective.Objects.Select(o => o.Id));
        Assert.Equal("d", Assert.Single(effective.Bindings).DeviceId);
        Assert.Single(SceneMath.Placements(effective));
        Assert.Equal(["old"], store.Load().Objects.Select(o => o.Id));
    }

    [Fact]
    public void An_empty_resolved_block_does_not_hide_the_scene_files_objects()
    {
        var store = new LightingSceneStore(_dir);
        var doc = new LightingSceneDoc();
        doc.Objects.Add(Obj("old", "keyboard", "user", 5f, 6f));
        store.Save(doc);
        store.Setup.TrySave(0, Setup("a"), out _);

        Assert.Equal(["old"], store.LoadEffective().Objects.Select(o => o.Id));
    }

    // ---- migration ----

    private PcSetupFile Migrate(params SceneObject[] objects)
    {
        var doc = new LightingSceneDoc();
        doc.Objects.AddRange(objects);
        new LightingSceneStore(_dir).Save(doc);
        return new LightingSceneStore(_dir).Setup.Load();
    }

    [Fact]
    public void Migration_turns_the_case_into_case_place()
    {
        var file = Migrate(Obj("case-1", "case", "build", -250f, 30f, 45f, w: 220f, d: 480f));

        Assert.Equal(0, file.Rev);
        Assert.Equal(-250f, file.Setup.Case.Place!.X);
        Assert.Equal(30f, file.Setup.Case.Place.Z);
        Assert.Equal(45f, file.Setup.Case.Place.Yaw);
        Assert.Empty(file.Setup.Items);
    }

    [Fact]
    public void Migration_turns_hand_placed_user_objects_into_custom_items_keeping_their_ids()
    {
        var file = Migrate(
            Obj("kb-uuid", "keyboard", "user", 10f, 100f, 15f, label: "My keys", w: 440f, d: 140f),
            Obj("mon-uuid", "monitor", "user", 0f, -200f, label: null, w: 600f, d: 60f));

        Assert.Equal(["kb-uuid", "mon-uuid"], file.Setup.Items.Select(i => i.Id));
        var kb = file.Setup.Items[0];
        Assert.Equal("keyboard", kb.Kind);
        Assert.Equal("My keys", kb.Custom!.Name);
        Assert.Equal(440f, kb.Custom.WidthMm);
        Assert.Equal(140f, kb.Custom.DepthMm);
        Assert.True(kb.Owned);
        Assert.Equal((10f, 100f, 15f), (kb.Place!.X, kb.Place.Z, kb.Place.Yaw));
        Assert.Equal("monitor", file.Setup.Items[1].Custom!.Name);
    }

    [Theory]
    [InlineData("headset", "headphones")]
    [InlineData("deskmat", "other")]
    [InlineData("strip", "other")]
    [InlineData("light", "other")]
    [InlineData("box", "other")]
    [InlineData("keyboard", "keyboard")]
    [InlineData("mouse", "mouse")]
    [InlineData("speaker", "speaker")]
    public void Migration_maps_scene_kinds_to_item_kinds(string sceneKind, string itemKind)
    {
        var file = Migrate(Obj("o", sceneKind, "user", 0f, 0f));
        Assert.Equal(itemKind, Assert.Single(file.Setup.Items).Kind);
    }

    [Fact]
    public void Migration_keeps_build_objects_out_of_items_and_serves_every_old_object_as_resolved()
    {
        var file = Migrate(
            Obj("c", "case", "build", 0f, 0f),
            Obj("mon", "monitor", "build", 0f, -300f),
            Obj("kb", "keyboard", "user", 0f, 100f));

        Assert.Equal(["kb"], file.Setup.Items.Select(i => i.Id));
        Assert.Equal(["c", "mon", "kb"], file.Setup.Resolved.Objects.Select(o => o.Id));
    }

    [Fact]
    public void Migration_is_idempotent_and_never_writes_either_file()
    {
        var doc = new LightingSceneDoc();
        doc.Objects.Add(Obj("kb", "keyboard", "user", 1f, 2f));
        new LightingSceneStore(_dir).Save(doc);
        var sceneBefore = File.ReadAllText(ScenePath);

        var store = new LightingSceneStore(_dir);
        var a = store.Setup.Load();
        var b = store.Setup.Load();
        var c = new LightingSceneStore(_dir).Setup.Load();

        Assert.Equal(JsonSerializer.Serialize(a.Setup.Items.Select(i => i.Id)), JsonSerializer.Serialize(b.Setup.Items.Select(i => i.Id)));
        Assert.Equal(["kb"], c.Setup.Items.Select(i => i.Id));
        Assert.Equal(sceneBefore, File.ReadAllText(ScenePath));
        Assert.False(File.Exists(SetupPath));
    }

    [Fact]
    public void A_saved_record_is_never_replaced_by_the_migration()
    {
        var doc = new LightingSceneDoc();
        doc.Objects.Add(Obj("kb", "keyboard", "user", 1f, 2f));
        new LightingSceneStore(_dir).Save(doc);
        var store = new LightingSceneStore(_dir);
        store.Setup.TrySave(0, Setup("mine"), out _);

        Assert.Equal(["mine"], new LightingSceneStore(_dir).Setup.Load().Setup.Items.Select(i => i.Id));
    }

    [Fact]
    public void A_scene_with_no_objects_migrates_to_an_empty_record()
    {
        var file = Migrate();
        Assert.Empty(file.Setup.Items);
        Assert.Null(file.Setup.Case.Place);
    }

    // ---- coordinate contract ----

    [Fact]
    public void Coordinate_contract_place_is_the_footprint_centre_in_millimetres_and_yaw_is_counter_clockwise_from_above()
    {
        // Same world as SceneObject: +X right of the seated user, +Z toward them, +Y up, desk surface at y = 0.
        var file = Migrate(Obj("kb", "keyboard", "user", x: 120f, z: 340f, yaw: 90f));
        var place = file.Setup.Items[0].Place!;

        // x and z are SceneObject.Position[0] and [2] unchanged: footprint centre, no unit or axis conversion.
        Assert.Equal(120f, place.X);
        Assert.Equal(340f, place.Z);

        // A positive yaw turns an item's front (+Z, toward the user) toward +X: counter-clockwise seen from above.
        var front = SceneMath.RotateYaw(Vector3.UnitZ, place.Yaw);
        Assert.Equal(1f, front.X, 4);
        Assert.Equal(0f, front.Z, 4);
        // And its right (+X) toward -Z, away from the user.
        var right = SceneMath.RotateYaw(Vector3.UnitX, place.Yaw);
        Assert.Equal(0f, right.X, 4);
        Assert.Equal(-1f, right.Z, 4);
    }

    // ---- validation ----

    private static PcSetup Valid()
    {
        var setup = Setup("kb");
        setup.Resolved.Objects.Add(Obj("kb", "keyboard", "user", 1f, 2f));
        return setup;
    }

    [Fact]
    public void A_valid_record_passes()
    {
        Assert.Null(PcSetupValidation.Validate(Valid()));
    }

    [Fact]
    public void Item_count_is_capped_below_the_scene_object_limit()
    {
        var setup = Setup(Enumerable.Range(0, PcSetupValidation.MaxItems + 1).Select(i => "i" + i).ToArray());
        Assert.Equal("too_many_items", PcSetupValidation.Validate(setup)!.Code);

        var atCap = Setup(Enumerable.Range(0, PcSetupValidation.MaxItems).Select(i => "i" + i).ToArray());
        Assert.Null(PcSetupValidation.Validate(atCap));
        Assert.True(PcSetupValidation.MaxItems < SceneValidation.MaxObjects);
    }

    [Fact]
    public void Resolved_objects_are_held_to_the_scene_object_limit()
    {
        var setup = Valid();
        setup.Resolved.Objects.Clear();
        for (var i = 0; i <= SceneValidation.MaxObjects; i++)
        {
            setup.Resolved.Objects.Add(Obj("o" + i, "box", "user", 0f, 0f));
        }
        Assert.Equal("invalid_resolved", PcSetupValidation.Validate(setup)!.Code);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("title")]
    [InlineData("key")]
    [InlineData("kind")]
    [InlineData("id")]
    public void Strings_are_length_capped(string field)
    {
        var setup = Valid();
        var item = setup.Items[0];
        var tooLong = new string('a', 300);
        switch (field)
        {
            case "name": item.Custom!.Name = tooLong; break;
            case "title": item.Catalog = new PcSetupCatalog { Category = "monitor", PartId = "p", Title = tooLong }; break;
            case "key": item.Detected = new PcSetupDetected { Key = tooLong }; break;
            case "kind": item.Kind = tooLong; break;
            default: item.Id = tooLong; break;
        }
        Assert.NotNull(PcSetupValidation.Validate(setup));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(1e9f)]
    public void Placements_must_be_finite_and_bounded(float bad)
    {
        var setup = Valid();
        setup.Items[0].Place!.X = bad;
        Assert.Equal("non_finite_number", PcSetupValidation.Validate(setup)!.Code);

        setup = Valid();
        setup.Case.Place = new PcSetupPlace { Z = bad };
        Assert.Equal("non_finite_number", PcSetupValidation.Validate(setup)!.Code);
    }

    [Fact]
    public void Detected_key_accepts_free_text_with_spaces_and_hash_up_to_the_cap()
    {
        foreach (var key in new[] { "LG ULTRAGEAR #2 (HDMI)", "DELL U2723QE / 27\" 4K", "Ж монитор", new string('k', PcSetupValidation.MaxKey) })
        {
            var setup = Valid();
            setup.Items[0].Detected = new PcSetupDetected { Key = key, Hidden = true };
            Assert.Null(PcSetupValidation.Validate(setup));
        }

        var tooLong = Valid();
        tooLong.Items[0].Detected = new PcSetupDetected { Key = new string('k', PcSetupValidation.MaxKey + 1) };
        Assert.Equal("string_too_long", PcSetupValidation.Validate(tooLong)!.Code);
    }

    [Fact]
    public void Ascii_safe_ids_with_a_hash_are_accepted_while_a_raw_name_as_an_id_is_not()
    {
        Assert.Null(PcSetupValidation.Validate(Setup("det-3fa9c1d2")));
        Assert.Equal("invalid_item", PcSetupValidation.Validate(Setup("det:LG #2"))!.Code);
    }

    [Fact]
    public void Custom_sizes_and_prices_must_be_finite_and_positive()
    {
        var setup = Valid();
        setup.Items[0].Custom!.WidthMm = -1f;
        Assert.Equal("non_finite_number", PcSetupValidation.Validate(setup)!.Code);

        setup = Valid();
        setup.Items[0].Custom!.Price = float.NaN;
        Assert.Equal("non_finite_number", PcSetupValidation.Validate(setup)!.Code);
    }

    [Fact]
    public void Duplicate_ids_and_the_reserved_case_id_are_refused()
    {
        var setup = Setup("a", "a");
        Assert.Equal("duplicate_id", PcSetupValidation.Validate(setup)!.Code);
        Assert.Equal("duplicate_id", PcSetupValidation.Validate(Setup("case"))!.Code);
    }

    [Fact]
    public void Opaque_blocks_and_the_whole_record_are_size_capped()
    {
        var big = JsonDocument.Parse("\"" + new string('x', PcSetupValidation.MaxOpaqueParts) + "\"").RootElement.Clone();
        var setup = Valid();
        setup.Parts = big;
        Assert.Equal("value_too_large", PcSetupValidation.Validate(setup)!.Code);

        setup = Valid();
        setup.Items[0].Catalog = new PcSetupCatalog
        {
            Category = "monitor",
            PartId = "p",
            Title = "t",
            Specs = JsonDocument.Parse("\"" + new string('x', PcSetupValidation.MaxOpaqueSpecs) + "\"").RootElement.Clone(),
        };
        Assert.Equal("value_too_large", PcSetupValidation.Validate(setup)!.Code);

        // Many in-cap pieces still add up past the record cap.
        setup = Valid();
        setup.Items.Clear();
        setup.Parts = JsonDocument.Parse("\"" + new string('x', PcSetupValidation.MaxOpaqueParts - 8) + "\"").RootElement.Clone();
        for (var i = 0; i < PcSetupValidation.MaxItems; i++)
        {
            setup.Items.Add(new PcSetupItem
            {
                Id = "i" + i,
                Kind = "monitor",
                Catalog = new PcSetupCatalog
                {
                    Category = "monitor",
                    PartId = "p",
                    Title = "t",
                    Specs = JsonDocument.Parse("\"" + new string('x', PcSetupValidation.MaxOpaqueSpecs - 8) + "\"").RootElement.Clone(),
                },
            });
        }
        Assert.Equal("record_too_large", PcSetupValidation.Validate(setup)!.Code);
    }

    [Fact]
    public void Record_cap_leaves_the_backup_document_inside_the_app_data_document_cap()
    {
        Assert.True(PcSetupValidation.MaxRecordBytes <= Nexus.Service.Widgets.AppDataStore.MaxDataBytes);
    }
}
