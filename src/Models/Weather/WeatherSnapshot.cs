using System.Collections.Generic;

namespace Nexus.Service.Models.Weather;

/// <summary>
/// Current weather payload for the panel weather widget.
/// Always returned (even on failure) - all fields nullable / empty-safe so the
/// widget can render a "no data" state without special-casing the response.
/// </summary>
public sealed class WeatherSnapshot
{
    public double? TemperatureC { get; set; }
    public double? TemperatureF { get; set; }
    public double? ApparentTemperatureC { get; set; }
    public double? ApparentTemperatureF { get; set; }
    /// <summary>Open-Meteo WMO weather code (0-99). 0 clear, 1-3 cloudy,
    /// 45/48 fog, 51+ drizzle/rain, 71+ snow, 95+ thunder. -1 when unknown.</summary>
    public int WeatherCode { get; set; } = -1;
    /// <summary>Short human-readable condition label ("Clear", "Cloudy", ...).</summary>
    public string Condition { get; set; } = "";
    public bool? IsDay { get; set; }
    public double? HumidityPct { get; set; }
    public double? DewPointC { get; set; }
    public double? DewPointF { get; set; }
    public double? WindKph { get; set; }
    /// <summary>Meteorological direction the wind blows FROM, degrees clockwise from north.</summary>
    public double? WindDirectionDeg { get; set; }
    public double? WindGustKph { get; set; }
    public double? PrecipitationMm { get; set; }
    public double? CloudCoverPct { get; set; }
    /// <summary>Sea-level pressure, hPa.</summary>
    public double? PressureHpa { get; set; }
    /// <summary>Current-hour UV index (Open-Meteo has no instantaneous value).</summary>
    public double? UvIndex { get; set; }
    /// <summary>Current-hour visibility, metres.</summary>
    public double? VisibilityM { get; set; }
    public WeatherAirQuality? AirQuality { get; set; }
    /// <summary>Best-effort location label ("San Francisco, US"). May be empty
    /// on stub or when geolocation fails.</summary>
    public string LocationLabel { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2 country code from IP geolocation. Used by
    /// the widget to auto-pick C vs F. Empty when unknown.</summary>
    public string CountryCode { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>IANA zone of the location ("Europe/Rome"); empty when unknown.</summary>
    public string Timezone { get; set; } = "";
    public int? UtcOffsetSeconds { get; set; }
    /// <summary>Provider's local wall-clock time of the current reading ("2026-09-12T19:30"), no offset suffix.</summary>
    public string LocalTime { get; set; } = "";
    /// <summary>ISO-8601 UTC timestamp of when this snapshot was produced. Empty
    /// string means we have no data at all (stub or complete failure).</summary>
    public string AsOf { get; set; } = "";
    /// <summary>Hourly forecast rows in local weather-provider times.</summary>
    public List<WeatherHourlyForecast> Hourly { get; set; } = new();
    /// <summary>Daily forecast rows in local weather-provider dates.</summary>
    public List<WeatherDailyForecast> Daily { get; set; } = new();

    public static WeatherSnapshot Empty => new();
}

public sealed class WeatherHourlyForecast
{
    public string Time { get; set; } = "";
    public int WeatherCode { get; set; } = -1;
    public double? TemperatureC { get; set; }
    public double? TemperatureF { get; set; }
    public double? ApparentTemperatureC { get; set; }
    public double? ApparentTemperatureF { get; set; }
    public double? PrecipitationProbabilityPct { get; set; }
    public double? PrecipitationMm { get; set; }
    public double? HumidityPct { get; set; }
    public double? WindKph { get; set; }
    public double? UvIndex { get; set; }
    public double? VisibilityM { get; set; }
    public bool? IsDay { get; set; }
}

public sealed class WeatherDailyForecast
{
    public string Date { get; set; } = "";
    public int WeatherCode { get; set; } = -1;
    public double? TemperatureMinC { get; set; }
    public double? TemperatureMaxC { get; set; }
    public double? TemperatureMinF { get; set; }
    public double? TemperatureMaxF { get; set; }
    public double? ApparentMinC { get; set; }
    public double? ApparentMaxC { get; set; }
    public double? ApparentMinF { get; set; }
    public double? ApparentMaxF { get; set; }
    /// <summary>Local wall-clock times ("2026-09-12T06:39"), no offset suffix.</summary>
    public string Sunrise { get; set; } = "";
    public string Sunset { get; set; } = "";
    public double? UvIndexMax { get; set; }
    public double? PrecipitationSumMm { get; set; }
    public double? PrecipitationProbabilityMaxPct { get; set; }
    public double? WindMaxKph { get; set; }
    public double? WindDirectionDeg { get; set; }
}

/// <summary>Open-Meteo air-quality reading for the current hour. Null on the snapshot when the AQ fetch failed.</summary>
public sealed class WeatherAirQuality
{
    public int? EuropeanAqi { get; set; }
    public int? UsAqi { get; set; }
    public double? Pm25 { get; set; }
    public double? Pm10 { get; set; }
    public double? Ozone { get; set; }
    public double? NitrogenDioxide { get; set; }
}

/// <summary>A single geocoding search match for manual location entry.</summary>
public sealed class WeatherGeocodeResult
{
    public string Name { get; set; } = "";
    public string Admin1 { get; set; } = "";
    public string Country { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed class WeatherGeocodeResponse
{
    public List<WeatherGeocodeResult> Results { get; set; } = new();
}

/// <summary>GET/PUT /api/weather/locations body. Every field of the PUT is optional; absent means "leave as is".</summary>
public sealed class WeatherPrefsDto
{
    public string? Unit { get; set; }
    public List<WeatherSavedLocationDto>? Locations { get; set; }
}

public sealed class WeatherSavedLocationDto
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Label { get; set; } = "";
    public string Cc { get; set; } = "";
}
