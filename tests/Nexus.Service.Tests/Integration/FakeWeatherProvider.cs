using Nexus.Service.Models.Weather;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Weather;

namespace Nexus.Service.Tests.Integration;

/// <summary>Network-free weather provider for hosts under test. <see cref="Approximate"/> is what the IP lookup would find.</summary>
public sealed class FakeWeatherProvider : IWeatherProvider
{
    public WeatherSavedLocation? Approximate { get; set; }

    public Task<WeatherSnapshot> GetCurrentAsync(double? lat = null, double? lon = null, string? label = null, string? countryCode = null) =>
        Task.FromResult(WeatherSnapshot.Empty);

    public Task<WeatherSavedLocation?> GetApproximateLocationAsync() => Task.FromResult(Approximate);

    public Task<List<WeatherGeocodeResult>> SearchLocationsAsync(string query, string? language) =>
        Task.FromResult(new List<WeatherGeocodeResult>());
}
