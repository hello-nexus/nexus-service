using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class DefaultShaderPresetTests
{
    public static TheoryData<string> Effects => new()
    {
        "cosmicdust", "hyperspace", "nebula", "starfield", "ball",
        "wave", "ferrofluid", "inkbloom", "liquidchrome", "jellyfish", "sacredgeometry",
    };

    [Theory]
    [MemberData(nameof(Effects))]
    public void Presets_use_only_declared_params_inside_their_ranges(string effect)
    {
        var specs = ShaderLibrary.Params(effect);
        for (var slot = 0; slot < AnimateTemplateDefaults.SlotCount; slot++)
        {
            var look = AnimateTemplateDefaults.Slot(effect, slot)!;
            Assert.Equal(specs.Keys.Where(k => !IsBase(k)).OrderBy(k => k), look.Params.Keys.OrderBy(k => k));
            Assert.InRange(look.Speed / 50f, specs["u_speed"].Min, specs["u_speed"].Max);
            Assert.InRange(look.Hue, specs["u_hue"].Min, specs["u_hue"].Max);
            Assert.InRange(look.Saturation, specs["u_saturation"].Min, specs["u_saturation"].Max);
            Assert.InRange(look.Contrast, specs["u_contrast"].Min, specs["u_contrast"].Max);
            foreach (var (name, value) in look.Params)
            {
                var spec = specs[name];
                Assert.InRange(value, spec.Min, spec.Max);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public void Presets_are_full_color_and_distinct(string effect)
    {
        var looks = Enumerable.Range(0, AnimateTemplateDefaults.SlotCount)
            .Select(s => AnimateTemplateDefaults.Slot(effect, s)!).ToList();
        // Colorize collapses the palette to one tint, so a vivid default keeps it off.
        Assert.All(looks, l => Assert.Equal(0f, l.Colorize, 3));
        Assert.All(looks, l => Assert.True(l.Saturation >= 1f));
        // No two presets share a palette position.
        Assert.Equal(looks.Count, looks.Select(l => System.Math.Round(l.Hue, 2)).Distinct().Count());
    }

    private static bool IsBase(string name) =>
        ShaderEffect.BaseParamNames.Contains(name) || name == "u_paletteMix";
}
