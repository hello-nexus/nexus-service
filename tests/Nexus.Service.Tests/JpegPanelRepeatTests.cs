using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class JpegPanelRepeatTests
{
    [Theory]
    [InlineData(0, 10_000, 0, true, false)]
    [InlineData(1_000, 10_000, 0, false, false)]
    [InlineData(1_000, 999, 0, true, false)]
    [InlineData(1_000, 1_000, 0, true, true)]
    [InlineData(1_000, 5_500, 4_800, true, false)]
    public void Repeat_is_due_only_for_an_opted_in_model_with_a_frame_after_the_idle_window(
        int repeatMs, long now, long lastSent, bool hasFrame, bool expected)
    {
        Assert.Equal(expected, JpegPanelStreamTransport.RepeatDue(repeatMs, now, lastSent, hasFrame));
    }

    [Fact]
    public void Only_the_capellix_repeats_its_last_frame()
    {
        Assert.Equal(1000, JpegPanelModel.CorsairCapellix.RepeatLastFrameMs);
        Assert.All(JpegPanelModel.All.Where(m => m != JpegPanelModel.CorsairCapellix), m => Assert.Equal(0, m.RepeatLastFrameMs));
    }
}
