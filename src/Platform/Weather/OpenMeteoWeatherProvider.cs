using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Weather;

namespace Nexus.Service.Platform.Weather;

/// <summary>
/// Cross-platform weather provider backed by Open-Meteo (no API key, free,
/// commercial use allowed). Location defaults to IP geolocation (ipwho.is, no
/// API key, HTTPS, permissive CORS + User-Agent policy) - accurate to the city
/// level, which is enough for a device-panel widget and avoids interactive OS
/// permission prompts inside a kiosk service. Callers may instead supply a
/// manual lat/lon (e.g. from <see cref="SearchLocationsAsync"/>), which skips
/// ipwho.is entirely.
///
/// The weather snapshot cache is keyed per location ("auto" for the IP path,
/// "{lat},{lon}" for manual) since concurrent widgets may track different
/// cities. ipwho.is location caches only the auto path, for 24 h. Weather per
/// key is cached for 15 min. A single request failure returns the last-known
/// good snapshot for that key if it's still reasonably fresh; total failure
/// returns WeatherSnapshot.Empty.
/// </summary>
public sealed class OpenMeteoWeatherProvider : IWeatherProvider
{
    private static readonly TimeSpan WeatherTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LocationTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan StaleServeTtl = TimeSpan.FromHours(2);

    private readonly IHttpClientFactory _http;

    private IpLocation? _cachedLocation;
    private DateTime _locationFetchedUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _locationLock = new(1, 1);

    private readonly Dictionary<string, (WeatherSnapshot Snapshot, DateTime FetchedUtc)> _weatherCache = new();
    private readonly Dictionary<string, Task<WeatherSnapshot?>> _inflight = new();

    // Guards the two dictionaries; never held across an upstream call.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OpenMeteoWeatherProvider(IHttpClientFactory http)
    {
        _http = http;
    }

    public async Task<WeatherSnapshot> GetCurrentAsync(double? lat = null, double? lon = null, string? label = null, string? countryCode = null)
    {
        var manual = lat is not null && lon is not null;
        var key = manual ? $"{lat},{lon}" : "auto";

        try
        {
            var now = DateTime.UtcNow;
            if (await TryGetCachedAsync(key, WeatherTtl, now).ConfigureAwait(false) is { } fresh)
            {
                return fresh;
            }

            IpLocation loc;
            if (manual)
            {
                loc = new IpLocation { Latitude = lat, Longitude = lon, CountryCode = countryCode };
            }
            else
            {
                var auto = await GetLocationAsync().ConfigureAwait(false);
                if (auto is null)
                {
                    return await TryGetCachedAsync(key, StaleServeTtl, now).ConfigureAwait(false) ?? WeatherSnapshot.Empty;
                }
                loc = auto;
            }

            // The rail asks for every saved place at once; one upstream round trip
            // per key runs outside the lock, and callers for the same key share it.
            Task<WeatherSnapshot?> fetch;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_inflight.TryGetValue(key, out fetch!))
                {
                    fetch = FetchWeatherAsync(loc, manual ? label : null);
                    _inflight[key] = fetch;
                }
            }
            finally
            {
                _lock.Release();
            }

