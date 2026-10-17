using System;
using System.Threading;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Sun;

/// <summary>The day's sunrise and sunset, resolved for one date and place.
/// Immutable, so the frame path reads it with one volatile load.</summary>
public sealed class SunPlan
{
    public SunPlan(DateOnly date, double lat, double lon, SunDay day)
    {
        Date = date;
        Lat = lat;
        Lon = lon;
        Day = day;
        if (day.Status == SunStatus.Ok)
        {
            Sunrise = day.SunriseMin!.Value;
            Sunset = day.SunsetMin!.Value;
            (Minutes, Levels) = SunShift.GeneratePoints(Sunrise, Sunset);
        }
    }

    public DateOnly Date { get; }
    public double Lat { get; }
    public double Lon { get; }
    public SunDay Day { get; }
    public double Sunrise { get; }
    public double Sunset { get; }
    /// <summary>Generated curve points, minutes of day ascending. Empty unless the status is ok.</summary>
    public double[] Minutes { get; } = Array.Empty<double>();
    /// <summary>Brightness percent per entry of <see cref="Minutes"/>.</summary>
    public double[] Levels { get; } = Array.Empty<double>();
}

/// <summary>Slides a day curve's morning points with sunrise and its evening
/// points with sunset, and holds the cached plan the frame path reads.</summary>
public static class SunShift
{
    private static SunPlan? _plan;
    private static volatile WeatherSavedLocation? _auto;

    /// <summary>The IP-derived location, set by the background resolver. Null until resolved.</summary>
    public static WeatherSavedLocation? AutoLocation
    {
        get => _auto;
        set => _auto = value;
    }

    /// <summary>The place the schedule shifts for: the chosen one, else the resolved IP location.</summary>
    public static WeatherSavedLocation? Effective(BrightnessSchedule schedule) => schedule.SunLocation ?? _auto;

    /// <summary>The cached plan for <paramref name="localDate"/> at <paramref name="location"/>,
    /// computed once per date and place (microseconds of arithmetic, no I/O).
    /// Null when there is no location.</summary>
    public static SunPlan? PlanFor(WeatherSavedLocation? location, DateOnly localDate)
    {
        if (location is null)
        {
            return null;
        }
        var cached = Volatile.Read(ref _plan);
        if (cached is not null && cached.Date == localDate && cached.Lat == location.Lat && cached.Lon == location.Lon)
        {
            return cached;
        }
        var plan = new SunPlan(localDate, location.Lat, location.Lon,
            SunTimes.ForLocalDate(location.Lat, location.Lon, localDate, TimeZoneInfo.Local));
        Volatile.Write(ref _plan, plan);
        return plan;
    }

    public static void ResetForTests()
    {
        Volatile.Write(ref _plan, null);
        _auto = null;
    }

    /// <summary>Today's generated curve: dim before dawn, climbing to full a
    /// couple of hours after sunrise, full until a couple of hours before
    /// sunset, then down to the night floor. A short day folds both full points
    /// into solar noon, a short night folds both floor points into solar
    /// midnight. Sorted by minute.</summary>
    public static (double[] Minutes, double[] Levels) GeneratePoints(double sunrise, double sunset)
    {
        var day = ((sunset - sunrise) % 1440 + 1440) % 1440;
        var s = sunrise + day;
        var noon = (sunrise + s) / 2;
        var rise100 = sunrise + 120;
        var set100 = s - 120;
        if (rise100 > set100)
        {
            rise100 = set100 = noon;
        }
        var predawn = sunrise - 60 + 1440;
        var dusk = s + 60;
        if (dusk > predawn)
        {
            dusk = predawn = (s + sunrise + 1440) / 2;
        }
        double[] mins = { predawn, sunrise + 1440, rise100 + 1440, set100 + 1440, s + 1440, dusk + 1440 };
        double[] levels = { 10, 30, 100, 100, 30, 10 };
        for (var i = 0; i < mins.Length; i++)
        {
            mins[i] = ((mins[i] - 1440) % 1440 + 1440) % 1440;
        }
        Array.Sort(mins, levels);
        return (mins, levels);
    }
}
