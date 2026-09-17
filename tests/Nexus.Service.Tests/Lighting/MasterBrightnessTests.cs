using Nexus.Service.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public sealed class MasterBrightnessTests
{
    private static List<BrightnessSchedulePoint> Points(params (int hour, int brightness)[] pts) =>
        pts.Select(p => new BrightnessSchedulePoint { Hour = p.hour, Brightness = p.brightness }).ToList();

    private static TimeSpan At(int hour, int minute = 0, int second = 0) => new(hour, minute, second);

    [Fact]
    public void Off_is_the_slider_alone()
    {
        var lighting = new LightingSettings { GlobalBrightness = 0.6f };
        lighting.BrightnessSchedule.Enabled = false;
        lighting.BrightnessSchedule.Points = Points((0, 10), (12, 10));

        Assert.Equal(0.6f, MasterBrightness.Effective(lighting, At(12)));
    }

    [Fact]
    public void On_caps_the_slider_and_never_raises_it()
    {
        var lighting = new LightingSettings
        {
            GlobalBrightness = 0.6f,
            BrightnessSchedule = new() { Enabled = true, Points = Points((0, 20), (12, 100)) },
        };

        Assert.Equal(0.6f, MasterBrightness.Effective(lighting, At(12)));
        Assert.Equal(0.2f, MasterBrightness.Effective(lighting, At(0)));
    }

    // Vectors shared with nexus-web's brightnessSchedule.test.ts: both sides
    // evaluate the same rule, so the graph's readout and the LEDs never
    // disagree.
    [Theory]
    [InlineData(3, 0, 10)]
    [InlineData(6, 0, 20)]
    [InlineData(8, 0, 53.3333)]
    [InlineData(9, 0, 76.6667)]
    [InlineData(12, 0, 100)]
    [InlineData(18, 0, 76.6667)]
    [InlineData(19, 0, 53.3333)]
    [InlineData(21, 0, 20)]
    [InlineData(21, 30, 15)]
    [InlineData(23, 0, 10)]
    [InlineData(23, 30, 10)]
    public void Follows_the_default_points_like_the_web_does(int hour, int minute, double percent)
    {
        Assert.Equal(percent / 100, MasterBrightness.Scheduled(MasterBrightness.DefaultSchedule(), At(hour, minute)), 4);
    }

    [Fact]
    public void Passes_through_every_point_and_moves_by_the_minute()
    {
        var pts = MasterBrightness.DefaultSchedule();
        foreach (var p in pts)
        {
            Assert.Equal(p.Brightness / 100f, MasterBrightness.Scheduled(pts, At(p.Hour)), 4);
        }
        // Seconds do not move it; the level steps once a minute.
        Assert.Equal(MasterBrightness.Scheduled(pts, At(6, 1)), MasterBrightness.Scheduled(pts, At(6, 1, 59)));
        Assert.NotEqual(MasterBrightness.Scheduled(pts, At(6, 1)), MasterBrightness.Scheduled(pts, At(6, 2)));
    }

    [Fact]
    public void Interpolates_by_the_minute()
    {
        var pts = Points((6, 40), (8, 70));

        // 6:01 is one minute of the 120-minute ramp: a quarter of a percent.
        Assert.Equal(0.4025f, MasterBrightness.Scheduled(pts, At(6, 1)), 4);
        Assert.Equal(0.55f, MasterBrightness.Scheduled(pts, At(7)), 4);
    }

    [Fact]
    public void Never_overshoots_and_rises_monotonically_between_rising_points()
    {
        var pts = MasterBrightness.DefaultSchedule();
        var prev = -1f;
        for (var m = 4 * 60; m <= 12 * 60; m += 5)
        {
            var level = MasterBrightness.Scheduled(pts, TimeSpan.FromMinutes(m));
            Assert.True(level >= prev - 1e-6f, $"fell at minute {m}");
            Assert.True(level <= 1f);
            prev = level;
        }
    }

    [Fact]
    public void Wraps_midnight_continuously()
    {
        var pts = Points((2, 20), (22, 60));

        // 22:00 -> 02:00 is one four-hour ramp from 60 down to 20, the same
        // from either side of the seam.
        Assert.Equal(MasterBrightness.Scheduled(pts, At(23, 59)), MasterBrightness.Scheduled(pts, TimeSpan.FromMinutes(-1)), 5);
        Assert.Equal(0.50f, MasterBrightness.Scheduled(pts, At(23)), 4);
        Assert.Equal(0.40f, MasterBrightness.Scheduled(pts, At(0)), 4);
        Assert.Equal(0.30f, MasterBrightness.Scheduled(pts, At(1)), 4);
    }

    [Fact]
    public void Point_order_does_not_matter()
    {
        var sorted = Points((0, 20), (12, 100), (18, 50));
        var shuffled = Points((18, 50), (0, 20), (12, 100));

        for (var h = 0; h < 24; h++)
        {
            Assert.Equal(MasterBrightness.Scheduled(sorted, At(h)), MasterBrightness.Scheduled(shuffled, At(h)));
        }
    }

    [Fact]
    public void Two_points_on_one_hour_read_as_the_earlier_like_the_cooling_engine()
    {
        var pts = Points((0, 20), (12, 50), (12, 70), (20, 80));

        Assert.Equal(0.5f, MasterBrightness.Scheduled(pts, At(12)), 5);
    }

    [Fact]
    public void Empty_or_single_point_schedules_are_flat()
    {
        Assert.Equal(1f, MasterBrightness.Scheduled(null, At(12)));
        Assert.Equal(1f, MasterBrightness.Scheduled(new(), At(12)));
        Assert.Equal(0.3f, MasterBrightness.Scheduled(Points((9, 30)), At(3)), 3);
        Assert.Equal(0.3f, MasterBrightness.Scheduled(Points((9, 30)), At(21)), 3);
    }

    [Fact]
    public void Default_curve_is_full_at_midday_and_dim_at_night()
    {
        var pts = MasterBrightness.DefaultSchedule();

        Assert.Equal(6, pts.Count);
        Assert.Equal(1f, MasterBrightness.Scheduled(pts, At(12)), 4);
        Assert.Equal(0.1f, MasterBrightness.Scheduled(pts, At(3)), 4);
        Assert.True(MasterBrightness.Scheduled(pts, At(23)) < MasterBrightness.Scheduled(pts, At(19)));
        // Dawn mirrors dusk about the middle of the day.
        for (var m = 0; m < 12 * 60; m += 15)
        {
            Assert.Equal(
                MasterBrightness.Scheduled(pts, TimeSpan.FromMinutes(13.5 * 60 - m)),
                MasterBrightness.Scheduled(pts, TimeSpan.FromMinutes(13.5 * 60 + m)), 5);
        }
    }

    [Fact]
    public void A_missing_schedule_is_the_slider_alone()
    {
        var lighting = new LightingSettings { GlobalBrightness = 0.4f, BrightnessSchedule = null! };

        Assert.Equal(0.4f, MasterBrightness.Effective(lighting, At(12)));
    }

    [Fact]
    public void A_non_finite_slider_passes_through_as_before()
    {
        var lighting = new LightingSettings { GlobalBrightness = float.NaN };
        lighting.BrightnessSchedule.Enabled = false;

        Assert.True(float.IsNaN(MasterBrightness.Effective(lighting, At(12))));
    }
}