            WeatherSnapshot? snapshot;
            try
            {
                snapshot = await fetch.ConfigureAwait(false);
            }
            finally
            {
                await _lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, fetch))
                    {
                        _inflight.Remove(key);
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }

            if (snapshot is not null)
            {
                await _lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    _weatherCache[key] = (snapshot, DateTime.UtcNow);
                }
                finally
                {
                    _lock.Release();
                }
                return snapshot;
            }

            return await TryGetCachedAsync(key, StaleServeTtl, now).ConfigureAwait(false) ?? WeatherSnapshot.Empty;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[weather] failed: {ex.Message}");
            return WeatherSnapshot.Empty;
        }
    }

    private async Task<WeatherSnapshot?> TryGetCachedAsync(string key, TimeSpan ttl, DateTime now)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _weatherCache.TryGetValue(key, out var cached) && (now - cached.FetchedUtc) < ttl ? cached.Snapshot : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IpLocation?> GetLocationAsync()
    {
        await _locationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (_cachedLocation is not null && (now - _locationFetchedUtc) < LocationTtl)
            {
                return _cachedLocation;
            }

            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var resp = await client.GetAsync("https://ipwho.is/").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[weather] ipwho.is returned {(int)resp.StatusCode}");
                return _cachedLocation;
            }

            var loc = await resp.Content.ReadFromJsonAsync(
                Nexus.Service.Serialization.AppJsonContext.Default.IpLocation
            ).ConfigureAwait(false);
            if (loc is null || loc.Latitude is null || loc.Longitude is null)
            {
                Console.Error.WriteLine($"[weather] ipwho.is returned empty lat/lon");
                return _cachedLocation;
            }

            _cachedLocation = loc;
            _locationFetchedUtc = now;
            return loc;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[weather] ipwho.is lookup failed: {ex.Message}");
            return _cachedLocation;
        }
        finally
        {
            _locationLock.Release();
        }
    }

    private async Task<WeatherSnapshot?> FetchWeatherAsync(IpLocation loc, string? labelOverride)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            var url = $"https://api.open-meteo.com/v1/forecast"
                + $"?latitude={loc.Latitude}&longitude={loc.Longitude}"
                + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m,wind_gusts_10m"
                + "&hourly=temperature_2m,weather_code,apparent_temperature,precipitation_probability,precipitation,relative_humidity_2m,dew_point_2m,wind_speed_10m,uv_index,visibility,is_day"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min,sunrise,sunset,uv_index_max,precipitation_sum,precipitation_probability_max,wind_speed_10m_max,wind_direction_10m_dominant"
                + "&temperature_unit=celsius&wind_speed_unit=kmh&forecast_days=10&timezone=auto";
            var forecastTask = client.GetAsync(url);
            var airQualityTask = FetchAirQualityAsync(client, loc);
            var resp = await forecastTask.ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                await airQualityTask.ConfigureAwait(false);
                return null;
            }

            var payload = await resp.Content.ReadFromJsonAsync(
                Nexus.Service.Serialization.AppJsonContext.Default.OpenMeteoResponse
            ).ConfigureAwait(false);
            if (payload?.Current is null)
            {
                await airQualityTask.ConfigureAwait(false);
                return null;
            }

            var c = payload.Current;
            var hourly = BuildHourlyForecast(payload.Hourly);
            var currentHour = FindCurrentHour(payload.Hourly, c.Time);

            return new WeatherSnapshot
            {
                TemperatureC = c.Temperature2m,
                TemperatureF = ToF(c.Temperature2m),
                ApparentTemperatureC = c.ApparentTemperature,
                ApparentTemperatureF = ToF(c.ApparentTemperature),
                WeatherCode = c.WeatherCode ?? -1,
                Condition = ConditionFor(c.WeatherCode),
                IsDay = c.IsDay is null ? null : c.IsDay == 1,
                HumidityPct = c.RelativeHumidity2m,
                DewPointC = currentHour?.DewPointC,
                DewPointF = ToF(currentHour?.DewPointC),
                WindKph = c.WindSpeed10m,
                WindDirectionDeg = c.WindDirection10m,
                WindGustKph = c.WindGusts10m,
                PrecipitationMm = c.Precipitation,
                CloudCoverPct = c.CloudCover,
                PressureHpa = c.PressureMsl,
                UvIndex = currentHour?.UvIndex,
                VisibilityM = currentHour?.VisibilityM,
                AirQuality = await airQualityTask.ConfigureAwait(false),
                LocationLabel = string.IsNullOrEmpty(labelOverride) ? FormatLocation(loc) : labelOverride,
                CountryCode = loc.CountryCode ?? "",
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Timezone = payload.Timezone ?? "",
                UtcOffsetSeconds = payload.UtcOffsetSeconds,
                LocalTime = c.Time ?? "",
                AsOf = DateTime.UtcNow.ToString("o"),
                Hourly = hourly,
                Daily = BuildDailyForecast(payload.Daily),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[weather] open-meteo fetch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Best-effort; a failure here never fails the forecast.</summary>
    private static async Task<WeatherAirQuality?> FetchAirQualityAsync(HttpClient client, IpLocation loc)
    {
        try
        {
            var url = "https://air-quality-api.open-meteo.com/v1/air-quality"
                + $"?latitude={loc.Latitude}&longitude={loc.Longitude}"
                + "&current=european_aqi,us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide&timezone=auto";
            var resp = await client.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            var payload = await resp.Content.ReadFromJsonAsync(
                Nexus.Service.Serialization.AppJsonContext.Default.OpenMeteoAirQualityResponse
            ).ConfigureAwait(false);
            var cur = payload?.Current;
            if (cur is null)
                return null;
            return new WeatherAirQuality
            {
                EuropeanAqi = cur.EuropeanAqi,
                UsAqi = cur.UsAqi,
                Pm25 = cur.Pm25,
                Pm10 = cur.Pm10,
                Ozone = cur.Ozone,
                NitrogenDioxide = cur.NitrogenDioxide,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[weather] air-quality fetch failed: {ex.Message}");
            return null;
        }
    }

    private static double? ToF(double? c) => c is null ? null : c.Value * 9.0 / 5.0 + 32.0;

    internal sealed record CurrentHour(double? DewPointC, double? UvIndex, double? VisibilityM);

    /// <summary>The hourly row whose hour prefix matches the current reading's time ("2026-09-12T19"); null when absent.</summary>
    internal static CurrentHour? FindCurrentHour(OpenMeteoHourly? hourly, string? currentTime)
    {
        if (hourly?.Time is null || string.IsNullOrEmpty(currentTime) || currentTime.Length < 13)
            return null;
        var prefix = currentTime.Substring(0, 13);
        for (var i = 0; i < hourly.Time.Length; i++)
        {
            if (hourly.Time[i] is { } t && t.StartsWith(prefix, StringComparison.Ordinal))
            {
                return new CurrentHour(
                    At(hourly.DewPoint2m, i),
                    At(hourly.UvIndex, i),
                    At(hourly.Visibility, i));
            }
        }
        return null;
    }

    private static double? At(double?[]? values, int i) =>
        values is not null && i < values.Length ? values[i] : null;

    private static int CodeAt(int?[]? values, int i) =>
        values is not null && i < values.Length ? values[i] ?? -1 : -1;

    private static List<WeatherDailyForecast> BuildDailyForecast(OpenMeteoDaily? daily)
    {
        var rows = new List<WeatherDailyForecast>();
        if (daily?.Time is null || daily.Temperature2mMin is null || daily.Temperature2mMax is null)
            return rows;

        var count = Math.Min(10, Math.Min(daily.Time.Length, Math.Min(daily.Temperature2mMin.Length, daily.Temperature2mMax.Length)));
        for (var i = 0; i < count; i++)
        {
            var minC = daily.Temperature2mMin[i];
            var maxC = daily.Temperature2mMax[i];
            var appMin = At(daily.ApparentTemperatureMin, i);
            var appMax = At(daily.ApparentTemperatureMax, i);
            rows.Add(new WeatherDailyForecast
            {
                Date = daily.Time[i] ?? "",
                WeatherCode = CodeAt(daily.WeatherCode, i),
                TemperatureMinC = minC,
                TemperatureMaxC = maxC,
                TemperatureMinF = ToF(minC),
                TemperatureMaxF = ToF(maxC),
                ApparentMinC = appMin,
                ApparentMaxC = appMax,
                ApparentMinF = ToF(appMin),
                ApparentMaxF = ToF(appMax),
                Sunrise = daily.Sunrise is not null && i < daily.Sunrise.Length ? daily.Sunrise[i] ?? "" : "",
                Sunset = daily.Sunset is not null && i < daily.Sunset.Length ? daily.Sunset[i] ?? "" : "",
                UvIndexMax = At(daily.UvIndexMax, i),
                PrecipitationSumMm = At(daily.PrecipitationSum, i),
                PrecipitationProbabilityMaxPct = At(daily.PrecipitationProbabilityMax, i),
                WindMaxKph = At(daily.WindSpeed10mMax, i),
                WindDirectionDeg = At(daily.WindDirection10mDominant, i),
            });
        }

        return rows;
    }

    private static List<WeatherHourlyForecast> BuildHourlyForecast(OpenMeteoHourly? hourly)
    {
        var rows = new List<WeatherHourlyForecast>();
        if (hourly?.Time is null || hourly.Temperature2m is null)
            return rows;

        var count = Math.Min(48, Math.Min(hourly.Time.Length, hourly.Temperature2m.Length));
        for (var i = 0; i < count; i++)
        {
            var tempC = hourly.Temperature2m[i];
            var appC = At(hourly.ApparentTemperature, i);
            var isDay = At(hourly.IsDay, i);
            rows.Add(new WeatherHourlyForecast
            {
                Time = hourly.Time[i] ?? "",
                WeatherCode = CodeAt(hourly.WeatherCode, i),
                TemperatureC = tempC,
                TemperatureF = ToF(tempC),
                ApparentTemperatureC = appC,
                ApparentTemperatureF = ToF(appC),
                PrecipitationProbabilityPct = At(hourly.PrecipitationProbability, i),
                PrecipitationMm = At(hourly.Precipitation, i),
                HumidityPct = At(hourly.RelativeHumidity2m, i),
                WindKph = At(hourly.WindSpeed10m, i),
                UvIndex = At(hourly.UvIndex, i),
                VisibilityM = At(hourly.Visibility, i),
                IsDay = isDay is null ? null : isDay == 1,
            });
        }

        return rows;
    }

    private static string FormatLocation(IpLocation loc)
    {
        var city = loc.City ?? "";
        var region = loc.CountryCode ?? loc.Country ?? "";
        if (!string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(region))
            return $"{city}, {region}";
        if (!string.IsNullOrEmpty(city))
            return city;
        if (!string.IsNullOrEmpty(region))
            return region;
        return "";
    }

    public async Task<List<WeatherGeocodeResult>> SearchLocationsAsync(string query, string? language)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return new List<WeatherGeocodeResult>();
        }

        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var lang = string.IsNullOrEmpty(language) ? "en" : language;
            var url = "https://geocoding-api.open-meteo.com/v1/search"
                + $"?name={Uri.EscapeDataString(query)}&count=10&language={Uri.EscapeDataString(lang)}&format=json";
            var resp = await client.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[weather] geocode search returned {(int)resp.StatusCode}");
                return new List<WeatherGeocodeResult>();
            }

            var payload = await resp.Content.ReadFromJsonAsync(
                Nexus.Service.Serialization.AppJsonContext.Default.OpenMeteoGeocodeResponse
            ).ConfigureAwait(false);
            if (payload?.Results is null)
            {
                return new List<WeatherGeocodeResult>();
            }

            var results = new List<WeatherGeocodeResult>(payload.Results.Length);
            foreach (var r in payload.Results)
            {
                results.Add(new WeatherGeocodeResult
                {
                    Name = r.Name ?? "",
                    Admin1 = r.Admin1 ?? "",
                    Country = r.Country ?? "",
                    CountryCode = r.CountryCode ?? "",
                    Latitude = r.Latitude ?? 0,
                    Longitude = r.Longitude ?? 0,
                });
            }
            return results;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[weather] geocode search failed: {ex.Message}");
            return new List<WeatherGeocodeResult>();
        }
    }

    /// <summary>
    /// WMO weather code -> short human label. Matches the code ranges used on
    /// the widget side to pick an icon. See https://open-meteo.com/en/docs.
    /// </summary>
    public static string ConditionFor(int? code) => code switch
    {
        0 => "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "",
    };
}

