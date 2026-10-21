using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests;

public class AnimateTemplateDefaultsTests
{
    [Fact]
    public void Defaults_load_with_full_slots_per_effect_and_one_per_fill()
    {
        Assert.True(AnimateTemplateDefaults.All.Count >= 80);
        Assert.All(AnimateTemplateDefaults.All, kv =>
        {
            var (effect, bundle) = (kv.Key, kv.Value);
            Assert.Equal(0, bundle.Selected);
            // Simple fills are a single look with no preset slots; every other
            // effect carries the full preset row.
            Assert.Equal(StaticEffectCatalog.IsFill(effect) ? 1 : AnimateTemplateDefaults.SlotCount, bundle.Slots.Count);
            Assert.All(bundle.Slots, slot => Assert.NotNull(slot));
        });
    }

    [Fact]
    public void Defaults_carry_known_signature_looks()
    {
        // Values pinned from the table this data was generated from
        // (nexus-web lightingTemplates.ts at the ownership handover).
        var fire = AnimateTemplateDefaults.Slot("fire", 0)!;
        Assert.Equal(0.03f, fire.Hue, 3);
        Assert.Equal(0.80f, fire.Colorize, 3);
        Assert.Equal(70, fire.Speed);
        Assert.Equal(1.6f, fire.Params["u_turbulence"], 3);

        // Simple fills carry a single look; any slot index clamps to it.
        var white = AnimateTemplateDefaults.Slot("simplewhite", 3)!;
        Assert.Equal(0f, white.Params["u_warmth"], 3);
        Assert.Equal(0f, white.Saturation, 3);

        // Coloured signature: slot 1 is the full-rainbow variant.
        var fireRainbow = AnimateTemplateDefaults.Slot("fire", 1)!;
        Assert.Equal(0f, fireRainbow.Hue, 3);
        Assert.Equal(0f, fireRainbow.Colorize, 3);
    }

    [Fact]
    public void Prune_drops_bundles_equal_to_defaults()
    {
        var dense = CloneDefaults();
        var pruned = AnimateTemplateDefaults.Prune(dense);
        Assert.Empty(pruned);
    }

    [Fact]
    public void Prune_keeps_edited_and_selected_slots_and_trims_trailing_nulls()
    {
        var dense = CloneDefaults();
        dense["fire"].Slots[2]!.Params["u_turbulence"] = 9.9f;
        var pruned = AnimateTemplateDefaults.Prune(dense);

        var bundle = Assert.Single(pruned).Value;
        Assert.Equal(3, bundle.Slots.Count);
        // Slot 0 is the selected slot of a kept bundle: stays dense for pre-v9
        // readers that dereference Slots[Selected] unguarded.
        Assert.True(AnimateTemplateDefaults.StateEquals(bundle.Slots[0]!, AnimateTemplateDefaults.Slot("fire", 0)!));
        Assert.Null(bundle.Slots[1]);
        Assert.Equal(9.9f, bundle.Slots[2]!.Params["u_turbulence"], 3);
    }

    [Fact]
    public void Prune_keeps_nondefault_selected_slot_index_with_dense_selected_slot()
    {
        var dense = CloneDefaults();
        dense["wave"].Selected = 2;
        var pruned = AnimateTemplateDefaults.Prune(dense);

        var (effect, bundle) = Assert.Single(pruned);
        Assert.Equal("wave", effect);
        Assert.Equal(2, bundle.Selected);
        Assert.Equal(3, bundle.Slots.Count);
        Assert.Null(bundle.Slots[0]);
        Assert.Null(bundle.Slots[1]);
        Assert.True(AnimateTemplateDefaults.StateEquals(bundle.Slots[2]!, AnimateTemplateDefaults.Slot("wave", 2)!));
    }

    [Fact]
    public void Prune_densifies_null_selected_slot_of_sparse_input_without_aliasing_defaults()
    {
        // A post-v9 profile file can round-trip an already-sparse bundle whose
        // selected slot is null; prune must materialize it (dense-selected
        // guarantee) as a copy, never the shared canonical instance.
        var sparse = new Dictionary<string, AnimateEffectTemplates>
        {
            ["fire"] = new()
            {
                Selected = 1,
                Slots = new() { null, null, new AnimateEffectState { Speed = 7 } },
            },
        };
        var pruned = AnimateTemplateDefaults.Prune(sparse);

        var bundle = pruned["fire"];
        var canonical = AnimateTemplateDefaults.Slot("fire", 1)!;
        Assert.NotNull(bundle.Slots[1]);
        Assert.NotSame(canonical, bundle.Slots[1]);
        Assert.True(AnimateTemplateDefaults.StateEquals(canonical, bundle.Slots[1]!));
        Assert.Equal(7, bundle.Slots[2]!.Speed);
    }

    [Fact]
    public void Prune_tolerates_null_input()
    {
        Assert.Empty(AnimateTemplateDefaults.Prune(null));
    }

    [Fact]
    public void Prune_drops_a_dense_multi_slot_fill_bundle_equal_to_the_single_canonical_look()
    {
        // A store written when fills carried a full preset row holds 4 dense
        // slots; the clamped Slot() lookup must recognize them all as default
        // so the bundle vanishes instead of surviving as fake user deltas.
        var canonical = AnimateTemplateDefaults.Slot("simplered", 0)!;
        var dense = new Dictionary<string, AnimateEffectTemplates>
        {
            ["simplered"] = new()
            {
                Selected = 0,
                Slots = new() { Clone(canonical), Clone(canonical), Clone(canonical), Clone(canonical) },
            },
        };
        Assert.Empty(AnimateTemplateDefaults.Prune(dense));
    }

