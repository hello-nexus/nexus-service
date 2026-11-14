using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
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
        var mix = src.IndexOf("col += fieldBg * (1.0 - fieldCover);", System.StringComparison.Ordinal);
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
}
