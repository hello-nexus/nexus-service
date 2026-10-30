using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.IdleDim;
using Nexus.Service.Lighting.Sun;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// The master brightness every frame writer caps its LEDs with: the slider
/// (<see cref="LightingSettings.GlobalBrightness"/>) capped again by the
/// time-of-day schedule while that is on and by the idle-dim ramp while the PC
/// is idle. One evaluation site, so each cap reaches every device family the
/// same way the slider does.
/// </summary>
public static class MasterBrightness
{
    /// <summary>The idle-dim cap every frame writer reads; the controller ramps it.</summary>
    public static readonly IdleDimRamp IdleRamp = new();

    /// <summary>Effective master level, 0..1, for the local clock right now.</summary>
    public static float Effective(LightingSettings lighting) =>
        Effective(lighting, DateTime.Now, IdleRamp.Cap());

    /// <summary>The master level plus the combine mode, snapshotted once per operation
    /// so a writer reads the settings a single time. Writers pass this down to the
    /// place they apply the per-device level and call <see cref="MasterLevel.Combine"/>.</summary>
    public static MasterLevel Resolve(NexusSettings settings) =>
        new(Effective(settings.Lighting), settings.MasterScalesDevices);

    /// <summary>The one rule joining a device's own level and the master level, both
    /// 0..1. Off: master caps the device (<c>min</c>). On: master multiplies the
    /// device, so the device level is a calibration offset.</summary>
    public static double Combine(double device01, double master, bool scales) =>
        scales ? device01 * master : Math.Min(device01, master);

    /// <summary>Effective master level at <paramref name="localNow"/>. With the
    /// schedule's sun shift on and today's sun times known, the generated daylight
    /// curve replaces the user's points; otherwise the user's curve applies.
    /// The slider is clamped, not sanitised: a non-finite level propagates,
    /// which the POST route guards against upstream.</summary>
    public static float Effective(LightingSettings lighting, DateTime localNow, float idleCap = 1f)
    {
        var global = Math.Min(Math.Clamp(lighting.GlobalBrightness, 0f, 1f), idleCap);
        var schedule = lighting.BrightnessSchedule;
        if (schedule is null || !schedule.Enabled)
        {
            return global;
        }
        if (schedule.SunShiftOn
            && SunShift.PlanFor(SunShift.Effective(schedule), DateOnly.FromDateTime(localNow), schedule.SunNightLevel, schedule.SunDayLevel) is { Day.Status: SunStatus.Ok } plan)
        {
            return Math.Min(global, ScheduledSun(plan, localNow.TimeOfDay));
        }
        return Math.Min(global, Scheduled(schedule.Points, localNow.TimeOfDay));
    }

    /// <summary>The generated sun curve's level, 0..1, at <paramref name="timeOfDay"/>,
    /// read from the plan's cached points.</summary>
    public static float ScheduledSun(SunPlan plan, TimeSpan timeOfDay)
    {
        var minute = ((int)timeOfDay.TotalMinutes % MinutesPerDay + MinutesPerDay) % MinutesPerDay;
        var level = CurveEasing.Interpolate(plan.Hours, plan.Levels, minute / 60.0, smooth: false, wrapMin: 0, wrapSpan: HoursPerDay);
        return (float)Math.Clamp(level / 100.0, 0.0, 1.0);
    }

    /// <summary>The schedule's level, 0..1, at <paramref name="timeOfDay"/>:
    /// straight lines between the points (<see cref="CurveEasing"/>), evaluated
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
        var level = CurveEasing.Interpolate(xs, ys, minute / 60.0, smooth: false, wrapMin: 0, wrapSpan: HoursPerDay);
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

    private static readonly (int Hour, int Brightness)[] DefaultPoints =
    {
        (5, 10), (7, 30), (10, 100), (17, 100), (20, 30), (22, 10),
    };

    /// <summary>True when <paramref name="points"/> are the out-of-box curve,
    /// whatever order they were saved in. Allocation-free for the sorted list
    /// the route persists: this runs on the frame path while the choice is unset.</summary>
    public static bool IsDefaultSchedule(List<BrightnessSchedulePoint>? points)
    {
        if (points is null || points.Count != DefaultPoints.Length)
        {
            return false;
        }
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Hour < points[i - 1].Hour)
            {
                return IsDefaultSchedule(points.OrderBy(p => p.Hour).ToList());
            }
        }
        for (var i = 0; i < DefaultPoints.Length; i++)
        {
            if (points[i].Hour != DefaultPoints[i].Hour || points[i].Brightness != DefaultPoints[i].Brightness)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The out-of-box curve follows daylight: a dim night floor, a
    /// dawn that starts slow and then climbs to full, full through the day,
    /// and the same shape mirrored through dusk back to the floor.</summary>
    public static List<BrightnessSchedulePoint> DefaultSchedule() =>
        DefaultPoints.Select(p => new BrightnessSchedulePoint { Hour = p.Hour, Brightness = p.Brightness }).ToList();

    private const int MinutesPerDay = 24 * 60;
    private const double HoursPerDay = 24;
}

/// <summary>A resolved master level with the mode that joins it to a device's own
/// level. See <see cref="MasterBrightness.Combine"/>.</summary>
public readonly record struct MasterLevel(float Level, bool Scales)
{
    /// <summary>Joins a device level (0..1) to this master level.</summary>
    public double Combine(double device01) => MasterBrightness.Combine(device01, Level, Scales);

    /// <summary>Joins a device level on an integer scale (<paramref name="max"/> steps, e.g. 0..4)
    /// to this master level, returning a level on the same scale, unrounded.</summary>
    public double CombineSteps(double deviceSteps, double max) => Combine(deviceSteps / max) * max;
}
