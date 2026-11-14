using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>The fill-level sensor effects color their unlit area from u_fill_bgHue/Sat/Val: black when a state carries none, a per-effect dim default in the shipped presets.</summary>
public class SensorFillBackgroundTests
{
    public static TheoryData<string> Fills => new() { "sensorbar", "sensorclock", "sensorsegments", "sensorliquid", "sensorcenter" };

    private static readonly string[] BgParams = { "u_fill_bgHue", "u_fill_bgSat", "u_fill_bgVal" };

    [Theory]
    [MemberData(nameof(Fills))]
    public void Shader_defaults_to_black_and_mixes_the_background_by_coverage_after_the_wheel(string effect)
    {
        var specs = ShaderLibrary.Params(effect);
        foreach (var name in BgParams)
        {
            Assert.Equal(0f, specs[name].Default);
            Assert.Equal(0f, specs[name].Min);
            Assert.Equal(1f, specs[name].Max);
        }
        var src = ShaderLibrary.Get(effect);
        var mix = src.IndexOf("col += fieldBg * ((1.0 - fieldCover) * (u_col_shade));", System.StringComparison.Ordinal);
        Assert.True(mix > src.IndexOf("col = hsv2rgb(", System.StringComparison.Ordinal), effect);
        Assert.Contains("fieldBg = (hsv2rgb(vec3(u_fill_bgHue, u_fill_bgSat, u_fill_bgVal)));", src);
    }

    [Theory]
    [MemberData(nameof(Fills))]
    public void Every_preset_carries_one_dim_background_for_the_effect(string effect)
    {
        var looks = Enumerable.Range(0, AnimateTemplateDefaults.SlotCount).Select(s => AnimateTemplateDefaults.Slot(effect, s)!).ToList();
        foreach (var name in BgParams)
        {
            Assert.All(looks, l => Assert.InRange(l.Params[name], 0f, 1f));
            Assert.Single(looks.Select(l => l.Params[name]).Distinct());
        }
        // Dim enough that the fill and any widget on top stay readable.
        Assert.All(looks, l => Assert.InRange(l.Params["u_fill_bgVal"], 0f, 0.15f));
    }

    [Theory]
    [InlineData("sensorshift")]
    [InlineData("sensorheat")]
    public void Effects_without_an_unlit_area_have_no_background(string effect)
    {
        Assert.DoesNotContain("u_fill_bg", ShaderLibrary.Get(effect));
        Assert.All(Enumerable.Range(0, AnimateTemplateDefaults.SlotCount),
            s => Assert.DoesNotContain(AnimateTemplateDefaults.Slot(effect, s)!.Params.Keys, k => k.StartsWith("u_fill_bg", System.StringComparison.Ordinal)));
    }

    // The v24 upgrade fixture's shape: the selected preset slot stored densely, before the background keys existed.
    private static AnimateEffectState StoredBeforeBackground(string effect, int slot, float? hue = null)
    {
        var preset = AnimateTemplateDefaults.Slot(effect, slot)!;
        return new AnimateEffectState
        {
            Speed = preset.Speed,
            Intensity = preset.Intensity,
            Hue = hue ?? preset.Hue,
            Colorize = preset.Colorize,
            Saturation = preset.Saturation,
            Contrast = preset.Contrast,
            Params = preset.Params.Where(kv => !BgParams.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            Sensors = preset.Sensors,
        };
    }

    [Theory]
    [InlineData("sensorbar")]
    [InlineData("sensorsegments")]
    public void A_slot_stored_without_the_background_takes_the_preset_value_and_still_equals_its_default(string effect)
    {
        var preset = AnimateTemplateDefaults.Slot(effect, 1)!;
        var templates = new Dictionary<string, AnimateEffectTemplates>
        {
            [effect] = new() { Selected = 1, Slots = new() { null, StoredBeforeBackground(effect, 1), StoredBeforeBackground(effect, 2) } },
        };

        var resolved = AnimateTemplateDefaults.ResolveSlot(templates, effect, 1)!;
        foreach (var name in BgParams)
        {
            Assert.Equal(preset.Params[name], resolved.Params[name]);
        }
        Assert.True(AnimateTemplateDefaults.StateEquals(resolved, preset));
        Assert.DoesNotContain("u_fill_bgVal", templates[effect].Slots[1]!.Params.Keys);

        // The untouched unselected slot prunes away; the selected one stays dense by design.
        var pruned = AnimateTemplateDefaults.Prune(templates)[effect];
        Assert.Equal(2, pruned.Slots.Count);

        // An activation state equal to that slot is redundant, and one with an edit gains the preset background on replay.
        var animate = new AnimateSettings { Effect = effect, Templates = templates };
        animate.States[effect] = StoredBeforeBackground(effect, 1);
        AnimateTemplateDefaults.PruneStates(animate);
        Assert.False(animate.States.ContainsKey(effect));
        var lighting = new LightingSettings { Animate = animate };
        animate.States[effect] = StoredBeforeBackground(effect, 1, hue: 0.9f);
        var start = AnimateTemplateDefaults.SavedStart(lighting, effect);
        Assert.Equal(0.9f, start.Hue, 3);
        foreach (var name in BgParams)
        {
            Assert.Equal(preset.Params[name], start.Params.Single(p => p.Name == name).Value);
        }
    }
}
