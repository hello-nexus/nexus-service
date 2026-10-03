using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using Nexus.Service.Widgets;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Records an installed SDK app sending the user to a site. Only the host and
/// the utm_* parameters leave the box: the rest of the query and the path can
/// carry machine identifiers (a warranty link pre-fills the Windows product key).
/// </summary>
public static partial class AppLinkTelemetry
{
    private static readonly string[] UtmKeys = { "utm_source", "utm_medium", "utm_campaign", "utm_content", "utm_term" };

    private static readonly AppTelemetryRateLimiter DefaultLimiter = new(30);

    public static void Capture(ITelemetry telemetry, AppRegistry apps, string? appId, string? url, AppTelemetryRateLimiter? limiter = null)
    {
        if (!AppIds.IsValid(appId) || !apps.TryGet(appId!, out var entry)) return;
        if (!(limiter ?? DefaultLimiter).TryAcquire(appId!)) return;
        var props = Properties(appId!, entry.Manifest.Version, url);
        if (props is not null) telemetry.Capture(TelemetryEvents.AppLinkOpened, props);
    }

    internal static (string Key, object? Value)[]? Properties(string appId, string appVersion, string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;

        var props = new List<(string Key, object? Value)> { ("app_id", appId), ("app_version", appVersion), ("host", HostLabel(uri.Host)) };
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq];
            if (Array.IndexOf(UtmKeys, key) < 0 || props.Exists(p => p.Key == key)) continue;
            string value;
            try { value = Uri.UnescapeDataString(pair[(eq + 1)..]); }
            catch (UriFormatException) { continue; }
            if (!UtmValue().IsMatch(value)) continue;
            props.Add((key, value));
        }
        return props.ToArray();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_.:-]{0,63}$")]
    private static partial Regex UtmValue();

    private static readonly string[] PrivateSuffixes = { ".local", ".lan", ".home", ".internal", ".localdomain" };

    /// <summary>A LAN name or address says something about the user's network, so only public-looking hosts are sent.</summary>
    internal static string HostLabel(string host)
    {
        var h = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(h, out _) || !h.Contains('.')) return "private";
        foreach (var suffix in PrivateSuffixes)
        {
            if (h.EndsWith(suffix, StringComparison.Ordinal)) return "private";
        }
        return h;
    }
}
