using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Weather;
using Nexus.Service.Platform.Weather;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Weather;

/// <summary>
/// Body validation for PUT /api/weather/locations and the current-hour lookup
/// the snapshot's UV / visibility / dew point derive from. Pure logic, no HTTP.
/// </summary>
public sealed class WeatherRoutesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("kelvin", null)]
    [InlineData("c", null)]
    [InlineData("auto", "auto")]
    [InlineData("C", "C")]
    [InlineData("F", "F")]
    public void ResolveUnit_accepts_only_auto_C_F(string? input, string? expected)
    {
        Assert.Equal(expected, WeatherRoutes.ResolveUnit(input));
    }

    [Fact]
    public void SanitizeLocations_drops_invalid_and_dedupes_by_coordinate()
    {
        var input = new List<WeatherSavedLocationDto>
        {
            new() { Lat = 45.65, Lon = 13.78, Label = " Trieste ", Cc = "it" },
            new() { Lat = 45.6501, Lon = 13.7801, Label = "Trieste again", Cc = "IT" },
            new() { Lat = 91, Lon = 0, Label = "North of the pole", Cc = "XX" },
            new() { Lat = 0, Lon = 181, Label = "Off the map", Cc = "XX" },
            new() { Lat = double.NaN, Lon = 0, Label = "NaN", Cc = "XX" },
            new() { Lat = 48.85, Lon = 2.35, Label = "   ", Cc = "FR" },
            new() { Lat = 48.85, Lon = 2.35, Label = "Paris", Cc = "france" },
        };

        var result = WeatherRoutes.SanitizeLocations(input);

        Assert.Equal(2, result.Count);
        Assert.Equal("Trieste", result[0].Label);
        Assert.Equal("IT", result[0].Cc);
        Assert.Equal("Paris", result[1].Label);
        Assert.Equal("FRA", result[1].Cc);
    }

    [Fact]
    public void SanitizeLocations_caps_at_the_maximum_keeping_order()
    {
        var input = Enumerable.Range(0, WeatherRoutes.MaxLocations + 5)
            .Select(i => new WeatherSavedLocationDto { Lat = i, Lon = i, Label = $"L{i}", Cc = "XX" })
            .ToList();

        var result = WeatherRoutes.SanitizeLocations(input);

        Assert.Equal(WeatherRoutes.MaxLocations, result.Count);
        Assert.Equal("L0", result[0].Label);
        Assert.Equal($"L{WeatherRoutes.MaxLocations - 1}", result[^1].Label);
    }

    [Fact]
    public void SanitizeLocations_truncates_long_labels()
    {
        var input = new List<WeatherSavedLocationDto>
        {
            new() { Lat = 1, Lon = 1, Label = new string('x', 200), Cc = "XX" },
        };

        var result = WeatherRoutes.SanitizeLocations(input);

        Assert.Equal(80, result[0].Label.Length);
    }

    [Fact]
    public void FindCurrentHour_matches_on_the_hour_prefix()
    {
        var hourly = new OpenMeteoHourly
        {
            Time = new[] { "2026-09-12T18:00", "2026-09-12T19:00", "2026-09-12T20:00" },
            DewPoint2m = new double?[] { 10, 11, 12 },
            UvIndex = new double?[] { 3, 2, null },
            Visibility = new double?[] { 20000, 15000, 10000 },
        };

        var hour = OpenMeteoWeatherProvider.FindCurrentHour(hourly, "2026-09-12T19:30");

        Assert.NotNull(hour);
        Assert.Equal(11, hour!.DewPointC);
        Assert.Equal(2, hour.UvIndex);
        Assert.Equal(15000, hour.VisibilityM);
    }

    [Fact]
    public void FindCurrentHour_returns_null_when_no_row_matches()
    {
        var hourly = new OpenMeteoHourly { Time = new[] { "2026-09-12T18:00" } };

        Assert.Null(OpenMeteoWeatherProvider.FindCurrentHour(hourly, "2026-09-13T01:00"));
        Assert.Null(OpenMeteoWeatherProvider.FindCurrentHour(hourly, ""));
        Assert.Null(OpenMeteoWeatherProvider.FindCurrentHour(null, "2026-09-12T18:00"));
    }
}
