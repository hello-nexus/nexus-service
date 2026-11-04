using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Activation states are deltas: merely starting an effect at its resolved
/// selected-slot look must not persist a dense state entry, and the v10
/// migration removes entries older builds wrote for default looks.
/// </summary>
public class AnimateStateDeltaTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly LightingOutputHub _hub;
    private readonly GpuContext _gpu;
    private readonly LightingProvider _provider;

    public AnimateStateDeltaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-statedelta-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _engine = new LightingEngine();
        _hub = new LightingOutputHub();
        _gpu = new GpuContext(160, 90);
        _provider = new LightingProvider(_store, _engine, _hub, _gpu, new MediaLibrary(), new Nexus.Service.Platform.DefaultMonitorEnumerator());
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static AnimateHeadlessStart BodyFrom(string effect, AnimateEffectState look) => new()
    {
        Effect = effect,
        Speed = look.Speed,
        Intensity = look.Intensity,
        Hue = look.Hue,
        Colorize = look.Colorize,
        Saturation = look.Saturation,
        Contrast = look.Contrast,
        Persist = true,
        Params = look.Params.Select(kv => new ShaderParam { Name = kv.Key, Value = kv.Value }).ToList(),
    };

    [Theory]
    [InlineData("synthwave", false)]
    [InlineData("stripes", true)]
    public async System.Threading.Tasks.Task Mcp_scenario_with_tuned_state_leaves_persisted_state_unchanged(string effect, bool isStatic)
    {
        var tuned = new AnimateEffectState
        {
            Speed = isStatic ? 0 : 72,
            Hue = 0.88f,
            Colorize = 0.3f,
            Saturation = 1.25f,
            Contrast = 2.53f,
            Params = new() { ["u_tuned"] = 36f },
        };
        _store.Update(s => (isStatic ? s.Lighting.Static.States : s.Lighting.Animate.States)[effect] = tuned);
        var tool = new Nexus.Service.Mcp.Tools.ApplyLightingScenarioTool(_provider, _store, new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { scenario = effect }), System.Threading.CancellationToken.None);

        Assert.False(result.IsError);
        var l = _store.Load().Lighting;
        var kept = (isStatic ? l.Static.States : l.Animate.States)[effect];
        Assert.Equal(0.88f, kept.Hue, 3);
        Assert.Equal(1.25f, kept.Saturation, 3);
        Assert.Equal(2.53f, kept.Contrast, 3);
        Assert.Equal(36f, kept.Params["u_tuned"], 3);
    }

    [Fact]
    public void Activating_the_default_look_stores_no_state_entry()
    {
        var canon = AnimateTemplateDefaults.Slot("fire", 0)!;
        _provider.StartAnimate(BodyFrom("fire", canon));

        Assert.False(_store.Load().Lighting.Animate.States.ContainsKey("fire"));
        Assert.Equal("fire", _store.Load().Lighting.Animate.Effect);
    }

    [Fact]
    public void Custom_look_stores_a_state_entry_and_returning_to_default_removes_it()
    {
        var canon = AnimateTemplateDefaults.Slot("fire", 0)!;
        var custom = new AnimateEffectState
        {
            Speed = canon.Speed,
            Intensity = canon.Intensity,
            Hue = 0.42f,
            Colorize = canon.Colorize,
            Saturation = canon.Saturation,
            Contrast = canon.Contrast,
            Params = new Dictionary<string, float>(canon.Params),
        };
        _provider.StartAnimate(BodyFrom("fire", custom));
        Assert.Equal(0.42f, _store.Load().Lighting.Animate.States["fire"].Hue, 3);

        _provider.StartAnimate(BodyFrom("fire", canon));
        Assert.False(_store.Load().Lighting.Animate.States.ContainsKey("fire"));
    }

    [Fact]
    public void Default_look_is_judged_against_the_stored_slot_delta_when_present()
    {
        // A user-edited selected slot becomes the baseline: replaying THAT look
        // stores nothing, while the canonical look now differs and persists.
        var edited = AnimateTemplateDefaultsTestsHelpers.Clone(AnimateTemplateDefaults.Slot("wave", 0)!);
        edited.Hue = 0.9f;
        _store.Update(s => s.Lighting.Animate.Templates["wave"] = new AnimateEffectTemplates
        {
            Selected = 0,
            Slots = new() { edited },
        });

        _provider.StartAnimate(BodyFrom("wave", edited));
        Assert.False(_store.Load().Lighting.Animate.States.ContainsKey("wave"));

        _provider.StartAnimate(BodyFrom("wave", AnimateTemplateDefaults.Slot("wave", 0)!));
        Assert.True(_store.Load().Lighting.Animate.States.ContainsKey("wave"));
    }

    [Fact]
    public void Migration_v10_prunes_states_equal_to_the_resolved_look()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-v10-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new NexusSettings { SchemaVersion = 9 };
            settings.Lighting.Animate.States["fire"] = AnimateTemplateDefaultsTestsHelpers.Clone(AnimateTemplateDefaults.Slot("fire", 0)!);
            var custom = AnimateTemplateDefaultsTestsHelpers.Clone(AnimateTemplateDefaults.Slot("wave", 0)!);
            custom.Speed = 7;
            settings.Lighting.Animate.States["wave"] = custom;
            settings.Lighting.Animate.States["someneweffect"] = new AnimateEffectState { Speed = 3 };
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings));

            var loaded = new JsonConfigStore(path).Load();

            Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.False(loaded.Lighting.Animate.States.ContainsKey("fire"));
            Assert.Equal(7, loaded.Lighting.Animate.States["wave"].Speed);
            Assert.Equal(3, loaded.Lighting.Animate.States["someneweffect"].Speed);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Intensity_zero_baseline_still_matches_the_coerced_replay()
    {
        // StartAnimate coerces intensity <= 0 to 1 before persisting; a slot
        // saved at intensity 0 must still count as "the default look" when
        // replayed, not pin a permanently dense state entry.
        var slot = AnimateTemplateDefaultsTestsHelpers.Clone(AnimateTemplateDefaults.Slot("fire", 0)!);
        slot.Intensity = 0f;
        _store.Update(s => s.Lighting.Animate.Templates["fire"] = new AnimateEffectTemplates
        {
            Selected = 0,
            Slots = new() { slot },
        });

        _provider.StartAnimate(BodyFrom("fire", slot));
        Assert.False(_store.Load().Lighting.Animate.States.ContainsKey("fire"));
    }

    [Fact]
    public void Null_slots_list_inside_a_bundle_is_tolerated_by_prune_and_resolve()
    {
        var templates = new Dictionary<string, AnimateEffectTemplates>
        {
            ["fire"] = new() { Selected = 1, Slots = null! },
        };
        var resolved = AnimateTemplateDefaults.ResolveSelected(templates, "fire");
        Assert.True(AnimateTemplateDefaults.StateEquals(resolved!, AnimateTemplateDefaults.Slot("fire", 1)!));

        var pruned = AnimateTemplateDefaults.Prune(templates);
        // Selected != 0 keeps the bundle; the dense-selected guarantee is
        // materialized despite the null input list.
        Assert.NotNull(pruned["fire"].Slots[1]);
    }

    [Fact]
    public void PruneStates_tolerates_null_templates_and_null_entries()
    {
        var animate = new AnimateSettings();
        animate.Templates = null!;
        animate.States["fire"] = AnimateTemplateDefaultsTestsHelpers.Clone(AnimateTemplateDefaults.Slot("fire", 0)!);
        var custom = AnimateTemplateDefaultsTestsHelpers.Clone(AnimateTemplateDefaults.Slot("wave", 0)!);
        custom.Hue = 0.77f;
        animate.States["wave"] = custom;
        animate.States["broken"] = null!;

        AnimateTemplateDefaults.PruneStates(animate);

        Assert.False(animate.States.ContainsKey("fire"));
        Assert.False(animate.States.ContainsKey("broken"));
        Assert.Equal(0.77f, animate.States["wave"].Hue, 3);
    }

    [Fact]
    public void Migration_v10_tolerates_explicit_null_templates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-v10null-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new NexusSettings { SchemaVersion = 9, OnboardingCompleted = true };
            settings.Lighting.Animate.States["fire"] = AnimateTemplateDefaultsTestsHelpers.Clone(AnimateTemplateDefaults.Slot("fire", 0)!);
            var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings)
                .Replace("\"templates\": {}", "\"templates\": null");
            Assert.Contains("\"templates\": null", json);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, json);

            var loaded = new JsonConfigStore(path).Load();

            Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.True(loaded.OnboardingCompleted);
            Assert.False(File.Exists(path + ".corrupt"));
            Assert.False(loaded.Lighting.Animate.States.ContainsKey("fire"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveSelected_prefers_stored_delta_and_falls_back_to_canon()
    {
        var templates = new Dictionary<string, AnimateEffectTemplates>();
        var canon = AnimateTemplateDefaults.ResolveSelected(templates, "fire")!;
        Assert.True(AnimateTemplateDefaults.StateEquals(canon, AnimateTemplateDefaults.Slot("fire", 0)!));

        var edited = AnimateTemplateDefaultsTestsHelpers.Clone(canon);
        edited.Speed = 99;
        templates["fire"] = new AnimateEffectTemplates { Selected = 2, Slots = new() { null, null, edited } };
        Assert.Equal(99, AnimateTemplateDefaults.ResolveSelected(templates, "fire")!.Speed);

        Assert.Null(AnimateTemplateDefaults.ResolveSelected(templates, "notaneffect"));
    }
}

internal static class AnimateTemplateDefaultsTestsHelpers
{
    public static AnimateEffectState Clone(AnimateEffectState s) => new()
    {
        Speed = s.Speed,
        Intensity = s.Intensity,
        Hue = s.Hue,
        Colorize = s.Colorize,
        Saturation = s.Saturation,
        Contrast = s.Contrast,
        Params = new Dictionary<string, float>(s.Params),
    };
}
