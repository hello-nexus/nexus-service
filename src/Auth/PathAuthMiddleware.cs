using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Relay;
using Nexus.Service.Routes;

namespace Nexus.Service.Auth;

// Path-based auth gate. Runs after UseRouting so we can read
// LocalhostOnlyAccess metadata off the matched endpoint, but in front of
// the route handler so a 401/403 short-circuits any state mutation.
//
// Auth order, top to bottom:
//   1. OPTIONS preflight - always passes.
//   2. Public paths (ping/pair/ready/hardware profile + a handful of phone-
//      pairing endpoints whose own handlers enforce per-request validation).
//   3. SPA shell fallback for unmatched top-level GET navigations.
//   4. Localhost-only routes (LocalhostOnlyAccess metadata) - 404 from LAN.
//   5. Static asset extensions (.js/.css/etc.), GET/HEAD only, so images the
//      SPA references by URL load without a token. Real files under wwwroot
//      are already served by UseStaticFiles before this middleware runs, so
//      this only reaches API routes whose last segment carries such a suffix.
//      It sits after the localhost gate on purpose: a suffix must never open a
//      LocalhostOnly route to the LAN, and never a mutation.
//   6. Bearer / query token - desktop session (loopback remote AND a local
//      Host header, so a DNS-rebound browser page cannot use it).
//   7. Phone session cookie / token - paired phone session, gated by the
//      Pair Remote killswitch.
internal static class PathAuthMiddleware
{
    /// <summary>HttpContext.Items key carrying the authenticated phone-session id.</summary>
    public const string PhoneSessionIdItem = "PhoneSessionId";

