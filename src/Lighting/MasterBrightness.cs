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
    /// Clamps the slider exactly as the writers did before the schedule existed,
    /// so a non-finite level still propagates unchanged.</summary>
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
    /// piecewise-linear between the points by the minute, wrapping midnight.
    /// Seconds are dropped so the level moves once a minute, never per frame.
    /// An empty schedule is no cap.</summary>
    public static float Scheduled(List<BrightnessSchedulePoint>? points, TimeSpan timeOfDay)
    {
        if (points is null || points.Count == 0)
        {
            return 1f;
        }
        var minute = ((int)timeOfDay.TotalMinutes % MinutesPerDay + MinutesPerDay) % MinutesPerDay;
        // The list is read unlocked while a route may swap it, so walk one
        // reference and never index past the count seen here.
        BrightnessSchedulePoint? before = null, after = null, first = null, last = null;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (first is null || p.Hour < first.Hour) first = p;
            if (last is null || p.Hour > last.Hour) last = p;
            var at = p.Hour * 60;
            if (at <= minute && (before is null || at > before.Hour * 60)) before = p;
            if (at > minute && (after is null || at < after.Hour * 60)) after = p;
        }
        if (first is null || last is null)
        {
            return 1f;
        }
        // Wrap: before midnight the segment runs from the last point to the
        // first point of the next day, and after midnight the other way round.
        int fromMinute, toMinute;
        float fromLevel, toLevel;
        if (before is null)
        {
            fromMinute = last.Hour * 60 - MinutesPerDay; fromLevel = last.Brightness;
            toMinute = first.Hour * 60; toLevel = first.Brightness;
        }
        else if (after is null)
        {
            fromMinute = last.Hour * 60; fromLevel = last.Brightness;
            toMinute = first.Hour * 60 + MinutesPerDay; toLevel = first.Brightness;
        }
        else
        {
            fromMinute = before.Hour * 60; fromLevel = before.Brightness;
            toMinute = after.Hour * 60; toLevel = after.Brightness;
        }
        var span = toMinute - fromMinute;
        var level = span <= 0
            ? fromLevel
            : fromLevel + (toLevel - fromLevel) * ((minute - fromMinute) / (float)span);
        return Math.Clamp(level / 100f, 0f, 1f);
    }

    /// <summary>The out-of-box curve: full at midday, dim through the night,
    /// one point every two hours.</summary>
    public static List<BrightnessSchedulePoint> DefaultSchedule() => new()
    {
        new() { Hour = 0, Brightness = 20 },
        new() { Hour = 2, Brightness = 15 },
        new() { Hour = 4, Brightness = 15 },
        new() { Hour = 6, Brightness = 40 },
        new() { Hour = 8, Brightness = 70 },
        new() { Hour = 10, Brightness = 90 },
        new() { Hour = 12, Brightness = 100 },
        new() { Hour = 14, Brightness = 100 },
        new() { Hour = 16, Brightness = 90 },
        new() { Hour = 18, Brightness = 70 },
        new() { Hour = 20, Brightness = 50 },
        new() { Hour = 22, Brightness = 30 },
    };

    private const int MinutesPerDay = 24 * 60;
}
