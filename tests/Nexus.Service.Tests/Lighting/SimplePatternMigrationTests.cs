using System.IO;
using Nexus.Service.Deck;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Simple mode's rainbow, bars and breathing tiles are presets of the advanced
/// effects now: every stored or incoming key lands on the new effect, a reversed
/// tile keeps running reversed, and the advanced rainbow's presets follow its
/// slot shift exactly once.
/// </summary>
public sealed class SimplePatternMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private string SettingsPath => Path.Combine(_tempDir, "settings.json");

    public SimplePatternMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-simple-pattern-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("sweeprainbow", "rainbow")]
    [InlineData("SweepBars", "sharplines")]
    [InlineData("sweepbreathing", "breathing")]
    [InlineData("rainbow", "rainbow")]
    [InlineData("sweepcomet", "sweepcomet")]
    [InlineData("plasma", "plasma")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Normalize_maps_only_the_retired_tiles(string? input, string? expected)
    {
        Assert.Equal(expected, SimplePatternMigration.Normalize(input));
    }

    [Theory]
    [InlineData("rainbow")]
    [InlineData("sharplines")]
    [InlineData("breathing")]
    [InlineData("sweepcycle")]
    [InlineData("sweepbrush")]
    [InlineData("sweepliquid")]
    [InlineData("sweepcomet")]
    public void Every_full_frame_effect_has_four_default_slots(string key)
    {
        Assert.True(AnimateTemplateDefaults.All.TryGetValue(key, out var bundle));
        Assert.Equal(4, bundle.Slots.Count);
        Assert.All(bundle.Slots, slot => Assert.NotNull(slot));
        Assert.Equal(50, bundle.Slots[0]!.Speed);
    }

    [Fact]
    public void Stock_slot_one_of_the_merged_rainbow_is_the_simple_look_and_slot_two_the_old_stock()
    {
        var simple = AnimateTemplateDefaults.Slot("rainbow", 0)!;
        Assert.Equal(0.55f, simple.Params["u_tempo"]);
        Assert.Equal(0f, simple.Params["u_aspect"]);
        var oldStock = AnimateTemplateDefaults.Slot("rainbow", 1)!;
        Assert.Equal(0.15f, oldStock.Params["u_tempo"]);
        Assert.Equal(1f, oldStock.Params["u_aspect"]);
        Assert.Equal(85, AnimateTemplateDefaults.Slot("rainbow", 2)!.Speed);
    }

    private static NexusSettings LegacyDoc()
    {
        var doc = new NexusSettings { SchemaVersion = 27 };
        doc.Lighting.Sync = "sweeprainbow";
        doc.Lighting.Animate.Effect = "sweeprainbow";
        doc.Lighting.Animate.States["sweeprainbow"] = new AnimateEffectState { Speed = -50 };
        doc.Lighting.Animate.States["sweepbars"] = new AnimateEffectState { Speed = 50 };
        doc.Lighting.DevicePins["a"] = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sweepbars" };
        doc.Lighting.DevicePins["b"] = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sweepcomet" };
        doc.Lighting.LayoutPresets.Add(new LayoutPreset
        {
            Id = "p",
            DevicePins = new() { ["c"] = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sweepbreathing" } },
            Look = new LightingPresetLook
            {
                Sync = "sweepbars",
                AnimateEffect = "sweepbars",
                AnimateSlot = -1,
                AnimateState = new AnimateEffectState { Speed = -50 },
            },
        });
        doc.Ui.RecentAnimations = ["sweepbreathing", "plasma", "breathing", "sweeprainbow"];
        doc.Panel.BackgroundEffect = "sweepbars";
        doc.Panel.BackgroundTemplate = 2;
        doc.PanelDevices["panel-1"] = new PanelDeviceRecord
        {
            Id = "panel-1",
            BackgroundEffect = "sweeprainbow",
            BackgroundTemplate = 3,
            BackgroundTemplates = new() { ["sweeprainbow"] = 3, ["plasma"] = 1 },
        };
        return doc;
    }

    private static void AssertMigrated(NexusSettings doc)
    {
        Assert.Equal("rainbow", doc.Lighting.Sync);
        Assert.Equal("rainbow", doc.Lighting.Animate.Effect);
        Assert.False(doc.Lighting.Animate.States.ContainsKey("sweeprainbow"));
        Assert.False(doc.Lighting.Animate.States.ContainsKey("sweepbars"));
        Assert.Equal("sharplines", doc.Lighting.DevicePins["a"].Effect);
        Assert.Equal("sweepcomet", doc.Lighting.DevicePins["b"].Effect);
        var preset = doc.Lighting.LayoutPresets[0];
        Assert.Equal("breathing", preset.DevicePins!["c"].Effect);
        Assert.Equal("sharplines", preset.Look!.Sync);
        Assert.Equal("sharplines", preset.Look.AnimateEffect);
        Assert.Equal(0, preset.Look.AnimateSlot);
        Assert.Equal(["breathing", "plasma", "rainbow"], doc.Ui.RecentAnimations);
        Assert.Equal("sharplines", doc.Panel.BackgroundEffect);
        Assert.Equal(0, doc.Panel.BackgroundTemplate);
        var record = doc.PanelDevices["panel-1"];
        Assert.Equal("rainbow", record.BackgroundEffect);
        Assert.Equal(0, record.BackgroundTemplate);
        Assert.Equal(0, record.BackgroundTemplates!["rainbow"]);
        Assert.False(record.BackgroundTemplates.ContainsKey("sweeprainbow"));
        Assert.Equal(1, record.BackgroundTemplates["plasma"]);
    }

    [Fact]
    public void Apply_rewrites_every_stored_field()
    {
        var doc = LegacyDoc();
        Assert.True(SimplePatternMigration.Apply(doc, shiftSlots: true));
        AssertMigrated(doc);
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        var doc = LegacyDoc();
        SimplePatternMigration.Apply(doc, shiftSlots: true);
        Assert.False(SimplePatternMigration.Apply(doc, shiftSlots: false));
        AssertMigrated(doc);
    }

    [Fact]
    public void Store_load_migrates_a_v27_settings_file_and_stamps_current()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(LegacyDoc(), Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        File.WriteAllText(SettingsPath, json);

        using var store = new JsonConfigStore(SettingsPath);
        var s = store.Load();

        Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
        AssertMigrated(s);
    }

    [Fact]
    public void Store_load_renames_a_retired_key_a_downgrade_wrote_back_at_the_current_schema()
    {
        File.WriteAllText(SettingsPath, $$"""{ "schemaVersion": {{NexusSettings.CurrentSchemaVersion}}, "lighting": { "sync": "sweepbars" } }""");

        using var store = new JsonConfigStore(SettingsPath);

        Assert.Equal("sharplines", store.Load().Lighting.Sync);
    }

    [Fact]
    public void A_reversed_simple_state_keeps_running_reversed_on_the_first_preset()
    {
        var doc = LegacyDoc();
        SimplePatternMigration.Apply(doc, shiftSlots: true);

        var animate = doc.Lighting.Animate;
        Assert.Equal(0, animate.Templates.GetValueOrDefault("rainbow")?.Selected ?? 0);
        var state = Assert.Contains("rainbow", animate.States);
        var stock = AnimateTemplateDefaults.Slot("rainbow", 0)!;
        Assert.Equal(-stock.Speed, state.Speed);
        Assert.Equal(stock.Params["u_tempo"], state.Params["u_tempo"]);
    }

    [Fact]
    public void A_forward_simple_state_leaves_no_delta_and_selects_the_first_preset()
    {
        var doc = new NexusSettings { SchemaVersion = 27 };
        doc.Lighting.Sync = "sweepbars";
        doc.Lighting.Animate.Effect = "sweepbars";
        doc.Lighting.Animate.States["sweepbars"] = new AnimateEffectState { Speed = 50 };
        doc.Lighting.Animate.Templates["sharplines"] = new AnimateEffectTemplates { Selected = 2 };

        SimplePatternMigration.Apply(doc, shiftSlots: true);

        Assert.False(doc.Lighting.Animate.States.ContainsKey("sharplines"));
        Assert.Equal(0, doc.Lighting.Animate.Templates.GetValueOrDefault("sharplines")?.Selected ?? 0);
    }

    [Fact]
    public void An_inactive_retired_state_does_not_override_the_advanced_selection()
    {
        var doc = new NexusSettings { SchemaVersion = 27 };
        doc.Lighting.Sync = "plasma";
        doc.Lighting.Animate.Effect = "plasma";
        doc.Lighting.Animate.States["sweeprainbow"] = new AnimateEffectState { Speed = -50 };
        doc.Lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates { Selected = 1 };

        SimplePatternMigration.Apply(doc, shiftSlots: true);

        Assert.False(doc.Lighting.Animate.States.ContainsKey("sweeprainbow"));
        Assert.Equal(2, doc.Lighting.Animate.Templates["rainbow"].Selected);
    }

    [Fact]
    public void Advanced_rainbow_slots_shift_up_with_the_old_look_translated_and_the_selection_following()
    {
        var edited = new AnimateEffectState { Speed = 70, Hue = 0.4f, Params = new() { ["u_density"] = 2f } };
        var doc = new NexusSettings { SchemaVersion = 27 };
        doc.Lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates
        {
            Selected = 1,
            Slots = [null, edited, null, new AnimateEffectState { Speed = 11 }],
        };
        doc.Lighting.Animate.States["rainbow"] = new AnimateEffectState { Speed = 33 };

        SimplePatternMigration.Apply(doc, shiftSlots: true);

        var bundle = doc.Lighting.Animate.Templates["rainbow"];
        Assert.Equal(2, bundle.Selected);
        var moved = bundle.Slots[2]!;
        Assert.Equal(70, moved.Speed);
        Assert.Equal(0.4f, moved.Hue);
        Assert.Equal(2f, moved.Params["u_density"]);
        Assert.Equal(1f, moved.Params["u_aspect"]);
        Assert.Equal(0.15f, moved.Params["u_tempo"]);
        Assert.Null(bundle.Slots.ElementAtOrDefault(0));
        Assert.True(bundle.Slots.Count <= 4);
        Assert.DoesNotContain(bundle.Slots, s => s is { Speed: 11 });
        var live = doc.Lighting.Animate.States["rainbow"];
        Assert.Equal(33, live.Speed);
        Assert.Equal(0.15f, live.Params["u_tempo"]);
    }

    [Fact]
    public void A_stored_stock_slot_prunes_to_the_new_default_after_the_shift()
    {
        var doc = new NexusSettings { SchemaVersion = 27 };
        var oldStockTwo = new AnimateEffectState
        {
            Speed = 30, Hue = 0.62f, Colorize = 0.75f,
            Params = new() { ["u_density"] = 0.5f, ["u_rotation"] = 0f },
        };
        doc.Lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates { Selected = 2, Slots = [null, null, oldStockTwo] };

        SimplePatternMigration.Apply(doc, shiftSlots: true);

        Assert.False(doc.Lighting.Animate.Templates.ContainsKey("rainbow") && doc.Lighting.Animate.Templates["rainbow"].Slots.Any(s => s is not null && s.Speed != 30));
        Assert.Equal(3, doc.Lighting.Animate.Templates["rainbow"].Selected);
    }

    [Fact]
    public void The_selection_of_the_dropped_fourth_slot_clamps_to_the_last_slot()
    {
        var doc = new NexusSettings { SchemaVersion = 27 };
        doc.Lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates { Selected = 3 };

        SimplePatternMigration.Apply(doc, shiftSlots: true);

        Assert.Equal(3, doc.Lighting.Animate.Templates["rainbow"].Selected);
    }

    [Fact]
    public void Panel_preset_picks_and_layout_looks_follow_the_rainbow_shift()
    {
        var doc = new NexusSettings { SchemaVersion = 27 };
        doc.PanelDevices["p"] = new PanelDeviceRecord
        {
            Id = "p",
            BackgroundEffect = "rainbow",
            BackgroundTemplate = 1,
            BackgroundTemplates = new() { ["rainbow"] = 1, ["breathing"] = 2 },
        };
        doc.Lighting.LayoutPresets.Add(new LayoutPreset
        {
            Id = "l",
            Look = new LightingPresetLook
            {
                AnimateEffect = "rainbow",
                AnimateSlot = 0,
                AnimateState = new AnimateEffectState { Speed = 40 },
            },
        });

        SimplePatternMigration.Apply(doc, shiftSlots: true);

        var record = doc.PanelDevices["p"];
        Assert.Equal(2, record.BackgroundTemplate);
        Assert.Equal(2, record.BackgroundTemplates!["rainbow"]);
        Assert.Equal(2, record.BackgroundTemplates["breathing"]);
        var look = doc.Lighting.LayoutPresets[0].Look!;
        Assert.Equal(1, look.AnimateSlot);
        Assert.Equal(0.15f, look.AnimateState!.Params["u_tempo"]);
    }

    [Fact]
    public void Profile_data_shifts_once_and_only_when_it_predates_the_schema_bump()
    {
        NexusSettings Doc(int version)
        {
            var d = new NexusSettings { SchemaVersion = version };
            d.Lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates { Selected = 0, Slots = [new AnimateEffectState { Speed = 61 }] };
            return d;
        }

        var legacy = Doc(0);
        SimplePatternMigration.ApplyProfile(legacy);
        SimplePatternMigration.ApplyProfile(legacy);
        Assert.Equal(1, legacy.Lighting.Animate.Templates["rainbow"].Selected);
        Assert.Equal(61, legacy.Lighting.Animate.Templates["rainbow"].Slots[1]!.Speed);
        Assert.Equal(SimplePatternMigration.SchemaVersion, legacy.SchemaVersion);

        var current = Doc(NexusSettings.CurrentSchemaVersion);
        SimplePatternMigration.ApplyProfile(current);
        Assert.Equal(0, current.Lighting.Animate.Templates["rainbow"].Selected);
    }

    [Fact]
    public void A_profile_already_carrying_the_merged_shader_params_is_not_shifted_again_whatever_its_stamp()
    {
        var shifted = new NexusSettings { SchemaVersion = 27 };
        shifted.Lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates
        {
            Selected = 2,
            Slots = [null, null, new AnimateEffectState { Speed = 61, Params = new() { ["u_aspect"] = 1f, ["u_tempo"] = 0.15f } }],
        };

        SimplePatternMigration.ApplyProfile(shifted);

        var bundle = shifted.Lighting.Animate.Templates["rainbow"];
        Assert.Equal(2, bundle.Selected);
        Assert.Equal(61, bundle.Slots[2]!.Speed);
    }

    [Fact]
    public void A_rainbow_panel_with_no_stored_preset_moves_to_the_old_first_look()
    {
        var doc = new NexusSettings { SchemaVersion = 27 };
        doc.PanelDevices["p"] = new PanelDeviceRecord { Id = "p", BackgroundEffect = "rainbow" };

        SimplePatternMigration.Apply(doc, shiftSlots: true);

        Assert.Equal(1, doc.PanelDevices["p"].BackgroundTemplate);
        Assert.Equal(1, doc.PanelDevices["p"].BackgroundTemplates!["rainbow"]);
    }

    [Fact]
    public void A_selection_on_the_dropped_fourth_rainbow_slot_keeps_its_look_as_the_live_state()
    {
        var stock = new NexusSettings { SchemaVersion = 27 };
        stock.Lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates { Selected = 3 };
        var edited = new NexusSettings { SchemaVersion = 27 };
        edited.Lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates
        {
            Selected = 3,
            Slots = [null, null, null, new AnimateEffectState { Speed = 11, Params = new() { ["u_density"] = 3f } }],
        };

        SimplePatternMigration.Apply(stock, shiftSlots: true);
        SimplePatternMigration.Apply(edited, shiftSlots: true);

        var fromStock = stock.Lighting.Animate.States["rainbow"];
        Assert.Equal(65, fromStock.Speed);
        Assert.Equal(1.6f, fromStock.Params["u_density"]);
        Assert.Equal(0.15f, fromStock.Params["u_tempo"]);
        var fromEdit = edited.Lighting.Animate.States["rainbow"];
        Assert.Equal(11, fromEdit.Speed);
        Assert.Equal(3f, fromEdit.Params["u_density"]);
        Assert.Equal(1f, fromEdit.Params["u_aspect"]);
    }

    [Theory]
    [InlineData("rainbow", 0f, true)]
    [InlineData("rainbow", 90f, false)]
    [InlineData("sharplines", 45f, false)]
    [InlineData("sharplines", 0f, true)]
    [InlineData("sweepbrush", 90f, true)]
    [InlineData("plasma", 0f, false)]
    public void Full_frame_sampling_yields_to_a_rotated_rainbow_or_sharplines(string key, float rotation, bool expected)
    {
        var extras = new Dictionary<string, float> { ["u_rotation"] = rotation };
        Assert.Equal(expected, Nexus.Service.Lighting.Engine.Gpu.ShaderLibrary.IsFullFrame(key, extras));
    }

    [Fact]
    public void A_named_effect_start_replays_the_selected_preset_not_the_shader_defaults()
    {
        var lighting = new LightingSettings();
        lighting.Animate.Templates["rainbow"] = new AnimateEffectTemplates { Selected = 2 };

        var start = AnimateTemplateDefaults.SavedStart(lighting, "rainbow");

        var slot = AnimateTemplateDefaults.Slot("rainbow", 2)!;
        Assert.Equal(slot.Speed, start.Speed);
        Assert.Equal(slot.Hue, start.Hue);
        Assert.Contains(start.Params, p => p.Name == "u_tempo" && p.Value == 0.15f);

        lighting.Animate.States["rainbow"] = new AnimateEffectState { Speed = -50 };
        Assert.Equal(-50, AnimateTemplateDefaults.SavedStart(lighting, "rainbow").Speed);
    }

    [Fact]
    public void Profile_import_and_export_round_trip_lands_on_the_new_keys()
    {
        using var store = new JsonConfigStore(SettingsPath);
        using var profiles = new ProfileManager(store);
        profiles.Initialize();
        var legacy = LegacyDoc();
        legacy.SchemaVersion = 0;

        var entry = profiles.ImportProfile("Legacy", legacy);
        var exported = profiles.ExportProfile(entry.Id)!;

        Assert.Equal("rainbow", exported.Lighting.Sync);
        Assert.Equal("sharplines", exported.Lighting.DevicePins["a"].Effect);
        Assert.Equal("sharplines", exported.Lighting.LayoutPresets[0].Look!.AnimateEffect);
    }

    [Fact]
    public void Deck_buttons_and_dials_rewrite_their_lighting_effect()
    {
        var button = new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "rgbEffect", Effect = "sweepbreathing" } };
        var nested = new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "rgbEffect", Effect = "sweepbars" } };
        var sequence = new DeckAction { Type = "sequence", Steps = [new DeckSequenceStep { Action = nested }] };
        var folderButton = new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "rgbEffect", Effect = "sweeprainbow" } };
        var untouched = new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "rgbEffect", Effect = "sweepcomet" } };
        var config = new DeckConfig
        {
            Pages =
            [
                new DeckPage
                {
                    Slots =
                    [
                        new DeckSlot { Action = button },
                        new DeckSlot { Action = sequence },
                        new DeckSlot { Folder = new DeckFolder { Slots = [new DeckSlot { Action = folderButton }] } },
                        new DeckSlot { Action = untouched },
                    ],
                },
            ],
        };
        var doc = new NexusSettings();
        doc.StreamDeck.Presets.Add(new DeckPreset { Id = "d", Deck = config });

        Assert.True(SimplePatternMigration.Apply(doc, shiftSlots: false));

        Assert.Equal("breathing", button.NexusAction!.Effect);
        Assert.Equal("sharplines", nested.NexusAction!.Effect);
        Assert.Equal("rainbow", folderButton.NexusAction!.Effect);
        Assert.Equal("sweepcomet", untouched.NexusAction!.Effect);
    }

    [Fact]
    public void Install_defaults_open_on_the_merged_rainbow()
    {
        Assert.Equal("rainbow", Nexus.Service.Defaults.InstallDefaults.Lighting.Sync);
        Assert.Equal("rainbow", Nexus.Service.Defaults.InstallDefaults.Lighting.Animate.Effect);
    }
}
