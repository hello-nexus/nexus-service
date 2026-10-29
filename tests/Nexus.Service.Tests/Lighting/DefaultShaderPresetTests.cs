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
        "lavalamp", "radar", "voronoi", "circuit", "sandstorm", "bubbles", "flowfield", "vapor", "lavafissure",
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
        // Ball reads colorize as the color wheel width, so its presets narrow it on purpose.
        if (effect == "ball")
        {
            Assert.InRange(looks[0].Colorize, 0.5f, 0.9f);
            Assert.Contains(looks, l => l.Colorize < 0.001f);
        }
        else
        {
            Assert.All(looks, l => Assert.Equal(0f, l.Colorize, 3));
        }
        Assert.All(looks, l => Assert.InRange(l.Saturation, 1f, 1.5f));
        // Every preset is its own look: no two share palette, speed and parameters.
        var signatures = looks.Select(l => string.Join("|", new object[] { System.Math.Round(l.Hue, 2), l.Speed }
            .Concat(l.Params.OrderBy(kv => kv.Key).Select(kv => (object)$"{kv.Key}={kv.Value}")))).Distinct().Count();
        Assert.Equal(looks.Count, signatures);
        // Palette alone tells the presets apart: no two share both mood and hue.
        var palettes = looks.Select(l => (l.Params.GetValueOrDefault("u_mood"), System.Math.Round(l.Hue, 2))).Distinct().Count();
        Assert.Equal(looks.Count, palettes);
    }

    private static bool IsBase(string name) =>
        ShaderEffect.BaseParamNames.Contains(name) || name == "u_paletteMix";
}
