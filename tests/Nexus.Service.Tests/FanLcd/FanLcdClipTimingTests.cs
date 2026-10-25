using Nexus.Service.Peripherals.FanLcd;
using Xunit;

namespace Nexus.Service.Tests.FanLcd;

public class FanLcdClipTimingTests
{
    [Fact]
    public void Starts_add_up_the_delays_and_end_with_the_clip_length()
    {
        Assert.Equal(new double[] { 0, 30, 70, 100 }, FanLcdClipTiming.Starts(new[] { 30, 40, 30 }));
    }

    [Fact]
    public void A_frame_without_a_delay_still_shows_for_the_minimum()
    {
        Assert.Equal(new double[] { 0, FanLcdClipTiming.MinDelayMs }, FanLcdClipTiming.Starts(new[] { 0 }));
    }

    [Fact]
    public void A_screen_as_fast_as_the_clip_shows_every_frame_on_time()
    {
        // 60 fps clip, 60 fps screen: when the screen is ready before a frame's start, the frame waits for it.
        var starts = FanLcdClipTiming.Starts(new[] { 17, 16, 17 });
        Assert.Equal((1, 17.0), FanLcdClipTiming.Next(starts, 0, 1000.0 / 60));
        Assert.Equal((2, 33.0), FanLcdClipTiming.Next(starts, 1, 32.5));
    }

    [Fact]
    public void A_screen_slower_than_the_clip_skips_frames_and_keeps_the_clips_speed()
    {
        // 60 fps clip on a 30 fps screen: after frame 0 the screen can take the next at 33 ms,
        // when frame 1 (17-33) is already over, so frame 2 goes then.
        var starts = FanLcdClipTiming.Starts(new[] { 17, 16, 17, 17 });
        Assert.Equal((2, 33.333333333333336), FanLcdClipTiming.Next(starts, 0, 1000.0 / 30));
    }

    [Fact]
    public void A_late_push_skips_to_the_frame_due_now()
    {
        var starts = FanLcdClipTiming.Starts(new[] { 100, 100, 100, 100 });
        // Frame 0 took until 250 ms to go out: frame 2 (200-300) is the one to show.
        Assert.Equal((2, 250.0), FanLcdClipTiming.Next(starts, 0, 250));
    }

    [Fact]
    public void The_clip_ends_at_its_length_or_when_its_time_has_run_out()
    {
        var starts = FanLcdClipTiming.Starts(new[] { 100, 100 });
        Assert.Equal((2, 200.0), FanLcdClipTiming.Next(starts, 1, 110));
        Assert.Equal((2, 230.0), FanLcdClipTiming.Next(starts, 0, 230));
    }
}
