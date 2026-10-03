using System;
using System.Collections.Generic;
using Nexus.Service.Widgets;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Records an installed SDK app sending the user to a site. Only the host and
/// the utm_* parameters leave the box: the rest of the query and the path can
/// carry machine identifiers (a warranty link pre-fills the Windows product key).
/// </summary>
public static class AppLinkTelemetry
{
    private const int MaxValueLength = 100;
    private static readonly string[] UtmKeys = { "utm_source", "utm_medium", "utm_campaign", "utm_content", "utm_term" };

    public static void Capture(ITelemetry telemetry, AppRegistry apps, string? appId, string? url)
    {
        if (!AppIds.IsValid(appId) || !apps.TryGet(appId!, out var entry)) return;
        var props = Properties(appId!, entry.Manifest.Version, url);
        if (props is not null) telemetry.Capture(TelemetryEvents.AppLinkOpened, props);
    }

    internal static (string Key, object? Value)[]? Properties(string appId, string appVersion, string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;

        var props = new List<(string Key, object? Value)> { ("app_id", appId), ("app_version", appVersion), ("host", uri.Host) };
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq];
            if (Array.IndexOf(UtmKeys, key) < 0 || props.Exists(p => p.Key == key)) continue;
            string value;
            try { value = Uri.UnescapeDataString(pair[(eq + 1)..]); }
            catch (UriFormatException) { continue; }
            if (value.Length == 0) continue;
            props.Add((key, value.Length > MaxValueLength ? value[..MaxValueLength] : value));
        }
        return props.ToArray();
    }
}
