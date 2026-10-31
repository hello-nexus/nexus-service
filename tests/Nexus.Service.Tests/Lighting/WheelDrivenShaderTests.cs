using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>The color wheel (u_hue is the arc's middle, 1 - u_colorize its width) is the only palette source: no effect carries a hidden palette param.</summary>
public class WheelDrivenShaderTests
{
    public static TheoryData<string> FormerMoodEffects => new()
    {
        "bubbles", "circuit", "cosmicdust", "ferrofluid", "flowfield", "hyperspace", "inkbloom", "jellyfish",
        "lavafissure", "liquidchrome", "nebula", "sacredgeometry", "sandstorm", "starfield", "vapor", "voronoi", "wave",
    };

    [Fact]
    public void No_effect_declares_a_mood_param_and_the_prelude_has_no_fixed_palettes()
    {
        foreach (var key in ShaderLibrary.AllEffectKeys)
        {
            Assert.DoesNotContain("u_mood", ShaderLibrary.Get(key));
            Assert.False(ShaderLibrary.Params(key).ContainsKey("u_mood"), key);
        }
        Assert.DoesNotContain("moodPalette", ShaderLibrary.Get("sandstorm"));
    }

    [Theory]
    [MemberData(nameof(FormerMoodEffects))]
    public void Former_mood_effects_color_from_the_wheel(string effect)
    {
        var src = ShaderLibrary.Get(effect);
        Assert.Contains("wheelColor(", src[src.IndexOf("// ---- effect body ----", System.StringComparison.Ordinal)..]);
        Assert.Contains("finish(", src[src.IndexOf("// ---- effect body ----", System.StringComparison.Ordinal)..]);
    }

    [Theory]
    [MemberData(nameof(FormerMoodEffects))]
    public void Presets_move_the_wheel_and_carry_no_mood(string effect)
    {
        var looks = Enumerable.Range(0, AnimateTemplateDefaults.SlotCount).Select(s => AnimateTemplateDefaults.Slot(effect, s)!).ToList();
        Assert.All(looks, l => Assert.DoesNotContain("u_mood", l.Params.Keys));
        Assert.Equal(looks.Count, looks.Select(l => (System.Math.Round(l.Hue, 2), System.Math.Round(l.Colorize, 2))).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(FormerMoodEffects))]
    public void Stale_mood_param_in_saved_state_clamps_cleanly_and_matches_no_uniform(string effect)
    {
        var look = AnimateTemplateDefaults.Slot(effect, 0)!;
        var state = new AnimateEffectState { Speed = look.Speed, Hue = look.Hue, Colorize = look.Colorize };
        foreach (var kv in look.Params)
        {
            state.Params[kv.Key] = kv.Value;
        }
        state.Params["u_mood"] = 6f;
        ShaderParamSpec.Sanitize(effect, state);
        // Sanitize clamps declared params only, so a stale key survives untouched; with no u_mood in the program it names no uniform.
        Assert.Equal(6f, state.Params["u_mood"]);
        Assert.DoesNotContain("uniform float u_mood", ShaderLibrary.Get(effect));
        foreach (var kv in look.Params)
        {
            Assert.Equal(kv.Value, state.Params[kv.Key], 3);
        }
    }

    [Fact]
    public void Vapor_wraps_its_clock_so_the_noise_hash_keeps_precision()
    {
        Assert.Matches(@"mod\(\s*u_time\s*\*\s*u_speed\s*\*\s*0\.6\s*,", ShaderLibrary.Get("vapor"));
    }
}
