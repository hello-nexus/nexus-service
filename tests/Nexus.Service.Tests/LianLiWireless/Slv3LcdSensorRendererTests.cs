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
}
