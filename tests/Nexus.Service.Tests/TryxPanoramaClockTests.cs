using System;
using System.Globalization;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxPanoramaClockTests
{
    private static readonly DateTime Afternoon = new(2026, 11, 14, 15, 5, 9);

    // The service and test host run with invariant globalization, so culture patterns are
    // supplied by hand instead of relying on ICU data.
    private static CultureInfo Culture(string shortTime, string shortDate)
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.DateTimeFormat.ShortTimePattern = shortTime;
        c.DateTimeFormat.ShortDatePattern = shortDate;
        return c;
    }

    [Theory]
    [InlineData("time", "system", "HH:mm", "d.M.yyyy", "15:05")]
    [InlineData("time", "system", "h:mm tt", "M/d/yyyy", "3:05 PM")]
    [InlineData("time", "24h", "h:mm tt", "M/d/yyyy", "15:05")]
    [InlineData("time", "12h", "HH:mm", "d.M.yyyy", "3:05 PM")]
    [InlineData("date", "system", "HH:mm", "d.M.yyyy", "14.11.2026")]
    [InlineData("date", "24h", "h:mm tt", "M/d/yyyy", "11/14/2026")]
    [InlineData("bogus", "system", "HH:mm", "d.M.yyyy", "--")]
    [InlineData("", "system", "HH:mm", "d.M.yyyy", "--")]
    public void FormatClockValue_UsesPreferenceThenCulture(string sensorId, string timeFormat, string shortTime, string shortDate, string expected)
    {
        var actual = TryxPanoramaHub.FormatClockValue(sensorId, Afternoon, timeFormat, Culture(shortTime, shortDate));
        Assert.Equal(expected, actual);
    }
}
