using Nexus.Service.Lighting;
using Nexus.Service.Lighting.CustomEffects;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Tests.Lighting.CustomEffects;

namespace Nexus.Service.Tests.Lighting.Sensors;

public sealed class SensorUniformSpecTests
{
    private const string CpuLine =
        "uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"summary/cpu-usage\",\"min\":0,\"max\":100}\n";

    [Fact]
    public void A_well_formed_annotation_yields_the_uniform_and_its_default_binding()
    {
        var specs = SensorUniformSpec.Parse("#version 330 core\n" + CpuLine + "void main() {}\n");

        var spec = Assert.Single(specs);
        Assert.Equal("u_a_s", spec.Name);
        Assert.Equal("quick", spec.Default.Category);
        Assert.Equal("summary/cpu-usage", spec.Default.Sensor);
        Assert.Equal(0f, spec.Default.Min);
        Assert.Equal(100f, spec.Default.Max);
    }

    [Fact]
    public void Several_annotations_keep_source_order_and_crlf_line_ends_parse()
    {
        var src = CpuLine.Replace("\n", "\r\n")
            + "uniform float u_b_s; // sensor {\"category\":\"cpu\",\"sensor\":\"cpu/temp\",\"min\":30.5,\"max\":95}\r\n";

        var specs = SensorUniformSpec.Parse(src);

        Assert.Equal(new[] { "u_a_s", "u_b_s" }, specs.Select(s => s.Name));
        Assert.Equal(30.5f, specs[1].Default.Min);
    }

    [Fact]
    public void Non_ASCII_escapes_decode_to_the_characters()
    {
        var specs = SensorUniformSpec.Parse(
            "uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"caf\\u00e9/\\u6e29\\u5ea6\",\"min\":0,\"max\":1}\n");

        Assert.Equal("café/温度", Assert.Single(specs).Default.Sensor);
    }

    [Theory]
    [InlineData("uniform float u_a_s; // sensor {not json}")]
    [InlineData("uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"\",\"min\":0,\"max\":100}")]
    [InlineData("uniform float u_a_s; // sensor {\"category\":\"\",\"sensor\":\"x\",\"min\":0,\"max\":100}")]
    [InlineData("uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"x\",\"min\":50,\"max\":50}")]
    [InlineData("uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"x\",\"min\":90,\"max\":10}")]
    [InlineData("uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"x\",\"min\":\"low\",\"max\":10}")]
    [InlineData("uniform float u_a_s; // sensor [1,2]")]
    [InlineData("uniform float u_a_s; // sensor")]
    [InlineData("float u_a_s = 1.0; // sensor {\"category\":\"quick\",\"sensor\":\"x\",\"min\":0,\"max\":1}")]
    public void A_malformed_or_unusable_annotation_is_skipped(string line)
    {
        Assert.Empty(SensorUniformSpec.Parse(line + "\n"));
    }

    [Fact]
    public void A_malformed_line_does_not_hide_a_good_one()
    {
        var specs = SensorUniformSpec.Parse("uniform float u_x_s; // sensor {oops}\n" + CpuLine);

        Assert.Equal("u_a_s", Assert.Single(specs).Name);
    }

    [Fact]
    public void A_sensor_uniform_is_not_a_slider_param()
    {
        var src = CpuLine + "uniform float u_gain; // hint_range(0.0, 2.0, 0.01) = 1.0\n";

        var slider = ShaderParamSpec.Parse(src);

        Assert.True(slider.ContainsKey("u_gain"));
        Assert.False(slider.ContainsKey("u_a_s"));
    }

    [Fact]
    public void A_source_without_the_marker_has_no_sensor_uniforms()
    {
        Assert.Empty(SensorUniformSpec.Parse(CustomFrags.Frag()));
    }

    [Fact]
    public void An_annotated_studio_fragment_passes_the_custom_effect_validator()
    {
        var frag = CustomFrags.Frag(extra: CpuLine);

        Assert.Null(CustomEffectStore.Validate("abc", "Name", "", frag));
        Assert.Null(CustomEffectStore.ValidateLoops(frag));
    }

