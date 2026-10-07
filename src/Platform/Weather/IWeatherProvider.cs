using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Models.Weather;

namespace Nexus.Service.Platform.Weather;

/// <summary>
/// Returns a current weather snapshot. Never throws - on failure returns an
/// empty snapshot whose <see cref="WeatherSnapshot.Unavailable"/> names why.
/// </summary>
public interface IWeatherProvider
{
    /// <summary>
    /// When lat and lon are both supplied, fetches that location directly and
    /// skips IP geolocation; label and countryCode then populate the snapshot
    /// verbatim instead of the IP-derived location fields.
    /// </summary>
    Task<WeatherSnapshot> GetCurrentAsync(double? lat = null, double? lon = null, string? label = null, string? countryCode = null);

    Task<List<WeatherGeocodeResult>> SearchLocationsAsync(string query, string? language);
}
