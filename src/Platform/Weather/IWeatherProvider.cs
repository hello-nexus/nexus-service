using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Models.Weather;

namespace Nexus.Service.Platform.Weather;

/// <summary>
/// Returns a current weather snapshot. Never throws - returns
/// <see cref="WeatherSnapshot.Empty"/> on any failure so callers can render a
/// "no data" state without a try/catch.
/// </summary>
public interface IWeatherProvider
{
    /// <summary>
    /// When lat and lon are both supplied, fetches that location directly and
    /// skips IP geolocation; label and countryCode then populate the snapshot
    /// verbatim instead of the IP-derived location fields.
    /// </summary>
    Task<WeatherSnapshot> GetCurrentAsync(double? lat = null, double? lon = null, string? label = null, string? countryCode = null);

    /// <summary>The IP-derived location (cached), or null while it cannot be resolved.</summary>
    Task<Nexus.Service.Persistence.WeatherSavedLocation?> GetApproximateLocationAsync();

    Task<List<WeatherGeocodeResult>> SearchLocationsAsync(string query, string? language);
}
