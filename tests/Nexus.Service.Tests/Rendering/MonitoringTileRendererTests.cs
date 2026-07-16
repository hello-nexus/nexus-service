using System.Collections.Generic;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Rendering;
using Xunit;

namespace Nexus.Service.Tests.Rendering;

public class MonitoringTileRendererTests
{
    private static readonly IReadOnlyList<float> History = new List<float> { 10f, 20f, 55f, 40f, 70f };

    [Theory]
    [InlineData(MonitoringTileStyle.Line, 72)]
    [InlineData(MonitoringTileStyle.Line, 80)]
    [InlineData(MonitoringTileStyle.Line, 96)]
    [InlineData(MonitoringTileStyle.Segments, 72)]
    [InlineData(MonitoringTileStyle.Segments, 80)]
    [InlineData(MonitoringTileStyle.Segments, 96)]
    [InlineData(MonitoringTileStyle.Backdrop, 72)]
    [InlineData(MonitoringTileStyle.Backdrop, 80)]
    [InlineData(MonitoringTileStyle.Backdrop, 96)]
    [InlineData(MonitoringTileStyle.Number, 72)]
    [InlineData(MonitoringTileStyle.Number, 80)]
    [InlineData(MonitoringTileStyle.Number, 96)]
    public void Render_produces_a_square_image_at_the_requested_size(MonitoringTileStyle style, int pixelSize)
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU Usage",
            ShowName = true,
            ValueText = "70%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, pixelSize);