public sealed class IpLocation
{
    [JsonPropertyName("latitude")] public double? Latitude { get; set; }
    [JsonPropertyName("longitude")] public double? Longitude { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
}

public sealed class OpenMeteoResponse
{
    [JsonPropertyName("timezone")] public string? Timezone { get; set; }
    [JsonPropertyName("utc_offset_seconds")] public int? UtcOffsetSeconds { get; set; }
    [JsonPropertyName("current")] public OpenMeteoCurrent? Current { get; set; }
    [JsonPropertyName("hourly")] public OpenMeteoHourly? Hourly { get; set; }
    [JsonPropertyName("daily")] public OpenMeteoDaily? Daily { get; set; }
}

public sealed class OpenMeteoCurrent
{
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("temperature_2m")] public double? Temperature2m { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double? RelativeHumidity2m { get; set; }
    [JsonPropertyName("apparent_temperature")] public double? ApparentTemperature { get; set; }
    [JsonPropertyName("is_day")] public int? IsDay { get; set; }
    [JsonPropertyName("precipitation")] public double? Precipitation { get; set; }
    [JsonPropertyName("weather_code")] public int? WeatherCode { get; set; }
    [JsonPropertyName("cloud_cover")] public double? CloudCover { get; set; }
    [JsonPropertyName("pressure_msl")] public double? PressureMsl { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double? WindSpeed10m { get; set; }
    [JsonPropertyName("wind_direction_10m")] public double? WindDirection10m { get; set; }
    [JsonPropertyName("wind_gusts_10m")] public double? WindGusts10m { get; set; }
}

/// <summary>Open-Meteo emits JSON null for a missing hour, so every series is nullable per element.</summary>
public sealed class OpenMeteoHourly
{
    [JsonPropertyName("time")] public string[]? Time { get; set; }
    [JsonPropertyName("temperature_2m")] public double?[]? Temperature2m { get; set; }
    [JsonPropertyName("weather_code")] public int?[]? WeatherCode { get; set; }
    [JsonPropertyName("apparent_temperature")] public double?[]? ApparentTemperature { get; set; }
    [JsonPropertyName("precipitation_probability")] public double?[]? PrecipitationProbability { get; set; }
    [JsonPropertyName("precipitation")] public double?[]? Precipitation { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double?[]? RelativeHumidity2m { get; set; }
    [JsonPropertyName("dew_point_2m")] public double?[]? DewPoint2m { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double?[]? WindSpeed10m { get; set; }
    [JsonPropertyName("uv_index")] public double?[]? UvIndex { get; set; }
    [JsonPropertyName("visibility")] public double?[]? Visibility { get; set; }
    [JsonPropertyName("is_day")] public double?[]? IsDay { get; set; }
}

public sealed class OpenMeteoDaily
{
    [JsonPropertyName("time")] public string[]? Time { get; set; }
    [JsonPropertyName("weather_code")] public int?[]? WeatherCode { get; set; }
    [JsonPropertyName("temperature_2m_min")] public double?[]? Temperature2mMin { get; set; }
    [JsonPropertyName("temperature_2m_max")] public double?[]? Temperature2mMax { get; set; }
    [JsonPropertyName("apparent_temperature_min")] public double?[]? ApparentTemperatureMin { get; set; }
    [JsonPropertyName("apparent_temperature_max")] public double?[]? ApparentTemperatureMax { get; set; }
    [JsonPropertyName("sunrise")] public string[]? Sunrise { get; set; }
    [JsonPropertyName("sunset")] public string[]? Sunset { get; set; }
    [JsonPropertyName("uv_index_max")] public double?[]? UvIndexMax { get; set; }
    [JsonPropertyName("precipitation_sum")] public double?[]? PrecipitationSum { get; set; }
    [JsonPropertyName("precipitation_probability_max")] public double?[]? PrecipitationProbabilityMax { get; set; }
    [JsonPropertyName("wind_speed_10m_max")] public double?[]? WindSpeed10mMax { get; set; }
    [JsonPropertyName("wind_direction_10m_dominant")] public double?[]? WindDirection10mDominant { get; set; }
}

public sealed class OpenMeteoAirQualityResponse
{
    [JsonPropertyName("current")] public OpenMeteoAirQualityCurrent? Current { get; set; }
}

public sealed class OpenMeteoAirQualityCurrent
{
    [JsonPropertyName("european_aqi")] public int? EuropeanAqi { get; set; }
    [JsonPropertyName("us_aqi")] public int? UsAqi { get; set; }
    [JsonPropertyName("pm2_5")] public double? Pm25 { get; set; }
    [JsonPropertyName("pm10")] public double? Pm10 { get; set; }
    [JsonPropertyName("ozone")] public double? Ozone { get; set; }
    [JsonPropertyName("nitrogen_dioxide")] public double? NitrogenDioxide { get; set; }
}

public sealed class OpenMeteoGeocodeResponse
{
    [JsonPropertyName("results")] public OpenMeteoGeocodeResult[]? Results { get; set; }
}

public sealed class OpenMeteoGeocodeResult
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("admin1")] public string? Admin1 { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
    [JsonPropertyName("latitude")] public double? Latitude { get; set; }
    [JsonPropertyName("longitude")] public double? Longitude { get; set; }
}
