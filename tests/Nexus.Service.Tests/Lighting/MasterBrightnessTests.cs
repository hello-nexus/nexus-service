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

    [Fact]
    public void Interpolates_between_points_by_the_minute()
    {
        var pts = Points((6, 40), (8, 70));

        Assert.Equal(0.40f, MasterBrightness.Scheduled(pts, At(6)));
        Assert.Equal(0.55f, MasterBrightness.Scheduled(pts, At(7)), 3);
        // 6:01 is one minute of the 120-minute ramp: a quarter of a percent.
        Assert.Equal(0.4025f, MasterBrightness.Scheduled(pts, At(6, 1)), 4);
        // Seconds do not move it; the level steps once a minute.
        Assert.Equal(MasterBrightness.Scheduled(pts, At(6, 1)), MasterBrightness.Scheduled(pts, At(6, 1, 59)));
    }

    [Fact]
    public void Wraps_midnight_in_both_directions()
    {
        var pts = Points((2, 20), (22, 60));

        // 22:00 -> 02:00 is a four-hour ramp from 60 down to 20.
        Assert.Equal(0.50f, MasterBrightness.Scheduled(pts, At(23)), 3);
        Assert.Equal(0.40f, MasterBrightness.Scheduled(pts, At(0)), 3);
        Assert.Equal(0.30f, MasterBrightness.Scheduled(pts, At(1)), 3);
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

        Assert.Equal(12, pts.Count);
        Assert.Equal(1f, MasterBrightness.Scheduled(pts, At(13)));
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
