using System;

namespace Nexus.Service.Lighting.Sun;

public enum SunStatus
{
    Ok,
    PolarDay,
    PolarNight,
    NoLocation,
}

/// <summary>Sunrise and sunset for one local date, in PC-local minutes since
/// midnight. Both are null unless <see cref="Status"/> is <see cref="SunStatus.Ok"/>.</summary>
public sealed record SunDay(SunStatus Status, double? SunriseMin, double? SunsetMin);

/// <summary>NOAA solar position algorithm (the general solar calculator
/// formulas), offline. Apparent sunrise uses the standard solar zenith that
/// includes refraction and the solar disc radius.</summary>
public static class SunTimes
{
    private const double SunriseZenithDeg = 90.833;
    private const double MinutesPerDay = 1440;

    /// <summary>Sunrise and sunset as minutes after 00:00 UTC of
    /// <paramref name="date"/>, for the solar day of that date at the given
    /// longitude. Values can fall below 0 or above 1440 for longitudes far from
    /// the prime meridian. Status is polar-day or polar-night when the sun does
    /// not cross the horizon.</summary>
    public static (SunStatus Status, double SunriseUtcMin, double SunsetUtcMin) ComputeUtcMinutes(
        double lat, double lon, DateOnly date)
    {
        var jd = date.DayNumber - new DateOnly(1970, 1, 1).DayNumber + 2440587.5;
        var rise = EventUtcMinutes(true, jd, lat, lon, out var status);
        if (status != SunStatus.Ok)
        {
            return (status, 0, 0);
        }
        var set = EventUtcMinutes(false, jd, lat, lon, out status);
        return status == SunStatus.Ok ? (SunStatus.Ok, rise, set) : (status, 0, 0);
    }

    /// <summary>Sunrise and sunset for the local calendar date, in
    /// <paramref name="tz"/> minutes since midnight. The solar maths runs in UTC
    /// and each instant is converted, so a DST day lands on the wall clock.</summary>
    public static SunDay ForLocalDate(double lat, double lon, DateOnly date, TimeZoneInfo tz)
    {
        var (status, riseUtc, setUtc) = ComputeUtcMinutes(lat, lon, date);
        if (status != SunStatus.Ok)
        {
            return new SunDay(status, null, null);
        }
        var origin = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        return new SunDay(SunStatus.Ok, LocalMinutes(origin.AddMinutes(riseUtc), tz), LocalMinutes(origin.AddMinutes(setUtc), tz));
    }

    private static double LocalMinutes(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, tz).TimeOfDay.TotalMinutes;

    private static double EventUtcMinutes(bool rising, double jdMidnight, double lat, double lon, out SunStatus status)
    {
        status = SunStatus.Ok;
        var minutes = 720.0;
        // The first pass places the event near solar noon; the second re-derives
        // the sun's position at the event itself.
        for (var pass = 0; pass < 2; pass++)
        {
            var t = (jdMidnight + minutes / MinutesPerDay - 2451545.0) / 36525.0;
            var (eqTime, decl) = Position(t);
            var latRad = Rad(lat);
            var cosHa = Math.Cos(Rad(SunriseZenithDeg)) / (Math.Cos(latRad) * Math.Cos(decl))
                - Math.Tan(latRad) * Math.Tan(decl);
            if (cosHa > 1)
            {
                status = SunStatus.PolarNight;
                return 0;
            }
            if (cosHa < -1)
            {
                status = SunStatus.PolarDay;
                return 0;
            }
            var ha = Deg(Math.Acos(cosHa));
            minutes = 720 - 4 * (lon + (rising ? ha : -ha)) - eqTime;
        }
        return minutes;
    }

    private static (double EqTime, double Declination) Position(double t)
    {
        var l0 = Mod(280.46646 + t * (36000.76983 + t * 0.0003032), 360);
        var m = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        var mRad = Rad(m);
        var e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        var c = Math.Sin(mRad) * (1.914602 - t * (0.004817 + 0.000014 * t))
            + Math.Sin(2 * mRad) * (0.019993 - 0.000101 * t)
            + Math.Sin(3 * mRad) * 0.000289;
        var trueLong = l0 + c;
        var omega = 125.04 - 1934.136 * t;
        var lambda = trueLong - 0.00569 - 0.00478 * Math.Sin(Rad(omega));
        var eps0 = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        var eps = eps0 + 0.00256 * Math.Cos(Rad(omega));
        var decl = Math.Asin(Math.Sin(Rad(eps)) * Math.Sin(Rad(lambda)));
        var y = Math.Pow(Math.Tan(Rad(eps) / 2), 2);
        var l0Rad = Rad(l0);
        var eq = y * Math.Sin(2 * l0Rad)
            - 2 * e * Math.Sin(mRad)
            + 4 * e * y * Math.Sin(mRad) * Math.Cos(2 * l0Rad)
            - 0.5 * y * y * Math.Sin(4 * l0Rad)
            - 1.25 * e * e * Math.Sin(2 * mRad);
        return (4 * Deg(eq), decl);
    }

    private static double Rad(double d) => d * Math.PI / 180;
    private static double Deg(double r) => r * 180 / Math.PI;
    private static double Mod(double v, double m) => (v % m + m) % m;
}
