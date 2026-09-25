using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;

namespace Nexus.Service.Routes;

/// <summary>
/// The dashboard route to restore when a window reopens. Deliberately process
/// memory and nothing else: the value must survive a window close but not a
/// service start, so localStorage (survives both) and the SPA itself (dies with
/// the window) are both the wrong side of that boundary. A cold start - system
/// boot or a proper service shutdown - therefore lands on home.
///
///   GET  /session/last-route  -> { path, fullscreen }
///   POST /session/last-route  -> { path, fullscreen }
///
/// The sidebar's "recently opened" rows share this lifetime, so they live
/// here too.
///
///   GET  /session/recent-apps -> { keys }
///   POST /session/recent-apps -> { keys }
/// </summary>
internal static class SessionRoutes
{
    // The SPA posts on every route change, so writes are far more frequent
    // than reads and neither side may block the other. Replaced whole, never
    // mutated, so a read never pairs one write's path with another's flag.
    private static LastRouteDto _lastRoute = new();
    private static string[] _recentApps = [];

    /// <summary>Longer than any route the SPA composes; a longer body is a caller bug, not a route.</summary>
    private const int MaxRouteLength = 256;

    /// <summary>The SPA keeps a handful; anything past this is a caller bug, not a list.</summary>
    private const int MaxRecentApps = 16;

    /// <summary>Longer than any sidebar app key ("app:" + a reverse-DNS id).</summary>
    private const int MaxRecentAppKeyLength = 128;

    /// <summary>The stored route, "" when none; the SPA clears it when Remember last page is turned off.</summary>
    internal static string LastRoute => Volatile.Read(ref _lastRoute).Path;

    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/session/last-route", () =>
            Results.Ok(Volatile.Read(ref _lastRoute))).LocalhostOnly();

        app.MapPost("/session/last-route", (LastRouteDto body) =>
        {
            var stored = ToStored(body);
            Volatile.Write(ref _lastRoute, stored);
            return Results.Ok(stored);
        }).LocalhostOnly();

        app.MapGet("/session/recent-apps", () =>
            Results.Ok(new RecentAppsDto { Keys = Volatile.Read(ref _recentApps) })).LocalhostOnly();

        app.MapPost("/session/recent-apps", (RecentAppsDto body) =>
        {
            Volatile.Write(ref _recentApps, SanitizeKeys(body?.Keys));
            return Results.Ok(new RecentAppsDto { Keys = Volatile.Read(ref _recentApps) });
        }).LocalhostOnly();
    }

    /// <summary>The value to store for a posted route; fullscreen means nothing without a page to be fullscreen on.</summary>
    internal static LastRouteDto ToStored(LastRouteDto? body)
    {
        var path = Sanitize(body?.Path);
        return new LastRouteDto { Path = path, Fullscreen = path.Length > 0 && body!.Fullscreen };
    }

    /// <summary>Keeps only an absolute same-origin path, so a stored value can never redirect a reopened window off-origin.</summary>
    internal static string Sanitize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }
        var value = path!.Trim();
        if (value.Length > MaxRouteLength
            || value[0] != '/'
            // "//host" and "/\host" are protocol-relative, not same-origin.
            || (value.Length > 1 && (value[1] == '/' || value[1] == '\\')))
        {
            return "";
        }
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return "";
            }
        }
        return value;
    }

    /// <summary>
    /// Keeps only plausible sidebar app keys, deduped in order, and the newest
    /// (last) ones when over the bound - the SPA sends oldest first. The list
    /// is replayed straight into the SPA's key lookup, which drops anything it
    /// does not know, so this only bounds what a caller can park here.
    /// </summary>
    internal static string[] SanitizeKeys(string[]? keys)
    {
        if (keys is null || keys.Length == 0)
        {
            return [];
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(keys.Length);
        foreach (var raw in keys)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            var key = raw.Trim();
            if (key.Length > MaxRecentAppKeyLength || !seen.Add(key))
            {
                continue;
            }
            var clean = true;
            foreach (var c in key)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    clean = false;
                    break;
                }
            }
            if (clean)
            {
                kept.Add(key);
            }
        }
        return kept.Count > MaxRecentApps
            ? kept.GetRange(kept.Count - MaxRecentApps, MaxRecentApps).ToArray()
            : kept.ToArray();
    }
}

public sealed class LastRouteDto
{
    public string Path { get; init; } = "";

    /// <summary>The page was in the dashboard's fullscreen (focus) mode.</summary>
    public bool Fullscreen { get; init; }
}

public sealed class RecentAppsDto
{
    public string[] Keys { get; set; } = [];
}
