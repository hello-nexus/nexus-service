using Nexus.Service.Lighting.CustomEffects;
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
        Assert.Equal("u_sensorbar_level_s", spec.Name);
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
}
