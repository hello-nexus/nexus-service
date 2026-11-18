using System;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxPanoramaClockTests
{
    private static readonly DateTime Afternoon = new(2026, 11, 4, 15, 5, 9);

    [Theory]
    [InlineData("system", "15:05")]
    [InlineData("24h", "15:05")]
    [InlineData("12h", "3:05 PM")]
    public void Time_HonorsPreference(string timeFormat, string expected)
        => Assert.Equal(expected, TryxPanoramaHub.FormatClockValue("time", Afternoon, timeFormat, "system"));

    [Theory]
    [InlineData("system", "Wed, Nov 4")]
    [InlineData("not-a-pattern", "Wed, Nov 4")]
    [InlineData("ddd, d mmm", "Wed, 4 Nov")]
    [InlineData("ddd d mmm", "Wed 4 Nov")]
    [InlineData("ddd, mmm d", "Wed, Nov 4")]
    [InlineData("d mmm", "4 Nov")]
    [InlineData("mmm d", "Nov 4")]
    [InlineData("ddd, d mmm yyyy", "Wed, 4 Nov 2026")]
    [InlineData("d mmm yyyy", "4 Nov 2026")]
    [InlineData("mmm d, yyyy", "Nov 4, 2026")]
    [InlineData("dd/mm/yy", "04/11/26")]
    [InlineData("dd/mm/yyyy", "04/11/2026")]
    [InlineData("mm/dd/yy", "11/04/26")]
    [InlineData("mm/dd/yyyy", "11/04/2026")]
    [InlineData("dd-mm-yy", "04-11-26")]
    [InlineData("dd-mm-yyyy", "04-11-2026")]
    [InlineData("dd.mm.yy", "04.11.26")]
    [InlineData("dd.mm.yyyy", "04.11.2026")]
    [InlineData("yyyy-mm-dd", "2026-11-04")]
    [InlineData("yyyy/mm/dd", "2026/11/04")]
    [InlineData("yyyy. m. d.", "2026. 11. 4.")]
    [InlineData("yyyy年m月d日", "2026年11月4日")]
    public void Date_MapsClientPatternKeys(string dateFormat, string expected)
        => Assert.Equal(expected, TryxPanoramaHub.FormatClockValue("date", Afternoon, "system", dateFormat));

    [Theory]
    [InlineData("system", true, "3:05 PM")]
    [InlineData("system", false, "15:05")]
    [InlineData("24h", true, "15:05")]
    [InlineData("12h", false, "3:05 PM")]
    public void SystemTime_FollowsTheUsersClock(string timeFormat, bool userUses12Hour, string expected)
        => Assert.Equal(expected, TryxPanoramaHub.FormatClockValue("time", Afternoon, timeFormat, "system", userUses12Hour));

    [Theory]
    [InlineData("h:mm tt", true)]
    [InlineData("hh:mm tt", true)]
    [InlineData("tt h:mm", true)]
    [InlineData("h:mm", true)]
    [InlineData("H:mm", false)]
    [InlineData("HH:mm", false)]
    [InlineData("HH:mm 'h'", false)]
    [InlineData("H 'h' mm", false)]
    [InlineData("HH 'tt' mm", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ShortTimePattern_MapsToHourCycle(string? pattern, bool expected)
        => Assert.Equal(expected, TryxUserClockFormat.ParseUses12Hour(pattern));

    [Theory]
    [InlineData("system", "yyyy-mm-dd", false, "2026-11-04  15:05")]
    [InlineData("12h", "ddd, d mmm", false, "Wed, 4 Nov  3:05 PM")]
    [InlineData("system", "system", true, "Wed, Nov 4  3:05 PM")]
    public void DateTime_JoinsDateAndTimeWithTwoSpaces(string timeFormat, string dateFormat, bool userUses12Hour, string expected)
        => Assert.Equal(expected, TryxPanoramaHub.FormatClockValue("datetime", Afternoon, timeFormat, dateFormat, userUses12Hour));

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    public void UnknownSensorId_FallsBackLikeUnknownSensor(string sensorId)
        => Assert.Equal("--", TryxPanoramaHub.FormatClockValue(sensorId, Afternoon, "system", "system"));
}