    private static readonly HashSet<string> AlwaysPublicPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "/ping", "/pair", "/ready", "/hardware/profile",
            // Sealed LAN tunnel: anonymous at the middleware - auth is the in-band
            // sealed handshake (the rid identifies the paired session, the AEAD key
            // proves possession), so the session token never rides the wire here.
            "/secure-tunnel",
            // Short-lived phone-panel pairing claims are validated at the handler level.
            "/panel/phone/claim",
        };

    // Per-method public paths. The phone QR opens the SPA shell without an
    // Authorization header; manual-code submit/confirm are validated by the
    // handler (rate limit, single-use, TTL, SAS binding).
    private static readonly HashSet<string> PublicGetPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Pair Remote killswitch state. Public so paired phones can poll
            // for re-enable while their session is locked out; the body is
            // a single boolean and learning "host has disabled remotes" is
            // exactly the info a locked-out client needs.
            "/panel/phone/remote-control",
            // Cloud-relay opt-in state. Public read for the same reason as the
            // killswitch: a single boolean the panel / relay client may read
            // without a token. Write is desktop-token only.
            "/panel/phone/relay",
            "/panel/phone",
            // Wi-Fi broadcast preference. Public read so the iOS app can
            // tell the user "this PC isn't broadcasting" without already
            // being paired. Write is desktop-token only.
            "/panel/phone/pair-broadcast",
        };

    private static readonly HashSet<string> PublicPostPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "/panel/phone/pair-code/submit",
            "/panel/phone/pair-code/confirm",
            // iOS Wi-Fi discovery → pair initiate. SAS-comparison handshake
            // with no 6-digit code; handler enforces the same rate limit as
            // /pair-code/submit.
            "/panel/phone/pair-wifi/initiate",
        };

    private static readonly string[] StaticAssetExtensions =
        { ".js", ".css", ".svg", ".png", ".ico", ".webmanifest", ".html", ".json", ".woff2" };

    private static bool IsPublicEndpoint(HttpContext ctx, string path)
    {
        if (AlwaysPublicPaths.Contains(path)) return true;
        if (ctx.Request.Method == "GET" && PublicGetPaths.Contains(path)) return true;
        if (ctx.Request.Method == "POST" && PublicPostPaths.Contains(path)) return true;
        // Module-worker code sessions carry a per-spawn 24-byte token in the
        // URL that the route handler validates. Bypassing here lets the
        // browser's ESM loader fetch sibling files inside a worker.
        if (ctx.Request.Method == "GET"
            && path.StartsWith("/apps-api/code/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // RFC 8615 well-known URIs (apple-app-site-association, assetlinks.json,
        // etc.) are public discovery documents by definition. They ship in the
        // SPA bundle's wwwroot and must be fetchable without a token so iOS /
        // Android Universal-Link verification can read them un-authenticated.
        if (ctx.Request.Method == "GET"
            && path.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// True only for a request injected in-process by <see cref="RelayHttpDispatcher"/>:
    /// the trusted-dispatch items key must hold the dispatcher's private sentinel
    /// (reference identity, not just any value) AND a non-empty phone-session id.
    /// Both items live in server-side per-request state a network caller can't
    /// populate, and the sentinel is unreachable outside the relay assembly, so
    /// this can never be satisfied by an external request.
    /// </summary>
    private static bool IsTrustedRelayDispatch(HttpContext ctx, out string sessionId)
    {
        sessionId = string.Empty;
        if (!ctx.Items.TryGetValue(RelayHttpDispatcher.TrustedRelayDispatchKey, out var marker)
            || !ReferenceEquals(marker, RelayHttpDispatcher.TrustedMarker))
        {
            return false;
        }
        if (ctx.Items.TryGetValue("PhoneSessionId", out var raw) && raw is string id && !string.IsNullOrEmpty(id))
        {
            sessionId = id;
            return true;
        }
        return false;
    }

    private static bool IsReadMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    private static bool IsStaticAsset(string path)
    {
        foreach (var ext in StaticAssetExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static IApplicationBuilder UseNexusPathAuth(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (string.Equals(ctx.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await next(ctx);
                return;
            }

            var path = ctx.Request.Path.Value ?? string.Empty;

            if (IsPublicEndpoint(ctx, path))
            {
                await next(ctx);
                return;
            }

            // SPA shell fallback - unmatched top-level browser navigation.
            // GET-only is important: Sec-Fetch-Site: none also fires on POSTs
            // from the address bar (curl with no Origin), but a POST to a
            // state-changing endpoint must never ride the auth-bypass lane.
            if (AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx))
            {
                // The desktop dashboard shell is loopback-only. Off-loopback,
                // serve the SPA shell solely for the phone-panel / pairing
                // surfaces; every other navigation 404s so the dashboard UI is
                // never reachable from the LAN or relay.
                if (!AuthRequestPolicy.IsShellReachable(ctx))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next(ctx);
                return;
            }

            // Localhost-only routes (service control: stop, startup-mode) get
            // 404 from any non-loopback caller before token validation, so the
            // route's existence is never leaked to LAN scanners.
            if (ctx.GetEndpoint()?.Metadata.GetMetadata<LocalhostOnlyAccess>() is not null)
            {
                var remote = ctx.Connection.RemoteIpAddress;
                if (remote is null || !IPAddress.IsLoopback(remote))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }

            if (IsReadMethod(ctx.Request.Method) && IsStaticAsset(path))
            {
                await next(ctx);
                return;
            }

            var panelPairing = ctx.RequestServices.GetRequiredService<Nexus.Service.Panel.PanelPhonePairingService>();

            // Trusted in-process relay dispatch. A REST-over-relay tunnel request
            // (RelayHttpDispatcher) arrives already authenticated end-to-end: only
            // a holder of the session token can derive rid_http + the AEAD key, so
            // it is authorized AS its phone session WITHOUT re-presenting a bearer.
            // The decision rides on HttpContext.Items, which the server creates
            // fresh per request and never fills from headers/body/query, so a
            // network caller can't set it; the value is identity-checked against a
            // private sentinel unreachable outside the relay assembly. The
            // remote-control killswitch still applies (OFF means OFF, even over
            // the relay), and so does the AllowPanel tier: a relayed phone is a
            // phone session and reaches exactly what a LAN phone session reaches.
            // The dispatcher already enforced the path allowlist.
            if (IsTrustedRelayDispatch(ctx, out var relaySessionId))
            {
                if (!panelPairing.GetRemoteControlEnabled())
                {
                    await AuthErrorResponse.WriteAsync(ctx, 403, "RemoteDisabled", "Remote control is currently disabled.");
                    return;
                }
                if (!AuthRequestPolicy.IsPanelSessionAllowed(ctx))
                {
                    await AuthErrorResponse.WriteAsync(ctx, 403, "Forbidden", "This action requires the desktop app.");
                    return;
                }
                ctx.Items["PhoneSessionId"] = relaySessionId;
                await next(ctx);
                return;
            }

            var tokens = ctx.RequestServices.GetRequiredService<TokenService>();
            var requestToken = AuthRequestPolicy.ExtractBearerOrQueryToken(ctx);
            // The desktop token is a loopback-only credential: it is minted only
            // over loopback (/pair) and the desktop app always reaches the service
            // over 127.0.0.1. Honor it only from loopback so a leaked token can't
            // drive the PC from another machine, and only under a local Host
            // header: a browser page whose DNS name was rebound to 127.0.0.1 is a
            // loopback remote too, and the Host header is the one thing it cannot
            // fake. Off-LAN access is the relay + phone-session path (handled
            // above), which never presents this token.
            if (AuthRequestPolicy.IsLoopbackRemote(ctx)
                && AuthRequestPolicy.IsLocalHostHeader(ctx)
                && tokens.Validate(requestToken))
            {
                ctx.RequestServices.GetService<Nexus.Service.Panel.PanelTunnelMonitor>()?.MarkAuthorized(ctx);
                await next(ctx);
                return;
            }

            var cookieToken = ctx.Request.Cookies[Nexus.Service.Panel.PanelPhonePairingService.SessionCookieName];
            var hasPanelSession =
                panelPairing.TryValidateSessionToken(requestToken, ctx, out var sessionId)
                || (!string.Equals(requestToken, cookieToken, StringComparison.Ordinal)
                    && panelPairing.TryValidateSessionToken(cookieToken, ctx, out sessionId));

            if (!hasPanelSession)
            {
                await AuthErrorResponse.WriteAsync(ctx, 401, "Unauthorized", "This panel is not paired with the Nexus service.");
                return;
            }
            ctx.RequestServices.GetService<Nexus.Service.Panel.PanelTunnelMonitor>()?.MarkAuthorized(ctx);

            // Pair Remote killswitch. When OFF, phone-session-authed requests
            // are rejected even though the session is otherwise valid. The
            // matching WS sockets have already been closed by
            // SetRemoteControlEnabledAsync; this guards new HTTP / WS upgrades.
            if (!panelPairing.GetRemoteControlEnabled())
            {
                await AuthErrorResponse.WriteAsync(ctx, 403, "RemoteDisabled", "Remote control is currently disabled.");
                return;
            }
            if (AuthRequestPolicy.RejectsInsecureCsrf(ctx))
            {
                await AuthErrorResponse.WriteAsync(ctx, 403, "CSRF", "Cross-site request blocked.");
                return;
            }
            if (!AuthRequestPolicy.IsPanelSessionAllowed(ctx))
            {
                await AuthErrorResponse.WriteAsync(ctx, 403, "Forbidden", "This action requires the desktop app.");
                return;
            }

            // Tag the request so the /ws upgrade can register the resulting
            // socket with MultiplexHub under this phone-session id - that's
            // how KickPhoneSessionsAsync / KickAllPhoneAsync find the right
            // sockets to close.
            if (!string.IsNullOrEmpty(sessionId))
                ctx.Items[PhoneSessionIdItem] = sessionId;
            await next(ctx);
        });
}