        Assert.Equal(pixelSize, image.Width);
        Assert.Equal(pixelSize, image.Height);
    }

    [Theory]
    [InlineData(MonitoringTileStyle.Line, 72)]
    [InlineData(MonitoringTileStyle.Line, 80)]
    [InlineData(MonitoringTileStyle.Line, 96)]
    [InlineData(MonitoringTileStyle.Segments, 72)]
    [InlineData(MonitoringTileStyle.Segments, 80)]
    [InlineData(MonitoringTileStyle.Segments, 96)]
    [InlineData(MonitoringTileStyle.Backdrop, 72)]
    [InlineData(MonitoringTileStyle.Backdrop, 80)]
    [InlineData(MonitoringTileStyle.Backdrop, 96)]
    [InlineData(MonitoringTileStyle.Number, 72)]
    [InlineData(MonitoringTileStyle.Number, 80)]
    [InlineData(MonitoringTileStyle.Number, 96)]
    public void Render_produces_a_non_empty_image_buffer_at_all_key_tile_sizes(MonitoringTileStyle style, int pixelSize)
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU Usage",
            ValueText = "70%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, pixelSize);
        var rgba = RenderKit.ToRgba32Bytes(image);

        Assert.NotEmpty(rgba);
        Assert.Equal(pixelSize * pixelSize * 4, rgba.Length);
    }

    [Theory]
    [InlineData(MonitoringTileStyle.Line)]
    [InlineData(MonitoringTileStyle.Segments)]
    [InlineData(MonitoringTileStyle.Backdrop)]
    [InlineData(MonitoringTileStyle.Number)]
    public void Render_encodes_as_a_valid_gen1_bmp_at_the_mini_key_size(MonitoringTileStyle style)
    {
        var model = StreamDeckModels.ByProductId(0x0063); // Mini: 80px BMP
        Assert.NotNull(model);

        var input = new MonitoringTileInput
        {
            Name = "GPU Temp",
            ValueText = "65°C",
            SensorType = "Temperature",
            History = History,
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, model!.KeyPixelSize);
        var rgb = RenderKit.ToRgb24(image);
        var bmp = BmpEncoder.Encode(rgb, image.Width, image.Height);

        Assert.NotEmpty(bmp);
        Assert.True(model.IsValidWireImageLength(bmp.Length));
    }

    [Theory]
    [InlineData(MonitoringTileStyle.Line, 0x0080)] // MK.2: 72px JPEG
    [InlineData(MonitoringTileStyle.Segments, 0x006c)] // XL: 96px JPEG
    [InlineData(MonitoringTileStyle.Backdrop, 0x006c)]
    [InlineData(MonitoringTileStyle.Number, 0x0080)]
    public void Render_encodes_as_a_valid_gen2_jpeg(MonitoringTileStyle style, int productId)
    {
        var model = StreamDeckModels.ByProductId(productId);
        Assert.NotNull(model);

        var input = new MonitoringTileInput
        {
            Name = "Memory",
            ValueText = "48%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, model!.KeyPixelSize);
        var jpeg = RenderKit.EncodeJpeg(image);

        Assert.NotEmpty(jpeg);
        Assert.True(model.IsValidWireImageLength(jpeg.Length));
    }

    [Fact]
    public void Render_tolerates_empty_history()
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU",
            ValueText = "0%",
            SensorType = "Load",
            History = System.Array.Empty<float>(),
            Style = MonitoringTileStyle.Line,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Theory]
    [InlineData(MonitoringTileStyle.Segments)]
    [InlineData(MonitoringTileStyle.Backdrop)]
    public void Render_tolerates_empty_history_for_the_new_styles(MonitoringTileStyle style)
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU",
            ValueText = "0%",
            SensorType = "Load",
            History = System.Array.Empty<float>(),
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Fact]
    public void Render_tolerates_a_single_history_sample()
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU",
            ValueText = "42%",
            SensorType = "Load",
            History = new List<float> { 42f },
            Style = MonitoringTileStyle.Segments,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Fact]
    public void Render_tolerates_a_degenerate_non_percent_domain_where_history_has_no_variation()
    {
        var input = new MonitoringTileInput
        {
            Name = "Clock",
            ValueText = "4200MHz",
            SensorType = "Clock",
            History = new List<float> { 4200f, 4200f, 4200f },
            Style = MonitoringTileStyle.Line,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Fact]
    public void Render_line_style_tolerates_a_single_history_sample()
    {
        var input = new MonitoringTileInput
        {
            Name = "CPU",
            ValueText = "42%",
            SensorType = "Load",
            History = new List<float> { 42f },
            Style = MonitoringTileStyle.Line,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Theory]
    [InlineData(MonitoringTileStyle.Segments)]
    [InlineData(MonitoringTileStyle.Backdrop)]
    public void Render_tolerates_a_degenerate_domain_for_the_new_styles(MonitoringTileStyle style)
    {
        var input = new MonitoringTileInput
        {
            Name = "Clock",
            ValueText = "4200MHz",
            SensorType = "Clock",
            History = new List<float> { 4200f, 4200f, 4200f },
            Style = style,
        };

        using var image = MonitoringTileRenderer.Render(input, 80);

        Assert.Equal(80, image.Width);
    }

    [Fact]
    public void Render_omits_the_name_when_ShowName_is_false()
    {
        var withName = new MonitoringTileInput { Name = "CPU", ShowName = true, ValueText = "1%", SensorType = "Load", History = History };
        var withoutName = new MonitoringTileInput { Name = "CPU", ShowName = false, ValueText = "1%", SensorType = "Load", History = History };

        using var imageWithName = MonitoringTileRenderer.Render(withName, 80);
        using var imageWithoutName = MonitoringTileRenderer.Render(withoutName, 80);

        Assert.Equal(80, imageWithName.Width);
        Assert.Equal(80, imageWithoutName.Width);
        Assert.NotEqual(RenderKit.ToRgb24(imageWithName), RenderKit.ToRgb24(imageWithoutName));
    }

    [Fact]
    public void Render_splits_the_number_style_value_into_a_numeric_and_unit_part_without_throwing()
    {
        var input = new MonitoringTileInput
        {
            Name = "GPU Clock",
            ValueText = "4713MHz",
            SensorType = "Clock",
            History = new List<float> { 4600f, 4700f, 4713f },
            Style = MonitoringTileStyle.Number,
        };

        using var image = MonitoringTileRenderer.Render(input, 96);

        Assert.Equal(96, image.Width);
    }

    [Fact]
    public void Render_segments_produces_different_pixels_from_line_at_the_same_input()
    {
        static MonitoringTileInput Input(MonitoringTileStyle style) => new()
        {
            Name = "CPU",
            ValueText = "70%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var line = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Line), 80);
        using var segments = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Segments), 80);

        Assert.NotEqual(RenderKit.ToRgb24(line), RenderKit.ToRgb24(segments));
    }

    [Fact]
    public void Render_backdrop_produces_different_pixels_from_line_at_the_same_input()
    {
        static MonitoringTileInput Input(MonitoringTileStyle style) => new()
        {
            Name = "CPU",
            ValueText = "70%",
            SensorType = "Load",
            History = History,
            Style = style,
        };

        using var line = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Line), 80);
        using var backdrop = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Backdrop), 80);

        Assert.NotEqual(RenderKit.ToRgb24(line), RenderKit.ToRgb24(backdrop));
    }

    [Fact]
    public void Render_backdrop_fills_edge_to_edge_reaching_corners_line_does_not()
    {
        // A flat series maxed at the domain ceiling fills Backdrop's whole
        // key face corner to corner. The sampled corner sits outside Line's
        // inset middle band and outside both styles' centered text.
        var maxedHistory = new List<float> { 100f, 100f, 100f, 100f, 100f };
        static MonitoringTileInput Input(MonitoringTileStyle style, IReadOnlyList<float> history) => new()
        {
            Name = "CPU",
            ValueText = "100%",
            SensorType = "Load",
            History = history,
            Style = style,
        };

        const int size = 80;
        const int x = 2;
        const int y = 78;
        var offset = (y * size + x) * 4;

        using var line = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Line, maxedHistory), size);
        using var backdrop = MonitoringTileRenderer.Render(Input(MonitoringTileStyle.Backdrop, maxedHistory), size);

        var lineRgba = RenderKit.ToRgba32Bytes(line);
        var backdropRgba = RenderKit.ToRgba32Bytes(backdrop);

        Assert.Equal(0x0e, lineRgba[offset]);
        Assert.Equal(0x11, lineRgba[offset + 1]);
        Assert.Equal(0x16, lineRgba[offset + 2]);

        Assert.False(backdropRgba[offset] == 0x0e && backdropRgba[offset + 1] == 0x11 && backdropRgba[offset + 2] == 0x16);
    }

    [Fact]
    public void Render_backdrop_draws_the_value_as_a_centered_overlay()
    {
        var withValue = new MonitoringTileInput { Name = "CPU", ValueText = "70%", SensorType = "Load", History = History, Style = MonitoringTileStyle.Backdrop };
        var withoutValue = new MonitoringTileInput { Name = "CPU", ValueText = "", SensorType = "Load", History = History, Style = MonitoringTileStyle.Backdrop };

        using var imageWithValue = MonitoringTileRenderer.Render(withValue, 80);
        using var imageWithoutValue = MonitoringTileRenderer.Render(withoutValue, 80);

        Assert.NotEqual(RenderKit.ToRgb24(imageWithValue), RenderKit.ToRgb24(imageWithoutValue));
    }

    [Theory]
    [InlineData("Load")]
    [InlineData("Temperature")]
    [InlineData("Control")]
    [InlineData("Level")]
    public void ResolveDomain_FixesPercentLikeSensorTypesTo0_100(string sensorType)
    {
        var domain = MonitoringTileRenderer.ResolveDomain(sensorType, new List<float> { 4200f, 4200f });

        Assert.Equal(0f, domain.Min);
        Assert.Equal(100f, domain.Max);
    }

    [Fact]
    public void ResolveDomain_AutoScalesEverythingElseToHistoryMinMax()
    {
        var domain = MonitoringTileRenderer.ResolveDomain("Clock", new List<float> { 4200f, 4700f, 4500f });

        Assert.Equal(4200f, domain.Min);
        Assert.Equal(4700f, domain.Max);
    }

    [Fact]
    public void FillFraction_DividesByDomainMaxRatherThanMinMaxNormalizing()
    {
        var domain = (Min: 100f, Max: 200f);

        Assert.Equal(0.75f, MonitoringTileRenderer.FillFraction(150f, domain));
    }

    [Fact]
    public void FillFraction_ClampsAboveDomainMax()
    {
        Assert.Equal(1f, MonitoringTileRenderer.FillFraction(500f, (Min: 0f, Max: 100f)));
    }

    [Fact]
    public void FillFraction_DegenerateDomainRendersNeutralFill()
    {
        Assert.Equal(0.5f, MonitoringTileRenderer.FillFraction(50f, (Min: 10f, Max: 10f)));
    }

    /// <summary>A non-degenerate domain (Max greater than Min) whose Max is exactly 0 (e.g. Min negative) must not divide by it - value/0 reaches Math.Clamp as NaN, which Clamp passes through unclamped.</summary>
    [Fact]
    public void FillFraction_NonDegenerateDomainWithZeroMax_RendersZeroNotNaN()
    {
        Assert.Equal(0f, MonitoringTileRenderer.FillFraction(-2f, (Min: -5f, Max: 0f)));
    }

    [Fact]
    public void FillFraction_NonFiniteMaxRendersZero()
    {
        Assert.Equal(0f, MonitoringTileRenderer.FillFraction(50f, (Min: 0f, Max: float.PositiveInfinity)));
    }

    /// <summary>Every comparison against NaN is false, so a `Max &lt;= Min` guard would miss a NaN bound and fall through to dividing by it; `!(Max &gt; Min)` catches it as degenerate instead.</summary>
    [Theory]
    [InlineData(float.NaN, 100f)]
    [InlineData(0f, float.NaN)]
    public void FillFraction_NaNDomainBoundRendersNeutralFill(float min, float max)
    {
        Assert.Equal(0.5f, MonitoringTileRenderer.FillFraction(50f, (Min: min, Max: max)));
    }

    [Theory]
    [InlineData("segments")]
    [InlineData("radial")]
    public void ParseStyle_SegmentsAndLegacyRadialBothMapToSegments(string style)
    {
        Assert.Equal(MonitoringTileStyle.Segments, MonitoringTileRenderer.ParseStyle(style));
    }

    [Fact]
    public void ParseStyle_MapsBackdrop()
    {
        Assert.Equal(MonitoringTileStyle.Backdrop, MonitoringTileRenderer.ParseStyle("backdrop"));
    }

    [Fact]
    public void ParseStyle_MapsNumber()
    {
        Assert.Equal(MonitoringTileStyle.Number, MonitoringTileRenderer.ParseStyle("number"));
    }

    [Theory]
    [InlineData("line")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("unknown-future-style")]
    public void ParseStyle_UnknownOrAbsentValuesFallBackToLine(string? style)
    {
        Assert.Equal(MonitoringTileStyle.Line, MonitoringTileRenderer.ParseStyle(style));
    }

    [Theory]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(96)]
    public void Render_uses_LabelText_over_Name_for_the_top_label(int pixelSize)
    {
        var withSensorName = new MonitoringTileInput { Name = "CPU Usage", ValueText = "70%", SensorType = "Load", History = History, Style = MonitoringTileStyle.Line };
        var withLabelText = new MonitoringTileInput { Name = "CPU Usage", LabelText = "My Label", ValueText = "70%", SensorType = "Load", History = History, Style = MonitoringTileStyle.Line };

        using var sensorNameImage = MonitoringTileRenderer.Render(withSensorName, pixelSize);
        using var labelTextImage = MonitoringTileRenderer.Render(withLabelText, pixelSize);

        Assert.NotEqual(RenderKit.ToRgb24(sensorNameImage), RenderKit.ToRgb24(labelTextImage));
    }

    [Theory]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(96)]
    public void Render_falls_back_to_Name_when_LabelText_is_null_or_empty(int pixelSize)
    {
        var withNullLabel = new MonitoringTileInput { Name = "CPU Usage", LabelText = null, ValueText = "70%", SensorType = "Load", History = History, Style = MonitoringTileStyle.Line };
        var withEmptyLabel = new MonitoringTileInput { Name = "CPU Usage", LabelText = "", ValueText = "70%", SensorType = "Load", History = History, Style = MonitoringTileStyle.Line };

        using var nullLabelImage = MonitoringTileRenderer.Render(withNullLabel, pixelSize);
        using var emptyLabelImage = MonitoringTileRenderer.Render(withEmptyLabel, pixelSize);

        Assert.Equal(RenderKit.ToRgb24(nullLabelImage), RenderKit.ToRgb24(emptyLabelImage));
    }

    [Fact]
    public void Render_hides_the_name_row_when_ShowName_is_false_even_with_LabelText_set()
    {
        var showNameFalseNoLabel = new MonitoringTileInput { Name = "CPU", ShowName = false, ValueText = "1%", SensorType = "Load", History = History };
        var showNameFalseWithLabel = new MonitoringTileInput { Name = "CPU", LabelText = "Custom", ShowName = false, ValueText = "1%", SensorType = "Load", History = History };

        using var noLabelImage = MonitoringTileRenderer.Render(showNameFalseNoLabel, 80);
        using var withLabelImage = MonitoringTileRenderer.Render(showNameFalseWithLabel, 80);

        Assert.Equal(RenderKit.ToRgb24(noLabelImage), RenderKit.ToRgb24(withLabelImage));
    }

    [Fact]
    public void ResolveDomain_Overload_FixedScaleWithValidRangeWins()
    {
        var input = new MonitoringTileInput { SensorType = "Load", History = new List<float> { 10f, 20f }, Scale = "fixed", Min = 20f, Max = 90f };

        var domain = MonitoringTileRenderer.ResolveDomain(input);

        Assert.Equal(20f, domain.Min);
        Assert.Equal(90f, domain.Max);
    }

    [Fact]
    public void ResolveDomain_Overload_AdaptiveScaleIgnoresMinMax()
    {
        var input = new MonitoringTileInput { SensorType = "Load", History = new List<float> { 10f, 20f }, Scale = "adaptive", Min = 20f, Max = 90f };

        var domain = MonitoringTileRenderer.ResolveDomain(input);

        Assert.Equal(0f, domain.Min);
        Assert.Equal(100f, domain.Max);
    }

    [Fact]
    public void ResolveDomain_Overload_AbsentScaleDefaultsToAdaptive()
    {
        var input = new MonitoringTileInput { SensorType = "Clock", History = new List<float> { 4200f, 4700f }, Min = 20f, Max = 90f };

        var domain = MonitoringTileRenderer.ResolveDomain(input);

        Assert.Equal(4200f, domain.Min);
        Assert.Equal(4700f, domain.Max);
    }

    [Theory]
    [InlineData(null, 90f)]
    [InlineData(20f, null)]
    public void ResolveDomain_Overload_MissingBoundFallsBackToAdaptive(float? min, float? max)
    {
        var input = new MonitoringTileInput { SensorType = "Clock", History = new List<float> { 4200f, 4700f }, Scale = "fixed", Min = min, Max = max };

        var domain = MonitoringTileRenderer.ResolveDomain(input);

        Assert.Equal(4200f, domain.Min);
        Assert.Equal(4700f, domain.Max);
    }

    [Theory]
    [InlineData(90f, 90f)]
    [InlineData(90f, 20f)]
    public void ResolveDomain_Overload_MaxNotGreaterThanMinFallsBackToAdaptive(float min, float max)
    {
        var input = new MonitoringTileInput { SensorType = "Clock", History = new List<float> { 4200f, 4700f }, Scale = "fixed", Min = min, Max = max };

        var domain = MonitoringTileRenderer.ResolveDomain(input);

        Assert.Equal(4200f, domain.Min);
        Assert.Equal(4700f, domain.Max);
    }

    [Theory]
    [InlineData(float.NaN, 90f)]
    [InlineData(20f, float.NaN)]
    [InlineData(float.PositiveInfinity, 90f)]
    [InlineData(20f, float.PositiveInfinity)]
    public void ResolveDomain_Overload_NonFiniteBoundFallsBackToAdaptive(float min, float max)
    {
        var input = new MonitoringTileInput { SensorType = "Clock", History = new List<float> { 4200f, 4700f }, Scale = "fixed", Min = min, Max = max };

        var domain = MonitoringTileRenderer.ResolveDomain(input);

        Assert.Equal(4200f, domain.Min);
        Assert.Equal(4700f, domain.Max);
    }

    [Theory]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(96)]
    public void Render_fixed_scale_changes_segments_fill_relative_to_adaptive(int pixelSize)
    {
        static MonitoringTileInput Input(string? scale, float? min, float? max) => new()
        {
            Name = "Clock",
            ValueText = "4500MHz",
            SensorType = "Clock",
            History = new List<float> { 4200f, 4500f, 4700f },
            Style = MonitoringTileStyle.Segments,
            Scale = scale,
            Min = min,
            Max = max,
        };

        using var adaptive = MonitoringTileRenderer.Render(Input(null, null, null), pixelSize);
        using var fixedScale = MonitoringTileRenderer.Render(Input("fixed", 0f, 10000f), pixelSize);

        Assert.NotEqual(RenderKit.ToRgb24(adaptive), RenderKit.ToRgb24(fixedScale));
    }

    [Theory]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(96)]
    public void Render_fixed_scale_changes_line_axis_relative_to_adaptive(int pixelSize)
    {
        static MonitoringTileInput Input(string? scale, float? min, float? max) => new()
        {
            Name = "Clock",
            ValueText = "4500MHz",
            SensorType = "Clock",
            History = new List<float> { 4200f, 4500f, 4700f },
            Style = MonitoringTileStyle.Line,
            Scale = scale,
            Min = min,
            Max = max,
        };

        using var adaptive = MonitoringTileRenderer.Render(Input(null, null, null), pixelSize);
        using var fixedScale = MonitoringTileRenderer.Render(Input("fixed", 0f, 10000f), pixelSize);

        Assert.NotEqual(RenderKit.ToRgb24(adaptive), RenderKit.ToRgb24(fixedScale));
    }

    [Theory]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(96)]
    public void Render_fixed_scale_with_invalid_range_falls_back_to_adaptive_pixels(int pixelSize)
    {
        static MonitoringTileInput Input(string? scale, float? min, float? max) => new()
        {
            Name = "Clock",
            ValueText = "4500MHz",
            SensorType = "Clock",
            History = new List<float> { 4200f, 4500f, 4700f },
            Style = MonitoringTileStyle.Backdrop,
            Scale = scale,
            Min = min,
            Max = max,
        };

        using var adaptive = MonitoringTileRenderer.Render(Input(null, null, null), pixelSize);
        using var missingBounds = MonitoringTileRenderer.Render(Input("fixed", null, null), pixelSize);
        using var invertedRange = MonitoringTileRenderer.Render(Input("fixed", 100f, 50f), pixelSize);

        Assert.Equal(RenderKit.ToRgb24(adaptive), RenderKit.ToRgb24(missingBounds));
        Assert.Equal(RenderKit.ToRgb24(adaptive), RenderKit.ToRgb24(invertedRange));
    }
}