    [Fact]
    public void A_fragment_with_a_non_ASCII_annotation_is_still_refused_by_the_ascii_rule()
    {
        var frag = CustomFrags.Frag(extra: "uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"é\",\"min\":0,\"max\":1}\n");

        Assert.Equal("frag must be ASCII only", CustomEffectStore.Validate("abc", "Name", "", frag));
    }

    [Fact]
    public void The_built_in_sensorbar_is_registered_self_contained_and_declares_its_sensor()
    {
        Assert.True(ShaderLibrary.IsSensorEffect("sensorbar"));
        Assert.Contains("sensorbar", ShaderLibrary.AllEffectKeys);
        Assert.True(ShaderLibrary.IsKnownKey("sensorbar"));

        var src = ShaderLibrary.Get("sensorbar");
        Assert.StartsWith("#version 330 core", src);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"uniform float u_speed;"));

        var spec = Assert.Single(ShaderLibrary.SensorUniforms("sensorbar"));
        Assert.Equal("u_fill_level_s", spec.Name);
        Assert.Equal("summary/cpu-usage", spec.Default.Sensor);
        Assert.False(ShaderLibrary.Params("sensorbar").ContainsKey(spec.Name));
    }

    [Fact]
    public void A_prelude_effect_is_not_a_sensor_effect_and_keeps_its_prelude()
    {
        Assert.False(ShaderLibrary.IsSensorEffect("plasma"));
        Assert.Empty(ShaderLibrary.SensorUniforms("plasma"));
        Assert.Contains("audioPresence", ShaderLibrary.Get("plasma"));
    }

    [Fact]
    public void A_commented_out_annotated_uniform_is_not_reported()
    {
        var src = "/*\n" + CpuLine + "*/\n"
            + "/* one-liner */ " + CpuLine.Replace("u_a_s", "u_c_s")
            + "// " + CpuLine.Replace("u_a_s", "u_d_s")
            + CpuLine.Replace("u_a_s", "u_live_s");

        var names = SensorUniformSpec.Parse(src).Select(s => s.Name).ToArray();

        Assert.Equal(new[] { "u_c_s", "u_live_s" }, names);
    }

    [Fact]
    public void A_block_comment_marker_inside_a_line_comment_does_not_open_a_block()
    {
        var src = "// note /*\n" + CpuLine;

        Assert.Single(SensorUniformSpec.Parse(src));
    }

    public static IEnumerable<object[]> EmbeddedSensorFrags() =>
        ShaderLibrary.AllEffectKeys.Where(ShaderLibrary.IsSensorEffect).Select(k => new object[] { k });

    [Fact]
    public void The_premade_set_is_embedded()
    {
        var keys = ShaderLibrary.AllEffectKeys.Where(ShaderLibrary.IsSensorEffect).ToHashSet();

        foreach (var k in new[] { "sensorbar", "sensorclock", "sensorsegments", "sensorliquid", "sensorcenter", "sensorshift", "sensorheat" })
        {
            Assert.Contains(k, keys);
        }
    }

    [Theory]
    [MemberData(nameof(EmbeddedSensorFrags))]
    public void Every_embedded_sensor_frag_passes_the_custom_effect_validator(string key)
    {
        var frag = ShaderLibrary.Get(key);

        Assert.Null(CustomEffectStore.Validate("abc", "Name", "", frag));
        Assert.Null(CustomEffectStore.ValidateLoops(frag));
    }

    [Theory]
    [MemberData(nameof(EmbeddedSensorFrags))]
    public void Every_embedded_sensor_frag_has_four_distinct_default_presets_over_its_own_sensor_uniforms(string key)
    {
        Assert.True(AnimateTemplateDefaults.All.TryGetValue(key, out var bundle));
        Assert.Equal(4, bundle.Slots.Count);
        var names = ShaderLibrary.SensorUniforms(key).Select(x => x.Name).ToHashSet();
        var specs = ShaderLibrary.Params(key);
        foreach (var slot in bundle.Slots)
        {
            var (uniform, binding) = Assert.Single(slot!.Sensors!);
            Assert.Contains(uniform, names);
            Assert.True(binding.IsUsable);
            Assert.Equal("quick", binding.Category);
            // A rebake that adds, drops or re-defaults a uniform must regenerate the JSON.
            var declared = specs.Where(kv => kv.Value.Declared && !ShaderEffect.BaseParamNames.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Default);
            Assert.Equal(declared.Keys.OrderBy(k => k, StringComparer.Ordinal), slot.Params.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (name, value) in slot.Params)
            {
                Assert.Equal(declared[name], value, 4);
            }
        }
        Assert.Equal(4, bundle.Slots.Select(x => x!.Sensors!.Values.Single().Sensor).Distinct().Count());
        var own = ShaderLibrary.SensorUniforms(key)[0].Default;
        Assert.Equal(own.Sensor, bundle.Slots[0]!.Sensors!.Values.Single().Sensor);
        Assert.Equal(own.Max, bundle.Slots[0]!.Sensors!.Values.Single().Max);
    }

    [Theory]
    [MemberData(nameof(EmbeddedSensorFrags))]
    public void Every_embedded_sensor_frag_declares_a_usable_default_binding_and_no_slider_for_it(string key)
    {
        var specs = ShaderLibrary.SensorUniforms(key);

        Assert.NotEmpty(specs);
        foreach (var spec in specs)
        {
            Assert.EndsWith("_s", spec.Name);
            Assert.True(spec.Default.IsUsable);
            Assert.Equal("quick", spec.Default.Category);
            Assert.StartsWith("summary/", spec.Default.Sensor);
            Assert.False(ShaderLibrary.Params(key).ContainsKey(spec.Name));
        }
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ShaderLibrary.Get(key), @"uniform float u_speed;"));
    }

    [GlTheory]
    [MemberData(nameof(EmbeddedSensorFrags))]
    public void Every_embedded_sensor_frag_compiles_and_renders(string key)
    {
        using var gpu = new GpuContext(160, 90);
        var fx = new ShaderEffect(key, gpu, ShaderLibrary.Get(key)) { FixedSensorValue = 0.6f };
        var canvas = new CanvasBuffer(160, 90);

        var lit = GlProbe.RenderUntilLit(fx, canvas, TimeSpan.FromSeconds(30));

        Assert.False(fx.HasFailed, $"{key} failed to compile");
        Assert.True(lit, $"{key} rendered black at a 0.6 fill");
    }
}

