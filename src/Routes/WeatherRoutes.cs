using System;
using System.Collections.Generic;
using Nexus.Service.Auth;
using Nexus.Service.Models.Weather;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Weather;

namespace Nexus.Service.Routes;

public static class WeatherRoutes
{
    internal const int MaxLocations = 20;
    private const int MaxLabelLength = 80;

    public static void MapWeatherEndpoints(this WebApplication app)
    {
        app.MapGet("/api/weather", async (double? lat, double? lon, string? label, string? cc, IWeatherProvider provider) =>
            await provider.GetCurrentAsync(lat, lon, label, cc))
            .AllowPanel();

        app.MapGet("/api/weather/geocode", async (string? q, string? language, IWeatherProvider provider) =>
        {
            var results = await provider.SearchLocationsAsync(q ?? "", language);
            return new WeatherGeocodeResponse { Results = results };
        }).AllowPanel();

        app.MapGet("/api/weather/locations", (IConfigStore store) => ToDto(store.Load().Weather)).AllowPanel();

        app.MapPut("/api/weather/locations", (WeatherPrefsDto body, IConfigStore store) =>
        {
            var unit = ResolveUnit(body.Unit);
            var locations = body.Locations is null ? null : SanitizeLocations(body.Locations);
            store.Update(s =>
            {
                if (unit is not null) s.Weather.Unit = unit;
                if (locations is not null) s.Weather.Locations = locations;
            });
            return ToDto(store.Load().Weather);
        }).AllowPanel();
    }

    private static WeatherPrefsDto ToDto(WeatherSettings weather)
    {
        var list = new List<WeatherSavedLocationDto>(weather.Locations.Count);
        foreach (var l in weather.Locations)
        {
            list.Add(new WeatherSavedLocationDto { Lat = l.Lat, Lon = l.Lon, Label = l.Label, Cc = l.Cc });
        }
        return new WeatherPrefsDto { Unit = weather.Unit, Locations = list };
    }

    /// <summary>Null when absent or not one of auto/C/F, so the PUT leaves the stored value alone.</summary>
    internal static string? ResolveUnit(string? unit) =>
        unit is "auto" or "C" or "F" ? unit : null;

    /// <summary>Drops out-of-range coordinates and empty labels, dedupes by rounded coordinate, caps the list.</summary>
    internal static List<WeatherSavedLocation> SanitizeLocations(List<WeatherSavedLocationDto> input)
    {
        var result = new List<WeatherSavedLocation>(Math.Min(input.Count, MaxLocations));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in input)
        {
            if (!double.IsFinite(l.Lat) || !double.IsFinite(l.Lon)) continue;
            if (l.Lat < -90 || l.Lat > 90 || l.Lon < -180 || l.Lon > 180) continue;
            var label = (l.Label ?? "").Trim();
            if (label.Length == 0) continue;
            if (label.Length > MaxLabelLength) label = label.Substring(0, MaxLabelLength);
            var cc = (l.Cc ?? "").Trim().ToUpperInvariant();
            if (cc.Length > 3) cc = cc.Substring(0, 3);
            if (!seen.Add(CoordinateKey(l.Lat, l.Lon))) continue;
            result.Add(new WeatherSavedLocation { Lat = l.Lat, Lon = l.Lon, Label = label, Cc = cc });
            if (result.Count == MaxLocations) break;
        }
        return result;
    }

    internal static string CoordinateKey(double lat, double lon) =>
        $"{Math.Round(lat, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)},{Math.Round(lon, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
