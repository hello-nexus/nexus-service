using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>Sandstorm and Vapor take every color from the color wheel (hue is the middle, 1 - colorize the width), with no preset-only palette.</summary>
public class WheelDrivenShaderTests
{
    [Theory]
    [InlineData("sandstorm")]
    [InlineData("vapor")]
    public void Shader_has_no_palette_param_and_reads_the_wheel(string effect)
    {
        // The shared prelude still offers moodPalette to other effects, so only the effect's own body counts.
        var src = ShaderLibrary.Get(effect);
        var body = src[src.IndexOf("// ---- effect body ----", System.StringComparison.Ordinal)..];
        Assert.DoesNotContain("u_mood", body);
        Assert.DoesNotContain("moodPalette", body);
        Assert.Contains("u_colorize", body);
        Assert.DoesNotContain("u_mood", ShaderLibrary.Params(effect).Keys);
    }

    [Theory]
    [InlineData("sandstorm")]
    [InlineData("vapor")]
    public void Presets_move_the_wheel_and_carry_no_mood(string effect)
    {
        var looks = Enumerable.Range(0, AnimateTemplateDefaults.SlotCount).Select(s => AnimateTemplateDefaults.Slot(effect, s)!).ToList();
        Assert.All(looks, l => Assert.DoesNotContain("u_mood", l.Params.Keys));
        Assert.Equal(looks.Count, looks.Select(l => (System.Math.Round(l.Hue, 2), System.Math.Round(l.Colorize, 2))).Distinct().Count());
        // Every preset narrows the wheel: none is the full rainbow or a bare single hue.
        Assert.All(looks, l => Assert.InRange(l.Colorize, 0.4f, 0.95f));
    }

    [Theory]
    [InlineData("sandstorm")]
    [InlineData("vapor")]
    public void Stale_mood_param_in_saved_state_is_inert(string effect)
    {
        var look = AnimateTemplateDefaults.Slot(effect, 0)!;
        var state = new AnimateEffectState { Speed = look.Speed, Hue = look.Hue, Colorize = look.Colorize };
        foreach (var kv in look.Params)
        {
            state.Params[kv.Key] = kv.Value;
        }
        state.Params["u_mood"] = 6f;
        ShaderParamSpec.Sanitize(effect, state);
        // Sanitize clamps only declared params and leaves others alone; the renderer sets a uniform only when the program declares it.
        Assert.False(ShaderLibrary.Params(effect).ContainsKey("u_mood"));
        foreach (var kv in look.Params)
        {
            Assert.Equal(kv.Value, state.Params[kv.Key], 3);
        }
    }

    [Fact]
    public void Vapor_wraps_its_clock_so_the_noise_hash_keeps_precision()
    {
        Assert.Matches(@"mod\(\s*u_time\s*\*\s*u_speed", ShaderLibrary.Get("vapor"));
    }
}
