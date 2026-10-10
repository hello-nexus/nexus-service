using System;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Activity;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

public static class ActivityRoutes
{
    /// <summary>~11.5 days. Longer than any real track, and low enough that the
    /// tick (x10_000) and microsecond (x1_000) conversions downstream stay well
    /// inside Int64.</summary>
    private const long MaxSeekPositionMs = 1_000_000_000L;

    public static void MapActivityEndpoints(this WebApplication app)
    {
        // Screen time - persistent history browsing
        app.MapGet("/api/screentime/day/{date}", (string date, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(date, out var d))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(store.GetDay(d));
        });

        app.MapGet("/api/screentime/range", (string from, string to, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(new List<DayTotal>(store.GetRange(f, t)));
        });

        app.MapGet("/api/screentime/app/{name}", (string name, string from, string to, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(store.GetAppHistory(Uri.UnescapeDataString(name), f, t));
        });

        app.MapGet("/api/screentime/day/{date}/hour/{hour:int}", (string date, int hour, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(date, out var d))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(new List<AppUsage>(store.GetHourUsage(d, hour)));
        });

        app.MapDelete("/api/screentime/day/{date}", (string date, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(date, out var d))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(new DeleteResponse { Deleted = store.DeleteDay(d) });
        });

        app.MapDelete("/api/screentime/range", (string from, string to, IScreenTimeStore store) =>
        {
            if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date"));
            }
            return Results.Ok(new DeleteResponse { Deleted = store.DeleteRange(f, t) });
        });

        app.MapDelete("/api/screentime/app/{name}", (string name, IScreenTimeStore store) =>
            new DeleteResponse { Deleted = store.DeleteApp(Uri.UnescapeDataString(name)) });

        app.MapDelete("/api/screentime/all", (IScreenTimeStore store) =>
            new DeleteResponse { Deleted = store.DeleteAll() });

        app.MapGet("/api/screentime/tracking", (IConfigStore config) =>
            new TrackingStatus { Enabled = config.Load().ScreenTime?.TrackingEnabled ?? true });

        app.MapPost("/api/screentime/tracking", (SetTrackingBody body, IConfigStore config) =>
        {
            config.Update(s =>
            {
                s.ScreenTime ??= new ScreenTimeSettings();
                s.ScreenTime.TrackingEnabled = body.Enabled;
            });
            return new TrackingStatus { Enabled = body.Enabled };
        });

        // Media
        app.MapGet("/api/media", (IMediaProvider m) => Results.Ok(m.GetSessions())).AllowPanel();
        app.MapPost("/api/media/{source}/control", (string source, MediaControlBody body, IMediaProvider m) =>
        {
            m.Control(source, body.Action);
            return ApiResponse.Ok();
        }).AllowPanel();
        // Absolute seek. Providers no-op when the underlying player does not
        // support it; the SPA gates the control on session.controls.isSeekEnabled.
        app.MapPost("/api/media/{source}/seek", IResult (string source, MediaSeekBody body, IMediaProvider m) =>
        {
            // Upper bound as well as lower: downstream multiplies by 10_000
            // (SMTC ticks) and 1_000 (MPRIS microseconds), which overflow into a
            // negative position past ~9.2e14.
            if (body.PositionMs < 0 || body.PositionMs > MaxSeekPositionMs)
            {
                return Results.BadRequest(ApiResponse.Fail($"positionMs must be between 0 and {MaxSeekPositionMs}"));
            }
            m.Seek(source, body.PositionMs);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();
        app.MapGet("/api/media/{source}/album-art", (string source, IMediaProvider m) =>
        {
            var bytes = m.GetAlbumArt(source);
            return bytes.Length == 0 ? Results.BadRequest() : Results.File(bytes, "image/png");
        }).AllowPanel();
        // Optional upgrade over /album-art: catalog-resolved high-res cover.
        // 404 on any miss; callers keep the standard thumbnail.
        app.MapGet("/api/media/{source}/album-art-hd", async (string source, IMediaProvider m, IAlbumArtHdResolver hd) =>
        {
            if (!m.GetSessions().TryGetValue(source, out var session))
            {
                return Results.NotFound();
            }
            var bytes = await hd.GetHdAlbumArtAsync(session.Song);
            return bytes.Length == 0 ? Results.NotFound() : Results.File(bytes, "image/jpeg");
        }).AllowPanel();

        // Shortcuts
        app.MapGet("/shortcuts", (string? targetId, IShortcutsProvider s) =>
        {
            if (!string.IsNullOrEmpty(targetId))
            {
                var sc = s.GetById(targetId);
                return sc is null
                    ? Results.NotFound(new GetShortcutResponse { Error = true, Msg = "Shortcut not found" })
                    : Results.Ok(new GetShortcutResponse { Shortcut = sc });
            }
            return Results.Ok(new GetAllShortcutsResponse { Shortcuts = new(s.GetAll()) });
        }).AllowPanel();
        app.MapGet("/shortcuts/icon", (string? targetId, HttpContext ctx, IShortcutsProvider s) =>
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return Results.BadRequest();
            }

            var bytes = s.GetIcon(targetId);
            if (bytes is null)
            {
                // The panel settles on a 404 and retries a 503.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            if (bytes.Length == 0)
            {
                return Results.NotFound();
            }

            // Content hash as the ETag: Results.File's entityTag param drives the
            // framework's own conditional-GET handling, so a matching
            // If-None-Match short-circuits to a bodyless 304 - the deck page and
            // WebView2 stop re-downloading icons that have not changed.
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var etag = new EntityTagHeaderValue($"\"{hash}\"");
            ctx.Response.Headers.CacheControl = "private, max-age=3600, must-revalidate";
            return Results.File(bytes, "image/png", entityTag: etag);
        }).AllowPanel();
        app.MapPost("/shortcuts/launch", (string? targetId, IShortcutsProvider s) =>
            s.Launch(targetId ?? "") ? ApiResponse.Ok() : ApiResponse.Fail("Not found")).AllowPanel();

    }
}
