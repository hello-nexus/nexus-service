using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdAnimationRendererTests
{
    [Theory]
    [InlineData("pulse", 0.0)]
    [InlineData("pulse", 1.7)]
    [InlineData("spectrum", 0.0)]
    [InlineData("spectrum", 12.3)]
    [InlineData("spin", 0.0)]
    [InlineData("spin", 5.4)]
    [InlineData(null, 0.0)]
    [InlineData("not-an-animation", 3.0)]
    public void Render_produces_a_400x400_jpeg_for_every_animation(string? animationId, double elapsedSeconds)
    {
        var jpeg = Slv3LcdAnimationRenderer.Render(animationId, elapsedSeconds, "#00D1FF", "#9B5DE5");

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.NotNull(info);
        Assert.Equal(400, info!.Width);
        Assert.Equal(400, info.Height);
    }

    [Fact]
    public void Render_tolerates_invalid_hex_colors()
    {
        var jpeg = Slv3LcdAnimationRenderer.Render("spin", 2.0, "nope", null);

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.Equal(400, info!.Width);
    }

    [Fact]
    public void FrameIntervalMs_is_a_reasonable_animation_cadence()
    {
        Assert.InRange(Slv3LcdAnimationRenderer.FrameIntervalMs, 16, 200);
    }
}
