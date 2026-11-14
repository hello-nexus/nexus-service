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
    [InlineData("bogus")]
    [InlineData("")]
    public void UnknownSensorId_FallsBackLikeUnknownSensor(string sensorId)
        => Assert.Equal("--", TryxPanoramaHub.FormatClockValue(sensorId, Afternoon, "system", "system"));
}
