using System;
using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Relay;

/// <summary>
/// Path gate for the REST-over-relay tunnel. Only the panel / control REST
/// surface may be tunneled; the high-bandwidth and socket-upgrade endpoints are
/// never relayed (cost guard + the runtime telemetry already has its own
/// <c>/ws</c> relay channel). The policy is allow-by-prefix with an explicit
/// deny set that wins, so a new sub-path under an allowed prefix is reachable
/// without an edit while the denied endpoints stay blocked even though they sit
/// under an allowed prefix (e.g. <c>/lighting/output</c> under <c>/lighting/</c>).
/// </summary>
public static class RelayHttpAllowlist
{
    /// <summary>
    /// Allowed path prefixes - the panel / control REST API. Matched
    /// case-insensitively at a path-segment boundary (exact, or prefix followed
    /// by '/'), so <c>/panel</c> matches <c>/panel/status</c> but not
    /// <c>/panelX</c>.
    /// </summary>
    private static readonly string[] AllowedPrefixes =
    {
        "/panel",
        "/cooling",
        "/devices",
        "/lighting",
        "/peripherals",
        "/keeb",
        "/displays",
        "/profiles",
        "/preferences",
        "/defaults",
        "/conflicts",
        "/benchmark",
        "/system",
        "/overlay",
        "/apps-api",
        "/shortcuts",
        "/media",
        // Gallery READ surface only (/gallery/items, …/{id}/file, …/{id}/thumbnail).
        // Never widen to "/gallery": the trusted-relay dispatch lane bypasses the
        // AllowPanel tier, so a blanket prefix would expose /gallery/pick (opens a
        // dialog on the host) and source mutations to relayed phone sessions.
        "/gallery/items",
        "/y70",
        "/qseries",
        "/api", // /api/steam, /api/obs, /api/discord, /api/weather, /api/media, /api/screentime
        "/ping",
        "/ready",
        "/hardware",
        // WebRTC DataChannel direct P2P signaling: the phone POSTs its offer
        // over this same relay HTTP tunnel to upgrade off the relay.
        "/rtc",
    };

    /// <summary>
    /// Denied exact paths - checked BEFORE the prefix allow. These sit under an
    /// allowed prefix but must never be tunneled:
    ///   • <c>/ws</c> - the multiplex socket upgrade (it has its own relay channel).
    ///   • <c>/lighting/output</c> - the 60fps binary RGB / screen-mirror stream (~1.3 MB/s).
    /// A WebSocket upgrade can't ride a request/response tunnel anyway, but we
    /// reject them by path so the contract is explicit and a 403 is returned.
    /// </summary>
    private static readonly string[] DeniedPaths =
    {
        "/ws",
        "/lighting/output",
        "/system/open-path", // opens arbitrary local files - LAN-only, never relayed
        "/system/pick-path", // opens a native OS dialog on the host - desktop-only, never relayed
        "/devices/firmware/flash", // irreversible flash - brick risk over a lossy tunnel
        "/panel/phone/pair-qr", // mints pair tokens - the desktop dashboard only
        "/system/input/keys", // raw keystroke injection - desktop-token only
        "/system/input/text",
        "/system/audio/play", // plays an arbitrary local file - desktop-token only
    };

    /// <summary>
    /// Denied prefixes - every screen-mirror / capture sub-path is blocked even
    /// though it lives under the allowed <c>/lighting/</c> prefix. The mirror
    /// pulls a continuous high-bandwidth capture the relay must never carry.
    /// </summary>
    private static readonly string[] DeniedPrefixes =
    {
        "/lighting/screen",
    };

    /// <summary>
    /// True when <paramref name="path"/> may be dispatched over the relay tunnel.
    /// Deny wins; then the path must clear an allowed prefix. The HTTP method is
    /// accepted for any of GET/POST/PUT/DELETE/PATCH and is otherwise rejected
    /// (so e.g. CONNECT/TRACE can't be tunneled).
    /// </summary>
    public static bool IsAllowed(string? method, string? path)
    {
        if (!IsAllowedMethod(method))
            return false;
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            return false;

        // Compare only the path, ignoring any query string, and normalize the
        // slash-spelling first: routing matches a trailing slash ("/x/flash/")
        // and repeated slashes to the same handler, so a raw exact-match deny
        // would let a denied path slip through. Collapse repeated slashes and
        // strip a trailing slash so the exact deny and the segment-boundary allow
        // both see the canonical path.
        var q = path.IndexOf('?');
        var p = q < 0 ? path : path[..q];
        p = p.Replace('\\', '/'); // Windows routing/file layer treats '\' as a separator; fold so a denied path can't hide behind it
        while (p.Contains("//"))
            p = p.Replace("//", "/");
        if (p.Length > 1 && p[^1] == '/')
            p = p[..^1];

        foreach (var denied in DeniedPaths)
        {
            if (string.Equals(p, denied, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        foreach (var denied in DeniedPrefixes)
        {
            if (MatchesPrefix(p, denied))
                return false;
        }

        foreach (var allowed in AllowedPrefixes)
        {
            if (MatchesPrefix(p, allowed))
                return true;
        }

        return false;
    }

    private static bool IsAllowedMethod(string? method)
        => string.Equals(method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, HttpMethods.Put, StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, HttpMethods.Delete, StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, HttpMethods.Patch, StringComparison.OrdinalIgnoreCase);

    /// <summary>Exact match or a prefix at a '/' segment boundary (no <c>/panelX</c> for <c>/panel</c>).</summary>
    private static bool MatchesPrefix(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Length == prefix.Length || path[prefix.Length] == '/';
    }
}
