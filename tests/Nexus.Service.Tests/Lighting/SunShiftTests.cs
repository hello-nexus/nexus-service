using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Sun;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

[Collection("SunShift")]
public sealed class SunShiftTests
{
    // Mirrored by nexus-web: sunrise, sunset, generated points, sampled levels.
    public sealed record Vector(int Sunrise, int Sunset, (double Min, double Level)[] Expected, (int Minute, double Level)[] EvalAt);

    public static readonly Vector[] Vectors =
    {
        new(420, 1140, new (double, double)[] { (360.0, 10.0), (420.0, 30.0), (540.0, 100.0), (1020.0, 100.0), (1140.0, 30.0), (1200.0, 10.0) }, new (int, double)[] { (0, 10.0), (30, 10.0), (360, 10.0), (420, 30.0), (540, 100.0), (720, 100.0), (780, 100.0), (1020, 100.0), (1080, 65.0), (1140, 30.0), (1200, 10.0), (1380, 10.0), (1439, 10.0) }),
        new(300, 1290, new (double, double)[] { (240.0, 10.0), (300.0, 30.0), (420.0, 100.0), (1170.0, 100.0), (1290.0, 30.0), (1350.0, 10.0) }, new (int, double)[] { (0, 10.0), (30, 10.0), (240, 10.0), (300, 30.0), (360, 65.0), (420, 100.0), (720, 100.0), (795, 100.0), (1080, 100.0), (1170, 100.0), (1290, 30.0), (1350, 10.0), (1380, 10.0), (1439, 10.0) }),
        new(660, 840, new (double, double)[] { (600.0, 10.0), (660.0, 30.0), (750.0, 100.0), (750.0, 100.0), (840.0, 30.0), (900.0, 10.0) }, new (int, double)[] { (0, 10.0), (30, 10.0), (360, 10.0), (600, 10.0), (660, 30.0), (720, 76.6667), (750, 100.0), (840, 30.0), (900, 10.0), (1080, 10.0), (1380, 10.0), (1439, 10.0) }),
        new(90, 1410, new (double, double)[] { (30.0, 10.0), (30.0, 10.0), (90.0, 30.0), (210.0, 100.0), (1290.0, 100.0), (1410.0, 30.0) }, new (int, double)[] { (0, 20.0), (30, 10.0), (90, 30.0), (210, 100.0), (360, 100.0), (720, 100.0), (750, 100.0), (1080, 100.0), (1290, 100.0), (1380, 47.5), (1410, 30.0), (1439, 20.3333) }),
        new(60, 1430, new (double, double)[] { (25.0, 10.0), (25.0, 10.0), (60.0, 30.0), (180.0, 100.0), (1310.0, 100.0), (1430.0, 30.0) }, new (int, double)[] { (0, 24.2857), (25, 10.0), (30, 12.8571), (60, 30.0), (180, 100.0), (360, 100.0), (720, 100.0), (745, 100.0), (1080, 100.0), (1310, 100.0), (1380, 59.1667), (1430, 30.0), (1439, 24.8571) }),
        new(325, 1231, new (double, double)[] { (265.0, 10.0), (325.0, 30.0), (445.0, 100.0), (1111.0, 100.0), (1231.0, 30.0), (1291.0, 10.0) }, new (int, double)[] { (0, 10.0), (30, 10.0), (265, 10.0), (325, 30.0), (360, 50.4167), (445, 100.0), (720, 100.0), (778, 100.0), (1080, 100.0), (1111, 100.0), (1231, 30.0), (1291, 10.0), (1380, 10.0), (1439, 10.0) }),
    };

    public static TheoryData<int> VectorIndexes() => new() { 0, 1, 2, 3, 4, 5 };

    private static SunPlan PlanOf(int sunrise, int sunset) =>
        new(new DateOnly(2026, 6, 21), 0, 0, new SunDay(SunStatus.Ok, sunrise, sunset));

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public void Generated_points_match_the_shared_vectors(int index)
    {
        var v = Vectors[index];

        var plan = PlanOf(v.Sunrise, v.Sunset);

        Assert.Equal(v.Expected.Length, plan.Minutes.Length);
        for (var i = 0; i < v.Expected.Length; i++)
        {
            Assert.Equal(v.Expected[i].Min, plan.Minutes[i], 6);
            Assert.Equal(v.Expected[i].Level, plan.Levels[i], 6);
        }
    }

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public void Evaluation_matches_the_shared_vectors(int index)
    {
        var v = Vectors[index];
        var plan = PlanOf(v.Sunrise, v.Sunset);

        foreach (var (minute, level) in v.EvalAt)
        {
            Assert.Equal(level, MasterBrightness.ScheduledSun(plan, TimeSpan.FromMinutes(minute)) * 100.0, 2);
        }
    }

    [Fact]
    public void Short_day_collapses_the_full_points_to_solar_noon()
    {
        var plan = PlanOf(660, 840);

        Assert.Equal(new[] { 750.0, 750.0 }, plan.Minutes.Where((_, i) => plan.Levels[i] == 100).ToArray());
    }

