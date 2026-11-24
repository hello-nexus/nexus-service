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
        var starts = FanLcdClipTiming.Starts(new[] { 17, 16, 17 });
        Assert.Equal((1, 17.0), FanLcdClipTiming.Next(starts, 0, 1000.0 / 60));
        Assert.Equal((2, 33.0), FanLcdClipTiming.Next(starts, 1, 32.5));
    }

    [Fact]
    public void A_screen_slower_than_the_clip_skips_frames_and_keeps_the_clips_speed()
    {
        // 60 fps clip, 30 fps screen: frame 1 (17-33 ms) is over when the screen is ready again.
        var starts = FanLcdClipTiming.Starts(new[] { 17, 16, 17, 17 });
        Assert.Equal((2, 1000.0 / 30), FanLcdClipTiming.Next(starts, 0, 1000.0 / 30));
    }

    [Fact]
    public void A_late_push_skips_to_the_frame_due_now()
    {
        var starts = FanLcdClipTiming.Starts(new[] { 100, 100, 100, 100 });
        Assert.Equal((2, 250.0), FanLcdClipTiming.Next(starts, 0, 250));
    }

    [Fact]
    public void The_clip_ends_at_its_length_or_when_its_time_has_run_out()
    {
        var starts = FanLcdClipTiming.Starts(new[] { 100, 100 });
        Assert.Equal((2, 200.0), FanLcdClipTiming.Next(starts, 1, 110));
        Assert.Equal((2, 230.0), FanLcdClipTiming.Next(starts, 0, 230));
    }

    [Theory]
    [InlineData(60, 1000.0 / 60, 60)]
    [InlineData(60, 1000.0 / 30, 30)]
    [InlineData(30, 1000.0 / 30, 30)]
    [InlineData(10, 1000.0 / 30, 10)]
    public void Playing_a_clip_end_to_end_pushes_at_the_lower_of_its_rate_and_the_cap(int clipFps, double minGapMs, int expectedFps)
    {
        var delays = new int[clipFps];
        for (var i = 0; i < delays.Length; i++)
        {
            delays[i] = (int)System.Math.Round((i + 1) * 1000.0 / clipFps) - (int)System.Math.Round(i * 1000.0 / clipFps);
        }
        var starts = FanLcdClipTiming.Starts(delays);

        // Instant pushes, so the cap alone limits the rate; a one second clip.
        var pushes = 1;
        var frame = 0;
        var now = 0.0;
        while (true)
        {
            var (next, due) = FanLcdClipTiming.Next(starts, frame, now + minGapMs);
            if (next >= delays.Length)
            {
                break;
            }
            pushes++;
            frame = next;
            now = due;
        }

        Assert.InRange(pushes, expectedFps - 1, expectedFps);
    }

    [Fact]
    public void An_unset_cap_is_the_default_zero_is_off_and_a_value_is_itself()
    {
        Assert.Equal(FanLcdClipTiming.DefaultFps, FanLcdClipTiming.CapOf(null));
        Assert.Equal(0, FanLcdClipTiming.CapOf(0));
        Assert.Equal(60, FanLcdClipTiming.CapOf(60));
    }

    [Fact]
    public void A_requested_cap_is_clamped_like_a_panels_and_off_stays_zero()
    {
        Assert.Equal(0, FanLcdClipTiming.ToStored(0));
        Assert.Equal(60, FanLcdClipTiming.ToStored(60));
        Assert.Equal(Nexus.Service.Panel.PanelDeviceRegistry.MaxFpsCap, FanLcdClipTiming.ToStored(5000));
    }

    [Fact]
    public void Off_leaves_no_gap_between_pushes_and_a_cap_leaves_its_period()
    {
        Assert.Equal(0, FanLcdClipTiming.MinGapMs(0));
        Assert.Equal(1000.0 / 60, FanLcdClipTiming.MinGapMs(60));
    }

    [Fact]
    public void Off_plays_a_60_fps_clip_at_its_own_rate()
    {
        var starts = FanLcdClipTiming.Starts(new[] { 17, 16, 17, 17 });
        Assert.Equal((1, 17.0), FanLcdClipTiming.Next(starts, 0, FanLcdClipTiming.MinGapMs(0)));
    }
}
