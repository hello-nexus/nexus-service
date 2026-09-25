using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Routes;

/// <summary>
/// Widget marketplace endpoints. The runtime is declarative now (the host
/// renders the widget from its manifest's view tree) so there is no
/// per-widget iframe origin and no bundled HTML/JS to serve as a page.
/// What this exposes:
///
/// <list type="bullet">
/// <item><c>GET /apps-api/installed</c> - everything the registry sees.</item>
/// <item><c>GET /apps-api/installed/{id}</c> - one widget, including manifest view tree.</item>
/// <item><c>GET /apps-api/installed/{id}/asset/{**path}</c> - static assets
///   (icons, SVGs, and encrypted <c>.nxpack</c> asset containers with their
///   <c>.key</c> dev sidecar) under the widget bundle. Restricted to image
///   extensions (PNG/JPG/WEBP/GIF/ICO/SVG) plus nxpack/key; no manifest-tree
///   binding enforced.</item>
/// <item><c>GET / PATCH /apps-api/installed/{id}/settings</c> - per-widget user settings.</item>
/// <item><c>GET /apps-api/code/{sessionId}/worker.js</c> + sibling module
///   files - the Tier 2 worker source, served behind a per-spawn session
///   token rather than a Bearer-authed installed-route. Only widgets that
///   declared <c>code: worker</c> can mint a session.</item>
/// </list>
///
/// Install, uninstall, available list, and fetch-proxy endpoints are
/// registered below alongside the asset / settings routes.
/// </summary>
public static class AppRoutes
{
    public const string ApiPrefix = "/apps-api/";