    [Fact]
    public void Short_night_collapses_the_floor_points_to_solar_midnight()
    {
        var plan = PlanOf(60, 1430);

        Assert.Equal(new[] { 25.0, 25.0 }, plan.Minutes.Where((_, i) => plan.Levels[i] == 10).ToArray());
    }

    [Fact]
    public void Coincident_points_never_throw_or_go_non_finite()
    {
        foreach (var (rise, set) in new[] { (660, 840), (60, 1430), (0, 1), (700, 700), (0, 0) })
        {
            var plan = PlanOf(rise, set);
            for (var m = 0; m < 1440; m += 7)
            {
                var level = MasterBrightness.ScheduledSun(plan, TimeSpan.FromMinutes(m));
                Assert.InRange(level, 0f, 1f);
            }
        }
    }

    private static LightingSettings Lighting(bool sunShift, WeatherSavedLocation? location, bool enabled = true) => new()
    {
        BrightnessSchedule = new()
        {
            Enabled = enabled,
            SunShift = sunShift,
            SunLocation = location,
            Points = new() { new() { Hour = 0, Brightness = 20 }, new() { Hour = 12, Brightness = 20 } },
        },
    };

    // The machine zone can differ from Oslo's, so sunset may sit before sunrise on the wall clock.
    private static int SolarNoon(SunDay day) =>
        (int)(day.SunriseMin!.Value + ((day.SunsetMin!.Value - day.SunriseMin.Value) % 1440 + 1440) % 1440 / 2) % 1440;

    private static readonly WeatherSavedLocation Oslo = new() { Lat = 59.9139, Lon = 10.7522, Label = "Oslo", Cc = "NO" };

    [Fact]
    public void Sun_shift_on_ignores_the_users_points_and_uses_the_generated_curve()
    {
        SunShift.ResetForTests();
        var date = new DateOnly(2026, 6, 21);
        var day = SunTimes.ForLocalDate(Oslo.Lat, Oslo.Lon, date, TimeZoneInfo.Local);
        var noon = SolarNoon(day);
        var at = date.ToDateTime(TimeOnly.MinValue).AddMinutes(noon);

        Assert.Equal(1f, MasterBrightness.Effective(Lighting(true, Oslo), at));
        Assert.Equal(0.2f, MasterBrightness.Effective(Lighting(false, Oslo), at));
    }

    [Fact]
    public void Sun_shift_without_a_location_falls_back_to_the_users_curve()
    {
        SunShift.ResetForTests();
        var at = new DateTime(2026, 6, 21, 12, 0, 0);

        Assert.Equal(0.2f, MasterBrightness.Effective(Lighting(true, null), at));
    }

    [Fact]
    public void Sun_shift_with_a_resolved_auto_location_uses_it()
    {
        SunShift.ResetForTests();
        SunShift.AutoLocation = Oslo;
        try
        {
            var date = new DateOnly(2026, 6, 21);
            var day = SunTimes.ForLocalDate(Oslo.Lat, Oslo.Lon, date, TimeZoneInfo.Local);
            var noon = SolarNoon(day);

            Assert.Equal(1f, MasterBrightness.Effective(Lighting(true, null), date.ToDateTime(TimeOnly.MinValue).AddMinutes(noon)));
        }
        finally
        {
            SunShift.ResetForTests();
        }
    }

    [Fact]
    public void Polar_status_uses_the_users_curve()
    {
        SunShift.ResetForTests();
        var longyearbyen = new WeatherSavedLocation { Lat = 78.22, Lon = 15.65, Label = "Longyearbyen", Cc = "SJ" };

        Assert.Equal(0.2f, MasterBrightness.Effective(Lighting(true, longyearbyen), new DateTime(2026, 12, 21, 12, 0, 0)));
    }

    [Fact]
    public void The_frame_path_does_not_allocate_with_an_unchosen_or_chosen_sun_shift()
    {
        SunShift.ResetForTests();
        var at = new DateTime(2026, 6, 21, 12, 0, 0);
        var unchosenDefault = new LightingSettings { BrightnessSchedule = new() { Enabled = true } };
        var unchosenCustom = Lighting(true, null);
        unchosenCustom.BrightnessSchedule.SunShift = null;
        var withPlan = Lighting(true, Oslo);
        var all = new[] { unchosenDefault, unchosenCustom, withPlan };
        foreach (var l in all)
        {
            MasterBrightness.Effective(l, at);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            foreach (var l in all)
            {
                MasterBrightness.Effective(l, at);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Plan_is_cached_per_date_and_place()
    {
        SunShift.ResetForTests();
        var date = new DateOnly(2026, 6, 21);

        var first = SunShift.PlanFor(Oslo, date);

        Assert.Same(first, SunShift.PlanFor(Oslo, date));
        Assert.NotSame(first, SunShift.PlanFor(Oslo, date.AddDays(1)));
    }
}

/// <summary>The sun-shift tests share process-wide state (the resolved place and the day plan), so they run one at a time.</summary>
[CollectionDefinition("SunShift", DisableParallelization = true)]
public sealed class SunShiftCollection;
