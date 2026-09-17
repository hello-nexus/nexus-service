using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// The master brightness every frame writer caps its LEDs with: the slider
/// (<see cref="LightingSettings.GlobalBrightness"/>) capped again by the
/// time-of-day schedule while that is on. One evaluation site, so the schedule
/// reaches every device family the same way the slider does.
/// </summary>
public static class MasterBrightness
{
    /// <summary>Effective master level, 0..1, for the local clock right now.</summary>
    public static float Effective(LightingSettings lighting) =>
        Effective(lighting, DateTime.Now.TimeOfDay);

    /// <summary>Effective master level, 0..1, at <paramref name="timeOfDay"/>.
    /// The slider is clamped, not sanitised: a non-finite level propagates,
    /// which the POST route guards against upstream.</summary>
    public static float Effective(LightingSettings lighting, TimeSpan timeOfDay)
    {
        var global = Math.Clamp(lighting.GlobalBrightness, 0f, 1f);
        var schedule = lighting.BrightnessSchedule;
        if (schedule is null || !schedule.Enabled)
        {
            return global;
        }
        return Math.Min(global, Scheduled(schedule.Points, timeOfDay));
    }

    /// <summary>The schedule's level, 0..1, at <paramref name="timeOfDay"/>:
    /// a smooth curve through the points (<see cref="CurveEasing"/>), evaluated
    /// by the minute and wrapping midnight. Seconds are dropped so the level
    /// moves once a minute, never per frame. An empty schedule is no cap.</summary>
    public static float Scheduled(List<BrightnessSchedulePoint>? points, TimeSpan timeOfDay)
    {
        if (points is null || points.Count == 0)
        {
            return 1f;
        }
        var minute = ((int)timeOfDay.TotalMinutes % MinutesPerDay + MinutesPerDay) % MinutesPerDay;
        // Read unlocked on the frame path. Safe only because the route replaces
        // the list wholesale and never mutates one that has been published.
        var count = points.Count;
        Span<double> xs = count <= CurveEasing.StackPoints ? stackalloc double[count] : new double[count];
        Span<double> ys = count <= CurveEasing.StackPoints ? stackalloc double[count] : new double[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = points[i].Hour;
            ys[i] = points[i].Brightness;
        }
        SortByX(xs, ys);
        var level = CurveEasing.Interpolate(xs, ys, minute / 60.0, smooth: true, wrapMin: 0, wrapSpan: HoursPerDay);
        return (float)Math.Clamp(level / 100.0, 0.0, 1.0);
    }

    // Insertion sort: the route persists points sorted, so this is a no-op
    // walk in practice and never allocates on the frame path.
    private static void SortByX(Span<double> xs, Span<double> ys)
    {
        for (var i = 1; i < xs.Length; i++)
        {
            var x = xs[i];
            var y = ys[i];
            var j = i - 1;
            while (j >= 0 && xs[j] > x)
            {
                xs[j + 1] = xs[j];
                ys[j + 1] = ys[j];
                j--;
            }
            xs[j + 1] = x;
            ys[j + 1] = y;
        }
    }

    /// <summary>The out-of-box curve: full at midday, dim through the night.</summary>
    public static List<BrightnessSchedulePoint> DefaultSchedule() => new()
    {
        new() { Hour = 0, Brightness = 20 },
        new() { Hour = 4, Brightness = 15 },
        new() { Hour = 8, Brightness = 60 },
        new() { Hour = 12, Brightness = 100 },
        new() { Hour = 16, Brightness = 90 },
        new() { Hour = 20, Brightness = 50 },
    };

    private const int MinutesPerDay = 24 * 60;
    private const double HoursPerDay = 24;
}
