using Nexus.Service.Lighting.Sun;

namespace Nexus.Service.Tests.Lighting;

public sealed class SunTimesTests
{
    // Minutes after 00:00 UTC of the date; a value above 1440 is the next UTC day,
    // below 0 the previous one. Reference values from published almanac tables.
    [Theory]
    [InlineData(40.7128, -74.0060, "2026-06-21", 565, 1471)]
    [InlineData(40.7128, -74.0060, "2026-12-21", 736, 1292)]
    [InlineData(59.9139, 10.7522, "2026-06-21", 114, 1244)]
    [InlineData(59.9139, 10.7522, "2026-12-21", 498, 852)]
    [InlineData(1.3521, 103.8198, "2026-03-20", -52, 675)]
    [InlineData(0.0, 0.0, "2026-03-20", 364, 1091)]
    public void Known_cities_match_almanac_within_two_minutes(double lat, double lon, string date, double rise, double set)
    {
        var (status, r, s) = SunTimes.ComputeUtcMinutes(lat, lon, DateOnly.Parse(date));

        Assert.Equal(SunStatus.Ok, status);
        Assert.InRange(r, rise - 2, rise + 2);
        Assert.InRange(s, set - 2, set + 2);
    }

    [Fact]
    public void South_pole_in_october_is_polar_day()
    {
        Assert.Equal(SunStatus.PolarDay, SunTimes.ComputeUtcMinutes(-90, 0, new DateOnly(2026, 10, 17)).Status);
    }

    [Fact]
    public void Longyearbyen_in_december_is_polar_night()
    {
        var day = SunTimes.ForLocalDate(78.22, 15.65, new DateOnly(2026, 12, 21), TimeZoneInfo.Utc);

        Assert.Equal(SunStatus.PolarNight, day.Status);
        Assert.Null(day.SunriseMin);
        Assert.Null(day.SunsetMin);
    }

    [Fact]
    public void Local_conversion_applies_the_zone_offset()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var date = new DateOnly(2026, 6, 21);
        var (_, riseUtc, setUtc) = SunTimes.ComputeUtcMinutes(59.9139, 10.7522, date);

        var local = SunTimes.ForLocalDate(59.9139, 10.7522, date, tz);

        Assert.Equal(riseUtc + 120, local.SunriseMin!.Value, 6);
        Assert.Equal(setUtc + 120, local.SunsetMin!.Value, 6);
    }

    [Fact]
    public void Local_conversion_wraps_an_event_that_crosses_midnight_in_the_zone()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("minus5", TimeSpan.FromHours(-5), "minus5", "minus5");

        var local = SunTimes.ForLocalDate(40.7128, -74.0060, new DateOnly(2026, 6, 21), tz);

        // Sunset 00:31 UTC the next day is 19:31 on the wall clock of that date.
        Assert.InRange(local.SunsetMin!.Value, 19 * 60 + 29, 19 * 60 + 33);
    }
}