    [Fact]
    public void Prune_is_idempotent_for_all_default_sparse_bundles()
    {
        // A post-v9 profile can carry a bundle whose every slot is a null
        // placeholder; that is semantically all-default, so it drops instead of
        // pinning a copy of today's default into slot 0.
        var sparse = new Dictionary<string, AnimateEffectTemplates>
        {
            ["fire"] = new() { Selected = 0, Slots = new() { null, null, null, null } },
        };
        Assert.Empty(AnimateTemplateDefaults.Prune(sparse));
    }

    [Fact]
    public void Prune_pads_truncated_input_to_densify_the_true_selected_index()
    {
        var sparse = new Dictionary<string, AnimateEffectTemplates>
        {
            ["fire"] = new() { Selected = 2, Slots = new() { null, null } },
        };
        var bundle = AnimateTemplateDefaults.Prune(sparse)["fire"];

        Assert.Equal(3, bundle.Slots.Count);
        Assert.Null(bundle.Slots[0]);
        Assert.Null(bundle.Slots[1]);
        Assert.True(AnimateTemplateDefaults.StateEquals(bundle.Slots[2]!, AnimateTemplateDefaults.Slot("fire", 2)!));
    }

    [Fact]
    public void Prune_keeps_unknown_effects_verbatim()
    {
        var custom = new AnimateEffectTemplates
        {
            Selected = 1,
            Slots = new() { new AnimateEffectState { Speed = 10 } },
        };
        var pruned = AnimateTemplateDefaults.Prune(new() { ["someneweffect"] = custom });
        Assert.Same(custom, pruned["someneweffect"]);
    }

    [Fact]
    public void Prune_then_default_fallback_reproduces_every_dense_slot()
    {
        var dense = CloneDefaults();
        dense["fire"].Slots[1]!.Hue = 0.42f;
        dense["wave"].Selected = 3;
        var pruned = AnimateTemplateDefaults.Prune(dense);

        foreach (var (effect, bundle) in dense)
        {
            for (var i = 0; i < bundle.Slots.Count; i++)
            {
                var stored = pruned.TryGetValue(effect, out var p) && i < p.Slots.Count ? p.Slots[i] : null;
                var effective = stored ?? AnimateTemplateDefaults.Slot(effect, i);
                Assert.True(AnimateTemplateDefaults.StateEquals(bundle.Slots[i]!, effective!),
                    $"{effect} slot {i} did not survive the prune round-trip");
            }
        }
    }

    [Fact]
    public void StateEquals_tolerates_float_roundtrip_noise_but_not_key_changes()
    {
        var a = AnimateTemplateDefaults.Slot("fire", 0)!;
        var b = Clone(a);
        b.Hue += 5e-5f;
        Assert.True(AnimateTemplateDefaults.StateEquals(a, b));

        b.Params.Remove("u_turbulence");
        Assert.False(AnimateTemplateDefaults.StateEquals(a, b));
    }

    [Fact]
    public void Migration_v9_prunes_materialized_default_templates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-tpl-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new NexusSettings { SchemaVersion = 8 };
            settings.Lighting.Animate.Templates = CloneDefaults();
            settings.Lighting.Animate.Templates["fire"].Slots[0]!.Speed = 99;
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings));

            var store = new JsonConfigStore(path);
            var loaded = store.Load();

            Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            var bundle = Assert.Single(loaded.Lighting.Animate.Templates).Value;
            Assert.Equal(99, bundle.Slots[0]!.Speed);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Migration_v9_tolerates_explicit_null_templates_without_corrupt_reset()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-tpl-null-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new NexusSettings { SchemaVersion = 8, OnboardingCompleted = true };
            var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings)
                .Replace("\"templates\": {}", "\"templates\": null");
            Assert.Contains("\"templates\": null", json);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, json);

            var store = new JsonConfigStore(path);
            var loaded = store.Load();

            Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            // A throw during Migrate would route through the corrupt-file path
            // and reset user state; OnboardingCompleted surviving proves it didn't.
            Assert.True(loaded.OnboardingCompleted);
            Assert.False(File.Exists(path + ".corrupt"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Serialized_defaults_roundtrip_and_etag_is_stable()
    {
        var parsed = JsonSerializer.Deserialize(AnimateTemplateDefaults.SerializedJson,
            AppJsonContext.Default.DictionaryStringAnimateEffectTemplates);
        Assert.NotNull(parsed);
        Assert.Equal(AnimateTemplateDefaults.All.Count, parsed!.Count);
        Assert.StartsWith("\"", AnimateTemplateDefaults.ETag);
        Assert.Equal(AnimateTemplateDefaults.ETag, AnimateTemplateDefaults.ETag);
    }

    private static Dictionary<string, AnimateEffectTemplates> CloneDefaults()
        => AnimateTemplateDefaults.All.ToDictionary(
            kv => kv.Key,
            kv => new AnimateEffectTemplates
            {
                Selected = kv.Value.Selected,
                Slots = kv.Value.Slots.Select(s => s is null ? null : Clone(s)).ToList(),
            });

    private static AnimateEffectState Clone(AnimateEffectState s) => new()
    {
        Speed = s.Speed,
        Intensity = s.Intensity,
        Hue = s.Hue,
        Colorize = s.Colorize,
        Saturation = s.Saturation,
        Contrast = s.Contrast,
        Params = new Dictionary<string, float>(s.Params),
        Sensors = Nexus.Service.Persistence.SensorBinding.CopyMap(s.Sensors),
    };
}