/// <summary>Renders until the async compile lands and something is lit, or the deadline passes.</summary>
internal static class GlProbe
{
    private static readonly Lazy<bool> Usable = new(() =>
    {
        using var gpu = new GpuContext(160, 90);
        var fx = new ShaderEffect("sensorbar", gpu, ShaderLibrary.Get("sensorbar")) { FixedSensorValue = 0.6f };
        return RenderUntilLit(fx, new CanvasBuffer(160, 90), TimeSpan.FromSeconds(30));
    });

    public static bool Works => Usable.Value;

    public static bool RenderUntilLit(ShaderEffect fx, CanvasBuffer canvas, TimeSpan deadline)
    {
        var end = DateTime.UtcNow + deadline;
        double t = 0;
        while (DateTime.UtcNow < end)
        {
            fx.RenderFrame(canvas, t += 50);
            if (fx.HasFailed)
            {
                return false;
            }
            if (!fx.IsRenderPending && AnyLit(canvas))
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return false;
    }

    private static bool AnyLit(CanvasBuffer canvas)
    {
        for (var y = 0; y < canvas.Height; y++)
        {
            for (var x = 0; x < canvas.Width; x += 4)
            {
                var (r, g, b) = canvas.GetPixel(x, y);
                if (r + g + b > 30)
                {
                    return true;
                }
            }
        }
        return false;
    }
}

/// <summary>A theory that reports itself skipped, with the reason, on a host where no GL context renders.</summary>
public sealed class GlTheoryAttribute : TheoryAttribute
{
    public GlTheoryAttribute()
    {
        if (!GlProbe.Works)
        {
            Skip = "no usable GL context on this host";
        }
    }
}
