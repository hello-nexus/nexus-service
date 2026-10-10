using System;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdClockRendererTests
{
    private static readonly DateTime FixedNow = new(2026, 7, 2, 14, 35, 20);

    [Theory]
    [InlineData("digital")]
    [InlineData("digitalMinimal")]
    [InlineData("analogClassic")]
    [InlineData("analogMinimal")]
    [InlineData(null)]
    [InlineData("not-a-face")]
    public void Render_produces_a_400x400_jpeg_for_every_face(string? face)
    {
        var jpeg = Slv3LcdClockRenderer.Render(face, FixedNow, "#00D1FF", "#FFFFFF");

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.NotNull(info);
        Assert.Equal(400, info!.Width);
        Assert.Equal(400, info.Height);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(23, 59, 59)]
    [InlineData(12, 0, 0)]
    public void Render_handles_the_full_range_of_times_on_the_analog_face(int hour, int minute, int second)
    {
        var jpeg = Slv3LcdClockRenderer.Render("analogClassic", new DateTime(2026, 1, 1, hour, minute, second), null, null);

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.Equal(400, info!.Width);
        Assert.Equal(400, info.Height);
    }

    [Fact]
    public void Render_tolerates_invalid_hex_colors()
    {
        var jpeg = Slv3LcdClockRenderer.Render("digital", FixedNow, "nope", "#zzzzzz");

        Assert.NotEmpty(jpeg);
        var info = TestImages.Identify(jpeg);
        Assert.Equal(400, info!.Width);
    }
}
