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
    // evaluate the same Fritsch-Carlson spline, so the graph's readout and the
    // LEDs never disagree.
    [Theory]
    [InlineData(1, 0, 17.1094)]
    [InlineData(2, 0, 15.6250)]
    [InlineData(3, 0, 15.0781)]
    [InlineData(6, 0, 32.1875)]
    [InlineData(10, 0, 85.3125)]
    [InlineData(14, 0, 98.1250)]
    [InlineData(18, 0, 71.2500)]
    [InlineData(22, 0, 32.5000)]
    [InlineData(23, 0, 25.1563)]
    [InlineData(23, 30, 22.2461)]
    public void Curves_through_the_default_points_like_the_web_does(int hour, int minute, double percent)
    {
        Assert.Equal(percent / 100, MasterBrightness.Scheduled(MasterBrightness.DefaultSchedule(), At(hour, minute)), 3);
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

        // The wrap segment is one curve: the same instant reads the same from
        // either side of the seam, and midnight sits inside it.
        Assert.Equal(MasterBrightness.Scheduled(pts, At(23, 59)), MasterBrightness.Scheduled(pts, TimeSpan.FromMinutes(-1)), 5);
        var atMidnight = MasterBrightness.Scheduled(pts, At(0));
        Assert.InRange(atMidnight, 0.2f, 0.6f);
        Assert.True(MasterBrightness.Scheduled(pts, At(23)) > atMidnight);
        Assert.True(atMidnight > MasterBrightness.Scheduled(pts, At(1)));
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
        Assert.True(MasterBrightness.Scheduled(pts, At(13)) > 0.95f);
        Assert.True(MasterBrightness.Scheduled(pts, At(3)) < 0.25f);
        Assert.True(MasterBrightness.Scheduled(pts, At(23)) < MasterBrightness.Scheduled(pts, At(19)));
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
