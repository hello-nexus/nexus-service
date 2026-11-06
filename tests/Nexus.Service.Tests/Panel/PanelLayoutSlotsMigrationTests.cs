using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Panel;

/// <summary>Schema 32: the 8.8 inch strip's one layout lands in the slot of the orientation it is mounted in.</summary>
public class PanelLayoutSlotsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slots-mig-" + Guid.NewGuid().ToString("N")[..8]);

    public PanelLayoutSlotsMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PanelDeviceCapabilities Strip() => new()
    {
        Surface = PanelSurfaces.Monitor,
        Family = "lianli-screen88",
        CssWidth = 1920,
        CssHeight = 480,
        SupportsPortrait = true,
    };

    private static PanelLayoutDto Layout() => new()
    {
        LayoutSchemaVersion = 2,
        Surface = PanelSurfaces.Monitor,
        Pages = { new PanelPageDto { Id = "p", Widgets = { new PanelWidgetDto { Id = "w", Type = "clock", Size = "4x4" } } } },
    };

    private static string Json(PanelLayoutDto? layout) => JsonSerializer.Serialize(layout, PersistenceJsonContext.Default.PanelLayoutDto);

    // The screen without its instance ids, which the portrait copy gets fresh.
    private static string Screen(PanelLayoutDto? layout) =>
        layout is null ? "" : Json(PanelLayoutDefaults.WithStableIds(PanelPresets.CloneLayout(layout)!, ""));

    private string Write(int schema, Action<PanelDeviceRecord> shape)
    {
        var settings = new NexusSettings { SchemaVersion = schema };
        var record = new PanelDeviceRecord { Id = "s", Capabilities = Strip(), Layout = Layout(), WidgetSize = "small", GridSlots = 6 };
        shape(record);
        settings.PanelDevices["s"] = record;
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings));
        return path;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, true)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    public void The_layout_the_user_sees_is_the_same_after_the_upgrade(int rotation, bool portrait)
    {
        var path = Write(31, r => r.Rotation = rotation == 0 ? null : rotation);
        var before = Json(Layout());

        using var store = new JsonConfigStore(path);
        var stored = store.Load().PanelDevices["s"];
        var shown = new PanelDeviceRegistry(store).Get("s")!;

        Assert.Equal(Screen(Layout()), Screen(shown.Layout));
        Assert.Equal("small", shown.WidgetSize);
        Assert.Equal(6, shown.GridSlots);
        Assert.True(stored.LayoutSlots);
        // The landscape fields keep what the strip showed, so a downgraded build still finds it.
        Assert.Equal(before, Json(stored.Layout));
        Assert.Equal(portrait ? Screen(Layout()) : "", Screen(stored.PortraitLayout));
        Assert.Equal("small", stored.WidgetSize);
        Assert.Equal(portrait ? "small" : null, stored.PortraitWidgetSize);
        Assert.Equal(portrait ? 6 : null, stored.PortraitGridSlots);
        Assert.Equal(NexusSettings.CurrentSchemaVersion, store.Load().SchemaVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pre_rotation_document_with_the_portrait_flag_lands_in_the_slot_it_showed(bool flip)
    {
        var path = Write(30, r => { r.Portrait = true; r.Flip180 = flip; });
        var before = Json(Layout());

        using var store = new JsonConfigStore(path);
        var shown = new PanelDeviceRegistry(store).Get("s")!;

        Assert.True(PanelRotation.IsPortrait(shown.Rotation ?? 0));
        Assert.Equal(Screen(Layout()), Screen(shown.Layout));
        Assert.Equal(Screen(Layout()), Screen(store.Load().PanelDevices["s"].PortraitLayout));
    }

    [Fact]
    public void Running_it_again_changes_nothing_and_a_second_load_is_byte_stable()
    {
        var path = Write(31, r => r.Rotation = 90);
        using (var first = new JsonConfigStore(path))
        {
            Assert.False(PanelLayoutSlotsMigration.Apply(first.Load()));
        }
        var afterFirst = File.ReadAllText(path);

        using var second = new JsonConfigStore(path);
        var loaded = second.Load();

        Assert.False(PanelLayoutSlotsMigration.Apply(loaded));
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void A_layout_saved_for_the_other_orientation_after_the_upgrade_is_not_moved_again()
    {
        var path = Write(31, r => r.Rotation = 90);
        using (var store = new JsonConfigStore(path))
        {
            var registry = new PanelDeviceRegistry(store);
            registry.Patch("s", new PanelDevicePatch { Rotation = 0 });
            registry.Patch("s", new PanelDevicePatch { Layout = new PanelLayoutDto { LayoutSchemaVersion = 2, Surface = PanelSurfaces.Monitor } });
            registry.Patch("s", new PanelDevicePatch { Rotation = 90 });
            store.FlushNow();
        }

        using var again = new JsonConfigStore(path);
        var stored = again.Load().PanelDevices["s"];

        Assert.Equal(Screen(Layout()), Screen(stored.PortraitLayout));
        Assert.NotNull(stored.Layout);
        Assert.Empty(stored.Layout!.Pages);
    }

    [Fact]
    public void A_downgraded_build_that_dropped_the_slots_is_placed_again_on_a_current_document()
    {
        // What an old build writes back: no portrait slot, no marker, and the layout it left in the one field it knows.
        var path = Write(NexusSettings.CurrentSchemaVersion, r =>
        {
            r.Rotation = 270;
            r.Layout = Layout();
            r.Layout.Pages[0].Widgets[0].Type = "edited-by-old-build";
            r.PortraitLayout = null;
            r.LayoutSlots = null;
        });

        using var store = new JsonConfigStore(path);
        var stored = store.Load().PanelDevices["s"];
        var shown = new PanelDeviceRegistry(store).Get("s")!;

        Assert.Equal("edited-by-old-build", stored.PortraitLayout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("edited-by-old-build", shown.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("edited-by-old-build", stored.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.True(stored.LayoutSlots);
    }

    [Fact]
    public void The_file_the_move_was_read_from_is_kept_once()
    {
        var path = Write(31, r => r.Rotation = 90);
        var original = File.ReadAllText(path);

        using (var store = new JsonConfigStore(path))
        {
            store.Load();
        }

        Assert.Equal(original, File.ReadAllText(path + ".pre-v32"));
        File.WriteAllText(path + ".pre-v32", "kept");
        File.WriteAllText(path, original);
        using (var store = new JsonConfigStore(path))
        {
            store.Load();
        }
        Assert.Equal("kept", File.ReadAllText(path + ".pre-v32"));
    }

    [Fact]
    public void Nothing_is_kept_when_nothing_moves()
    {
        var path = Write(31, r => r.Rotation = 180);

        using var store = new JsonConfigStore(path);
        store.Load();

        Assert.False(File.Exists(path + ".pre-v32"));
    }

    [Fact]
    public void Toggling_the_strip_between_classes_switches_slots()
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        var record = registry.Allocate("Strip", Strip());
        registry.Patch(record.Id, new PanelDevicePatch { Layout = Layout(), WidgetSize = "small" });

        Assert.Null(registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90 })!.Layout);
        registry.Patch(record.Id, new PanelDevicePatch { Layout = new PanelLayoutDto { LayoutSchemaVersion = 2, Surface = PanelSurfaces.Monitor }, WidgetSize = "large" });
        var back = registry.Patch(record.Id, new PanelDevicePatch { Rotation = 180 })!;

        Assert.Equal(Json(Layout()), Json(back.Layout));
        Assert.Equal("small", back.WidgetSize);
        // The strip's capabilities are already the turned size; the render size does not swap again.
        Assert.Equal((1920, 480), (back.RenderWidth, back.RenderHeight));
        var turned = registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90 })!;
        Assert.Equal((1920, 480), (turned.RenderWidth, turned.RenderHeight));
        Assert.Equal("large", turned.WidgetSize);
    }

    [Fact]
    public void A_strip_record_made_after_the_migration_is_marked_and_stays_where_it_is_saved()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var fresh = registry.Allocate("Strip", Strip());
        var bare = registry.Allocate("Bare", null);
        registry.Patch(bare.Id, new PanelDevicePatch { Capabilities = Strip() });

        Assert.True(store.Load().PanelDevices[fresh.Id].LayoutSlots);
        Assert.True(store.Load().PanelDevices[bare.Id].LayoutSlots);
        Assert.Null(registry.Get(fresh.Id)!.LayoutSlots);
    }

    [Theory]
    [InlineData(90, null)]
    [InlineData(270, 180)]
    [InlineData(180, 180)]
    public void A_quarter_stored_on_glass_that_is_not_square_keeps_the_turn_it_displayed(int stored, int? expected)
    {
        var path = Write(31, r =>
        {
            r.Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdWide, Family = "thermalright-lcd", CssWidth = 854, CssHeight = 480 };
            r.Rotation = stored;
        });

        using var store = new JsonConfigStore(path);

        Assert.Equal(expected, store.Load().PanelDevices["s"].Rotation);
    }

    [Fact]
    public void A_quarter_chosen_on_non_square_glass_after_the_upgrade_is_not_clamped_again()
    {
        var path = Write(31, r => r.Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdWide, Family = "thermalright-lcd", CssWidth = 854, CssHeight = 480 });
        using (var store = new JsonConfigStore(path))
        {
            new PanelDeviceRegistry(store).Patch("s", new PanelDevicePatch { Rotation = 90 });
            store.FlushNow();
        }

        using var again = new JsonConfigStore(path);

        Assert.Equal(90, again.Load().PanelDevices["s"].Rotation);
    }

    [Fact]
    public void A_quarter_on_square_glass_and_on_the_strip_is_left_alone()
    {
        var square = Write(31, r =>
        {
            r.Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdSquare, CssWidth = 480, CssHeight = 480 };
            r.Rotation = 90;
        });
        using var store = new JsonConfigStore(square);
        Assert.Equal(90, store.Load().PanelDevices["s"].Rotation);
    }

    [Fact]
    public void The_portrait_copy_has_its_own_ids_and_its_own_deck_rows()
    {
        var path = Write(31, r =>
        {
            r.Rotation = 90;
            r.Layout = Layout();
            r.Layout.ActivePageId = "p";
            r.Layout.ImmersiveOnLoadWidgetId = "w";
            r.Layout.Pages[0].Widgets[0].Type = "deck";
        });
        var json = File.ReadAllText(path);
        var withRow = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!;
        withRow.StreamDeck.Instances["widget:w"] = new DeckInstance { Mode = "custom", ActivePresetId = "preset-1" };
        File.WriteAllText(path, JsonSerializer.Serialize(withRow, PersistenceJsonContext.Default.NexusSettings));

        using var store = new JsonConfigStore(path);
        var loaded = store.Load();
        var stored = loaded.PanelDevices["s"];
        var landscapeWidget = stored.Layout!.Pages.Single().Widgets.Single();
        var portraitWidget = stored.PortraitLayout!.Pages.Single().Widgets.Single();

        Assert.Equal("w", landscapeWidget.Id);
        Assert.NotEqual("w", portraitWidget.Id);
        Assert.NotEqual(stored.Layout.Pages.Single().Id, stored.PortraitLayout.Pages.Single().Id);
        Assert.Equal(stored.PortraitLayout.Pages.Single().Id, stored.PortraitLayout.ActivePageId);
        Assert.Equal(portraitWidget.Id, stored.PortraitLayout.ImmersiveOnLoadWidgetId);
        Assert.Equal("w", stored.Layout.ImmersiveOnLoadWidgetId);

        var rows = loaded.StreamDeck.Instances;
        var landscapeRow = rows["widget:w"];
        var portraitRow = rows["widget:" + portraitWidget.Id];
        Assert.NotSame(landscapeRow, portraitRow);
        Assert.Equal("preset-1", portraitRow.ActivePresetId);
        portraitRow.ActivePresetId = "preset-2";
        Assert.Equal("preset-1", landscapeRow.ActivePresetId);
    }

    [Fact]
    public void Placing_the_same_layout_again_after_a_downgrade_gives_the_same_ids()
    {
        var first = Write(31, r => r.Rotation = 90);
        string id;
        using (var store = new JsonConfigStore(first))
        {
            id = store.Load().PanelDevices["s"].PortraitLayout!.Pages.Single().Widgets.Single().Id;
        }
        var second = Write(NexusSettings.CurrentSchemaVersion, r => r.Rotation = 90);

        using var again = new JsonConfigStore(second);

        Assert.Equal(id, again.Load().PanelDevices["s"].PortraitLayout!.Pages.Single().Widgets.Single().Id);
    }
}
