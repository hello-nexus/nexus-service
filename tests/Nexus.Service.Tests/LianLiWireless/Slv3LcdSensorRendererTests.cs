using System.IO;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdSensorRendererTests
{
    [Theory]
    [InlineData("ring", 0f)]
    [InlineData("ring", 42f)]
    [InlineData("ring", 100f)]
    [InlineData("bar", 0f)]
    [InlineData("bar", 55f)]
    [InlineData("bar", 100f)]
    public void Render_produces_a_400x400_jpeg(string style, float value)
    {
        var jpeg = Slv3LcdSensorRenderer.Render(style, value, 0f, 100f, "CPU", "%", "#00D1FF", "#FFFFFF");

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.NotNull(info);
        Assert.Equal(400, info!.Width);
        Assert.Equal(400, info.Height);
    }

    [Fact]
    public void Render_is_case_insensitive_on_style()
    {
        var jpeg = Slv3LcdSensorRenderer.Render("BAR", 50f, 0f, 100f, "CPU", "%", null, null);

        var info = TestImages.Identify(jpeg);
        Assert.Equal(400, info!.Width);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-style")]
    public void Render_defaults_unknown_or_missing_style_to_ring(string? style)
    {
        var jpeg = Slv3LcdSensorRenderer.Render(style, 50f, 0f, 100f, "CPU", "%", null, null);

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.Equal(400, info!.Width);
        Assert.Equal(400, info.Height);
    }

    [Fact]
    public void Render_tolerates_invalid_hex_colors()
    {
        var jpeg = Slv3LcdSensorRenderer.Render("ring", 50f, 0f, 100f, "GPU", "RPM", "not-a-color", "");

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.Equal(400, info!.Width);
    }

    [Fact]
    public void Render_clamps_a_value_outside_min_max()
    {
        var jpeg = Slv3LcdSensorRenderer.Render("ring", 999f, 0f, 100f, "CPU", "%", null, null);

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.Equal(400, info!.Width);
        Assert.Equal(400, info.Height);
    }

    [Fact]
    public void Render_handles_a_degenerate_min_max_range()
    {
        var jpeg = Slv3LcdSensorRenderer.Render("ring", 50f, 100f, 100f, "CPU", "%", null, null);

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.Equal(400, info!.Width);
    }

    [Theory]
    [InlineData(65.34f, "Temperature", false, "65.3")]
    [InlineData(65.5f, "Temperature", true, "66")]
    [InlineData(-0.5f, "Temperature", true, "-1")]
    [InlineData(149.54f, "Temperature", false, "149.5")]
    [InlineData(42.26f, "Load", false, "42.3")]
    [InlineData(42.4f, "Load", true, "42")]
    [InlineData(1200.4f, "Fan", false, "1200")]
    // No value type (older callers): the original integer rendering.
    [InlineData(42.6f, "", false, "43")]
    public void FormatValueText_FollowsTheSharedRule(float value, string type, bool whole, string expected)
    {
        Assert.Equal(expected, Slv3LcdSensorRenderer.FormatValueText(value, type, whole));
    }

    [Fact]
    public void Render_accepts_a_value_type_and_whole_numbers()
    {
        var jpeg = Slv3LcdSensorRenderer.Render("ring", 65.4f, 0f, 100f, "CPU", "°C", null, null, "Temperature", wholeNumbers: false);

        Assert.NotEmpty(jpeg);
    }

    /// <summary>The widest decimal readings must stay inside the gauge: the ring's inner circle and the bar canvas.</summary>
    [Theory]
    [InlineData("100.0", 72f, 270f)]
    [InlineData("212.0", 72f, 270f)]
    [InlineData("100.0", 96f, 340f)]
    [InlineData("212.0", 96f, 340f)]
    public void Decimal_value_text_fits_the_layout(string text, float size, float limit)
    {
        using var font = Nexus.Service.Rendering.RenderKit.CreateFont(Nexus.Service.Rendering.RenderKit.ResolveFont(), size, bold: true);

        Assert.True(font.MeasureText(text) <= limit, $"{text} at {size} measures {font.MeasureText(text)}");
    }
}