    private static readonly HashSet<string> AllowedAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico",
        ".svg", ".nxpack", ".key",
    };

    // Tier 2 widget worker source files. Restricted to JS modules and JSON
    // sidecar data; CSS/HTML/anything-else isn't useful inside a worker and
    // would widen the exfil surface.
    private static readonly HashSet<string> AllowedCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".json",
    };

    public static void MapAppEndpoints(this WebApplication app)
    {
        app.MapGet("/apps-api/installed", (AppRegistry registry, OemInfo oemInfo, IConfigStore store) =>
        {
            var response = new AppInstalledListingResponse();
            var autoInstalled = store.Load().AutoInstalledApps;
            foreach (var entry in registry.All())
            {
                if (IsOemHidden(entry, oemInfo)) continue;
                response.Apps.Add(BuildListing(entry, oemInfo, autoInstalled));
            }
            return Results.Json(response, AppJsonContext.Default.AppInstalledListingResponse);
        }).AllowPanel();

        app.MapGet("/apps-api/installed/{id}", (string id, AppRegistry registry, OemInfo oemInfo, IConfigStore store) =>
        {
            if (!AppIds.IsValid(id)) return Results.NotFound();
            if (!registry.TryGet(id, out var entry)) return Results.NotFound();
            if (IsOemHidden(entry, oemInfo)) return Results.NotFound();
            return Results.Json(BuildListing(entry, oemInfo, store.Load().AutoInstalledApps), AppJsonContext.Default.AppInstalledListing);
        }).AllowPanel();

        app.MapGet("/apps-api/instance/{instanceId}/settings",
            (string instanceId, WidgetSettingsService settings) =>
        {
            // Instance ids are GUIDs assigned at widget-placement time, so we
            // don't apply the marketplace-id charset whitelist. The service
            // returns an empty doc when the id doesn't resolve to a known
            // placement - the SPA renders that as "no settings".
            return Results.Json(settings.Get(instanceId), AppJsonContext.Default.WidgetSettingsDocument);
        }).AllowPanel();

        app.MapPatch("/apps-api/instance/{instanceId}/settings",
            (string instanceId, WidgetSettingsPatch body, WidgetSettingsService settings) =>
        {
            var updated = settings.Apply(instanceId, body);
            return Results.Json(updated, AppJsonContext.Default.WidgetSettingsDocument);
        }).AllowPanel();

        app.MapGet("/apps-api/installed/{id}/asset/{**path}",
            (string id, string? path, HttpContext ctx, AppRegistry registry) =>
            ServeAsset(id, path, ctx, registry, requireImage: true)).AllowPanel();

        app.MapGet("/apps-api/available", (AppInstaller installer) =>
        {
            return Results.Json(installer.Catalogue(), AppJsonContext.Default.AppCatalogResponse);
        }).AllowPanel();

        app.MapPost("/apps-api/install", (AppInstallRequest body, AppInstaller installer, MultiplexHub hub) =>
        {
            var result = installer.Install(body.Id ?? "");
            if (result.Error is null) PanelTopics.BroadcastAppsChanged(hub);
            return Results.Json(result, AppJsonContext.Default.AppInstallResponse);
        }).AllowPanel();

        // Catalog passthrough. The dashboard is served under connect-src 'self',
        // so it cannot reach the cloud API itself; these hand back its JSON
        // verbatim rather than widening the CSP.
        app.MapGet("/apps-api/store/apps",
            async (Nexus.Service.Store.StoreCatalogProxy proxy, HttpContext http, CancellationToken ct) =>
        {
            var body = await proxy.ListAsync(
                http.Request.Query["nexusVersion"], ParseTouch(http), ct);
            return body is null
                ? Results.Json(ApiResponse.Fail("catalog unavailable"), AppJsonContext.Default.ApiResponse, statusCode: 503)
                : Results.Content(body, "application/json");
        }).AllowPanel();

        app.MapGet("/apps-api/store/apps/{appId}",
            async (string appId, Nexus.Service.Store.StoreCatalogProxy proxy, HttpContext http, CancellationToken ct) =>
        {
            var body = await proxy.DetailAsync(appId, http.Request.Query["nexusVersion"], ParseTouch(http), ct);
            return body is null
                ? Results.Json(ApiResponse.Fail("not found"), AppJsonContext.Default.ApiResponse, statusCode: 404)
                : Results.Content(body, "application/json");
        }).AllowPanel();

        // Media rides through the service because the dashboard's img-src is
        // 'self'; a cross-origin asset host renders as a broken image.
        app.MapGet("/apps-api/store/media/{**path}",
            async (string path, Nexus.Service.Store.StoreCatalogProxy proxy, HttpContext http, CancellationToken ct) =>
        {
            var media = await proxy.MediaAsync(path, ct);
            if (media is null)
            {
                return Results.NotFound();
            }
            http.Response.Headers.XContentTypeOptions = "nosniff";
            if (media.Value.contentType == "image/svg+xml")
            {
                ApplySvgDocumentGuards(http.Response);
            }
            return Results.Bytes(media.Value.bytes, media.Value.contentType);
        }).AllowPanel();

        // Install one version from the asset CDN. Distinct path from the
        // sideload route above: that one activates a bundled app, this one
        // downloads a versioned artifact.
        //
        // Getting an app needs a linked Nexus account, whose grant both
        // authorizes the download and records the purchase Manage purchases
        // lists; its hash supersedes whatever the page sent. The exception is
        // an app that ships with hardware attached to this machine, which
        // installs without an account (see HardwareAppCatalog).
        app.MapPost("/apps-api/store/install",
            async (StoreInstallRequest body, Nexus.Service.Store.StoreEntitlements entitlements,
                   Nexus.Service.Store.StoreInstaller installer,
                   Nexus.Service.Store.HardwareAppCatalog hardware,
                   Nexus.Service.Store.StoreCatalogProxy catalog,
                   IConfigStore store, MultiplexHub hub, HttpContext http, CancellationToken ct) =>
        {
            var appId = body.AppId ?? "";
            var auth = await entitlements.AuthorizeAsync(
                appId, body.Version ?? "", http.Request.Query["nexusVersion"], ct);
            if (!auth.Ok || auth.Grant is null)
            {
                // An app that ships with attached hardware needs no account, so
                // the manual Install button behaves the same as the automatic
                // path. Only a machine with no account at all is waived: a
                // refused token reads sign_in_required too, and the grant is
                // what records the purchase. Without it the caller's hash
                // stands, which still pins the bytes and cannot redirect the
                // download - StoreInstaller composes the URL itself. The catalog
                // read is the automatic path's launch-day gate.
                var hardwareWaiver = auth.Reason == "sign_in_required"
                    && !entitlements.HasLinkedAccount
                    && hardware.IsMatched(appId);
                var waived = hardwareWaiver
                    && await Nexus.Service.Store.StoreRelease.LatestAsync(
                        catalog, appId, http.Request.Query["nexusVersion"].ToString(), ct) is not null;
                if (!waived)
                {
                    return Results.Json(new StoreInstallResponse
                    {
                        AppId = appId,
                        Version = body.Version ?? "",
                        Ok = false,
                        // Signing in cannot fix a waiver the catalog refused.
                        Reason = hardwareWaiver ? "store_unavailable" : auth.Reason ?? "store_unavailable",
                    }, AppJsonContext.Default.StoreInstallResponse);
                }
            }
            else
            {
                body.Sha256 = auth.Grant.Sha256;
                if (auth.Grant.Size > 0) body.Size = auth.Grant.Size;
            }
            var result = await installer.InstallAsync(body, ct);
            if (result.Ok && Nexus.Service.Store.HardwareAppCatalog.IsHardwareApp(appId))
            {
                store.Update(s =>
                {
                    // A deliberate reinstall clears the suppression an uninstall set.
                    s.UserRemovedApps.Remove(appId);
                    // Recorded so the hardware installer leaves this alone: it
                    // would otherwise place the widget and announce an install
                    // the user performed themselves.
                    if (!s.AutoInstalledApps.Contains(appId)) s.AutoInstalledApps.Add(appId);
                });
            }
            if (result.Ok) PanelTopics.BroadcastAppsChanged(hub);
            return Results.Json(result, AppJsonContext.Default.StoreInstallResponse);
        }).AllowPanel();

        // Manage purchases: cloud entitlements joined with what is on disk here.
        app.MapGet("/apps-api/store/library",
            async (Nexus.Service.Store.StoreEntitlements entitlements, CancellationToken ct) =>
        {
            var library = await entitlements.LibraryAsync(ct);
            return Results.Json(library, AppJsonContext.Default.StoreLibraryResponse);
        }).AllowPanel();

        app.MapPost("/apps-api/uninstall", (AppInstallRequest body, AppInstaller installer, IConfigStore store,
                                            AppRegistry registry, PanelDeviceRegistry panels, MultiplexHub hub) =>
        {
            var id = body.Id ?? "";
            var result = installer.Uninstall(id);
            // Removing an app the hardware auto-installer placed is a decision
            // it must not overturn on the next tick. Only those ids are
            // recorded: Uninstall reports success even when no user copy
            // existed, so tracking every id would grow without bound.
            if (result.Error is null && Nexus.Service.Store.HardwareAppCatalog.IsHardwareApp(id))
            {
                store.Update(s =>
                {
                    s.AutoInstalledApps.Remove(id);
                    if (!s.UserRemovedApps.Contains(id)) s.UserRemovedApps.Add(id);
                });
            }
            if (result.Error is null)
            {
                // Uninstall reports success when it removed no user copy, and a
                // bundled copy of the same id stays installed - so the registry,
                // not the response, says whether the placements are now dead.
                if (!registry.TryGet(id, out _))
                {
                    foreach (var deviceId in panels.RemoveWidgetType(WidgetSettingsService.AppTypePrefix + id))
                        PanelTopics.BroadcastPanelDevice(hub, deviceId);
                }
                PanelTopics.BroadcastAppsChanged(hub);
            }
            return Results.Json(result, AppJsonContext.Default.AppInstallResponse);
        }).AllowPanel();

        // Host-action dispatch. Widgets POST { widgetId, action, args };
        // the action is validated against the manifest's capabilities.dispatch
        // allowlist and then routed to the registered handler. Returns
        // the handler's result (action-specific JsonElement shape).
        app.MapPost("/apps-api/dispatch",
            async (AppDispatchRequest body, AppRegistry registry,
                   AppActionRegistry actions, AppDispatchRateLimiter limiter,
                   IServiceProvider services, HttpContext ctx) =>
        {
            if (!AppIds.IsValid(body.AppId))
            {
                return Results.Json(new AppDispatchResponse { Ok = false, Error = "invalid widget id" },
                    AppJsonContext.Default.AppDispatchResponse, statusCode: 400);
            }
            if (!registry.TryGet(body.AppId, out var entry))
            {
                return Results.Json(new AppDispatchResponse { Ok = false, Error = "widget not installed" },
                    AppJsonContext.Default.AppDispatchResponse, statusCode: 404);
            }
            if (!entry.Manifest.Capabilities.Dispatch.Contains(body.Action))
            {
                return Results.Json(new AppDispatchResponse { Ok = false, Error = $"action '{body.Action}' not in manifest allowlist" },
                    AppJsonContext.Default.AppDispatchResponse, statusCode: 403);
            }
            if (!actions.TryGet(body.Action, out var handler))
            {
                return Results.Json(new AppDispatchResponse { Ok = false, Error = $"action '{body.Action}' is not registered" },
                    AppJsonContext.Default.AppDispatchResponse, statusCode: 404);
            }
            // Cap dispatch rate per widget - control actions drive real hardware,
            // so a runaway worker loop must not hammer them.
            if (!limiter.TryAcquire(body.AppId))
            {
                return Results.Json(new AppDispatchResponse { Ok = false, Error = "rate limit exceeded" },
                    AppJsonContext.Default.AppDispatchResponse, statusCode: 429);
            }

            try
            {
                // Inject the caller's app id so self-referential actions (e.g. app.*)
                // can look up their own manifest block without a second argument channel.
                // AppIds.IsValid has already verified the id contains only [a-z0-9.-],
                // so wrapping it in quotes produces valid JSON without escaping.
                var enrichedArgs = new Dictionary<string, System.Text.Json.JsonElement>(
                    body.Args ?? new Dictionary<string, System.Text.Json.JsonElement>(),
                    StringComparer.Ordinal);
                using var appIdDoc = System.Text.Json.JsonDocument.Parse($"\"{body.AppId}\"");
                enrichedArgs["__appId"] = appIdDoc.RootElement.Clone();
                var result = await handler(services, enrichedArgs, ctx.RequestAborted);
                return Results.Json(new AppDispatchResponse { Ok = true, Result = result },
                    AppJsonContext.Default.AppDispatchResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(new AppDispatchResponse { Ok = false, Error = ex.Message },
                    AppJsonContext.Default.AppDispatchResponse, statusCode: 500);
            }
        }).AllowPanel();

        app.MapPost("/apps-api/proxy",
            async (AppProxyRequest body, AppProxyService proxy, HttpContext ctx) =>
        {
            var result = await proxy.ExecuteAsync(body, ctx.RequestAborted);
            return Results.Json(result, AppJsonContext.Default.AppProxyResponse);
        }).AllowPanel();

        // (The legacy GET /apps-api/installed/{id}/worker.js route was
        // removed - Tier 2 workers always boot through a per-spawn code
        // session URL `/apps-api/code/{sessionId}/worker.js`, so the
        // installed-route variant served only to widen the attack surface.)

        // Module-worker code session. The web side POSTs here to mint a
        // short-lived URL-path token; subsequent `import "./lib/x.js"`
        // statements inside the worker inherit the token automatically
        // (relative imports use the worker's base URL). The token is the
        // *only* auth the code-serving GET below requires - the Bearer
        // header can't ride along on module imports.
        app.MapPost("/apps-api/installed/{id}/code-session",
            (string id, HttpContext ctx, AppRegistry registry, AppCodeSessionService sessions) =>
        {
            if (!AppIds.IsValid(id))
            {
                return Results.Json(new AppCodeSessionResponse { Error = "invalid widget id" },
                    AppJsonContext.Default.AppCodeSessionResponse, statusCode: 400);
            }
            if (!registry.TryGet(id, out var entry))
            {
                return Results.Json(new AppCodeSessionResponse { Error = "widget not installed" },
                    AppJsonContext.Default.AppCodeSessionResponse, statusCode: 404);
            }
            if (!entry.Manifest.Capabilities.WorkerCode)
            {
                return Results.Json(new AppCodeSessionResponse { Error = "widget has no worker code capability" },
                    AppJsonContext.Default.AppCodeSessionResponse, statusCode: 400);
            }
            var token = sessions.Create(id);
            return Results.Json(new AppCodeSessionResponse
            {
                SessionId = token,
                BaseUrl = "/apps-api/code/" + token,
                ExpiresInSeconds = (int)AppCodeSessionService.DefaultLifetime.TotalSeconds,
            }, AppJsonContext.Default.AppCodeSessionResponse);
        }).AllowPanel();

        // Code-serving endpoint. NOT .AllowPanel() - authenticates via the
        // session token in the URL path itself, which is why module-worker
        // sibling imports work without needing a Bearer header. Validation
        // gates: session must exist + not be expired, widget must still be
        // installed, path must resolve under the widget root, extension
        // must be in AllowedCodeExtensions. The path-prefix bypass for this
        // route lives in Program.cs auth middleware.
        app.MapGet("/apps-api/code/{sessionId}/{**path}",
            (string sessionId, string? path, HttpContext ctx,
             AppCodeSessionService sessions, AppRegistry registry) =>
            ServeCodeFile(sessionId, path, ctx, sessions, registry));
    }

    private static IResult ServeCodeFile(
        string sessionId,
        string? path,
        HttpContext ctx,
        AppCodeSessionService sessions,
        AppRegistry registry)
    {
        if (string.IsNullOrEmpty(path)) return Results.NotFound();
        var widgetId = sessions.Resolve(sessionId);
        if (widgetId is null) return Results.NotFound();
        if (!registry.TryGet(widgetId, out var entry)) return Results.NotFound();
        // The capability check from session-create is enforced again here so
        // a session for a widget that lost the capability mid-session stops
        // serving.
        if (!entry.Manifest.Capabilities.WorkerCode) return Results.NotFound();

        var resolved = ResolveBundleFile(entry.RootPath, path);
        if (resolved is null) return Results.NotFound();
        var ext = Path.GetExtension(resolved);
        if (!AllowedCodeExtensions.Contains(ext)) return Results.NotFound();

        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";

        var contentType = ext.ToLowerInvariant() switch
        {
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".json"         => "application/json; charset=utf-8",
            _               => "application/octet-stream",
        };
        return Results.Stream(File.OpenRead(resolved), contentType);
    }

    /// <summary>
    /// An SVG is a document: navigated to directly it renders on the service
    /// origin and runs any script it carries, with no CSP because the security
    /// headers only cover HTML. As an <c>&lt;img&gt;</c> source it is inert, and
    /// that is the only way the SPA uses these. Serve it so the image tag still
    /// works while a top-level navigation downloads instead of rendering, and
    /// pin an empty sandbox for any context that does treat it as a document.
    /// </summary>
    private static void ApplySvgDocumentGuards(HttpResponse response)
    {
        response.Headers.ContentDisposition = "attachment";
        response.Headers.ContentSecurityPolicy = "sandbox; script-src 'none'";
    }

    private static IResult ServeAsset(
        string id,
        string? path,
        HttpContext ctx,
        AppRegistry registry,
        bool requireImage)
    {
        if (!AppIds.IsValid(id)) return Results.NotFound();
        if (!registry.TryGet(id, out var entry)) return Results.NotFound();
        if (string.IsNullOrEmpty(path)) return Results.NotFound();

        var resolved = ResolveBundleFile(entry.RootPath, path);
        if (resolved is null) return Results.NotFound();

        if (requireImage)
        {
            var ext = Path.GetExtension(resolved);
            if (!AllowedAssetExtensions.Contains(ext)) return Results.NotFound();
        }

        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        // Encrypted containers may sit in the browser's HTTP cache: the cached
        // bytes are AES-GCM ciphertext (same protection as at rest on disk) and
        // re-fetching tens of MB per widget mount dominates load time. The .key
        // dev sidecar and everything else stays no-store.
        ctx.Response.Headers.CacheControl =
            Path.GetExtension(resolved).Equals(".nxpack", StringComparison.OrdinalIgnoreCase)
                ? "private, max-age=86400"
                : "no-store";

        var contentType = Path.GetExtension(resolved).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".ico" => "image/x-icon",
            ".svg" => "image/svg+xml",
            ".nxpack" or ".key" => "application/octet-stream",
            _ => "application/octet-stream",
        };
        if (contentType == "image/svg+xml")
        {
            ApplySvgDocumentGuards(ctx.Response);
        }
        return Results.Stream(File.OpenRead(resolved), contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Resolve a bundle-relative path to an absolute path on disk. Returns
    /// null if the path escapes the bundle root, points at a symlink, or
    /// doesn't exist. Critical for blocking <c>../</c> traversal smuggled
    /// inside the URL and for refusing reparse-point exfiltration paths
    /// installed by a tampered or malicious bundle.
    /// </summary>
    internal static string? ResolveBundleFile(string root, string requested)
    {
        if (string.IsNullOrEmpty(requested)) return null;
        var rootFull = Path.GetFullPath(root);
        if (Path.IsPathRooted(requested)) return null;
        if (requested.Contains("..", StringComparison.Ordinal)) return null;

        var combined = Path.GetFullPath(Path.Combine(rootFull, requested));
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.Ordinal)) return null;
        if (!File.Exists(combined)) return null;

        try
        {
            var info = new FileInfo(combined);
            if (info.LinkTarget is not null) return null;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        }
        catch (IOException)
        {
            return null;
        }
        return combined;
    }

    private static bool IsBundleRelativeAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        return true;
    }

    // A bundled app whose manifest carries an oem manufacturer gate is an
    // OEM-exclusive bake-in: it stays out of the listing entirely (nav, app
    // picker) on hardware whose SMBIOS manufacturer does not match. A user
    // copy of the same id is a deliberate install and is never hidden.
    private static bool IsOemHidden(AppEntry entry, OemInfo oemInfo)
    {
        if (entry.Source != AppInstallPaths.Source.Bundled) return false;
        return entry.Manifest.Oem?.Manufacturer is { Count: > 0 } manufacturers
            && !oemInfo.Matches(manufacturers);
    }

    private static AppInstalledListing BuildListing(AppEntry entry, OemInfo oemInfo, List<string> autoInstalled)
    {
        // Icons / SVG assets are now served from `/apps-api/installed/{id}/asset/...`,
        // not the (removed) per-widget origin. Building the URL here keeps the
        // dashboard from having to know the route shape.
        var iconUrl = IsBundleRelativeAssetPath(entry.Manifest.Icon)
            ? $"/apps-api/installed/{entry.Id}/asset/{entry.Manifest.Icon}"
            : null;

        var source = entry.Source switch
        {
            AppInstallPaths.Source.User => "user",
            AppInstallPaths.Source.Bundled => "bundled",
            _ => "unknown",
        };

        // No oem block means no gate; an oem block gates preinstall on the
        // detected SMBIOS manufacturer matching one of the listed names.
        var oemMatch = entry.Manifest.Oem?.Manufacturer is not { Count: > 0 } manufacturers
            || oemInfo.Matches(manufacturers);

        return new AppInstalledListing
        {
            Id = entry.Id,
            Name = entry.Manifest.Name,
            Version = entry.Manifest.Version,
            Description = entry.Manifest.Description,
            Category = entry.Manifest.Category,
            IconUrl = iconUrl,
            Surfaces = new List<string>(entry.Manifest.Surfaces),
            Runtime = entry.Manifest.Runtime,
            Page = entry.Manifest.Page,
            Capabilities = entry.Manifest.Capabilities,
            Viewport = entry.Manifest.Viewport,
            Settings = new List<AppManifestSettingEntry>(entry.Manifest.Settings),
            Sizes = new List<string>(entry.Manifest.Sizes),
            DefaultSize = entry.Manifest.DefaultSize,
            Source = source,
            // Preinstall is an OEM bake-in honored only for bundled apps; a user
            // copy of the same id is a deliberate user choice, not a pre-install.
            // A hardware auto-install is also "placed for you, unasked", which
            // is what the dashboard acts on, so it reports the same flag from
            // the user root.
            Preinstalled = (entry.Manifest.Preinstalled && entry.Source == AppInstallPaths.Source.Bundled && oemMatch)
                || autoInstalled.Contains(entry.Id),
            Immersive = entry.Manifest.Immersive,
            SingleInstance = entry.Manifest.SingleInstance,
        };
    }

    /// <summary>Only an explicit false narrows the catalog; absent means unknown.</summary>
    private static bool? ParseTouch(HttpContext http)
    {
        var raw = http.Request.Query["touch"].ToString();
        if (string.IsNullOrEmpty(raw)) return null;
        return raw != "false" && raw != "0";
    }
}
