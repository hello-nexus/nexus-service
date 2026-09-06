using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Games;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>
/// Local fps session surface: the local-data tracking switch and purge
/// (mirroring /api/screentime), and read-only game/session summaries served
/// from BinaryFpsSessionStore. Pinned cross-repo contract with nexus-web -
/// field names and shapes must not change on one side alone.
/// </summary>
public static class FpsRoutes
{
    private const int DefaultSessionsLimit = 50;
    private const int MinSessionsLimit = 1;
    private const int MaxSessionsLimit = 200;
    private const int DefaultSessionsOverviewLimit = 200;
    private const int MinSessionsOverviewLimit = 1;
    private const int MaxSessionsOverviewLimit = 500;

    public static void MapFpsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/fps/tracking", (IConfigStore config) =>
            new TrackingStatus { Enabled = config.Load().Fps?.TrackingEnabled ?? true });

        app.MapPost("/api/fps/tracking", (SetTrackingBody body, IConfigStore config) =>
        {
            config.Update(s =>
            {
                s.Fps ??= new FpsSettings();
                s.Fps.TrackingEnabled = body.Enabled;
            });
            return new TrackingStatus { Enabled = body.Enabled };
        });

        app.MapDelete("/api/fps/all", (BinaryFpsSessionStore store, IMetricsHistoryStore history) =>
        {
            var deleted = store.DeleteAll();
            history.BlankFpsSeries();
            return new DeleteResponse { Deleted = deleted };
        });

        app.MapDelete("/api/fps/range", (string from, string to, BinaryFpsSessionStore store) =>
        {
            if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t) || t < f)
            {
                return Results.BadRequest(ApiResponse.Fail("invalid date range"));
            }
            return Results.Ok(new DeleteResponse { Deleted = store.DeleteRange(f, t) });
        });

        app.MapGet("/api/fps/games", (BinaryFpsSessionStore store) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return new FpsGamesResponse { Supported = false };
            }

            var games = store.QueryGameSummaries()
                .Select(ToGameDto)
                .OrderByDescending(g => g.LastPlayedUtcMs)
                .ToList();
            return new FpsGamesResponse { Supported = true, Games = games };
        }).AllowPanel();

        app.MapGet("/api/fps/games/{gameKey}/sessions", (string gameKey, int? limit, BinaryFpsSessionStore store) =>
        {
            var clampedLimit = Math.Clamp(limit ?? DefaultSessionsLimit, MinSessionsLimit, MaxSessionsLimit);
            var sessions = store.QuerySessions(Uri.UnescapeDataString(gameKey), clampedLimit)
                .Select(ToSessionDto)
                .ToList();
            return new FpsSessionsResponse { Sessions = sessions };
        }).AllowPanel();

        // Backs the monitoring page's fps overlay: which game was being
        // played across a time range, cheap over a 7-day window since
        // QuerySessionsByTimeRange scans only the overlapping month segments.
        app.MapGet("/api/fps/sessions", (long? from, long? to, int? limit, BinaryFpsSessionStore store, IServiceProvider sp) =>
        {
            if (from is null || to is null || to < from)
            {
                return Results.BadRequest(ApiResponse.Fail("from and to are required and to must be >= from"));
            }

            var clampedLimit = Math.Clamp(limit ?? DefaultSessionsOverviewLimit, MinSessionsOverviewLimit, MaxSessionsOverviewLimit);
            var sessions = store.QuerySessionsByTimeRange(from.Value, to.Value, clampedLimit)
                .Select(ToSessionOverviewDto)
                .ToList();

            // The run in progress is not on disk yet, and the overlay masks
            // fps points to session ranges, so without it a game being played
            // right now draws nothing. Null off Windows, where no recorder is
            // registered.
            var live = sp.GetService<FpsSessionRecorder>()?.SnapshotOpenSession();
            if (live is not null && live.StartedUtcMs <= to.Value && live.EndedUtcMs >= from.Value)
            {
                // Drops the oldest rather than overrunning the caller's limit.
                if (sessions.Count >= clampedLimit) sessions.RemoveAt(0);
                sessions.Add(ToSessionOverviewDto(live));
            }
            return Results.Ok(new FpsSessionsOverviewResponse { Sessions = sessions });
        }).AllowPanel();

        // iconOnly is the client's retry after a store url failed to load in the
        // browser: the legacy capsule path is a guess, and a 404 there must fall
        // back to the icon rather than to a bare placeholder.
        app.MapGet("/api/fps/games/{gameKey}/art", async (string gameKey, bool? iconOnly, IGameArtResolver art, HttpContext ctx) =>
        {
            var resolved = iconOnly == true
                ? art.ResolveIcon(gameKey)
                : await art.ResolveAsync(gameKey, ctx.RequestAborted);
            // Without this a 302 is not cached at all and every card re-asks on
            // each render; the resolver's own ttl is far longer than an hour.
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            if (resolved.Url.Length > 0) return Results.Redirect(resolved.Url);
            if (resolved.Bytes.Length > 0) return Results.File(resolved.Bytes, "image/png");
            return Results.NotFound();
        }).AllowPanel();

        app.MapDelete("/api/fps/sessions/{id}", (string id, BinaryFpsSessionStore store) =>
        {
            if (!Guid.TryParse(id, out var sessionId))
            {
                return Results.BadRequest(ApiResponse.Fail("id must be a valid guid"));
            }
            return Results.Ok(new DeleteResponse { Deleted = store.DeleteSession(sessionId) });
        });

        app.MapDelete("/api/fps/games/{gameKey}", (string gameKey, BinaryFpsSessionStore store) =>
            new DeleteResponse { Deleted = store.DeleteGame(Uri.UnescapeDataString(gameKey)) });
    }

    internal static FpsGameDto ToGameDto(FpsGameSummary s) => new()
    {
        GameKey = s.GameKey,
        Name = s.GameName,
        Store = s.Store,
        SteamAppId = int.TryParse(s.SteamAppId, out var appId) ? appId : null,
        Sessions = s.Sessions,
        FocusedSec = s.FocusedSec,
        AvgFps = AverageFps(s.Frames, s.ValidSec),
        P1Fps = FpsHistogram.Percentile(s.Hist, 1),
        P99Fps = FpsHistogram.Percentile(s.Hist, 99),
        MinFps = s.MinFps,
        MaxFps = s.MaxFps,
        LastPlayedUtcMs = s.LastPlayedUtcMs,
    };

    internal static FpsSessionDto ToSessionDto(FpsSessionRecord r) => new()
    {
        Id = r.Id.ToString(),
        StartedUtcMs = r.StartedUtcMs,
        EndedUtcMs = r.EndedUtcMs,
        FocusedSec = r.FocusedSec,
        ValidSec = r.ValidSec,
        AvgFps = AverageFps(r.Frames, r.ValidSec),
        P1Fps = FpsHistogram.Percentile(r.Hist, 1),
        P99Fps = FpsHistogram.Percentile(r.Hist, 99),
        MinFps = r.MinFps,
        MaxFps = r.MaxFps,
        DispW = r.DispW,
        DispH = r.DispH,
        RefreshHz = r.RefreshHz,
        Fullscreen = r.Fullscreen,
        Capped = r.Capped,
        CapValue = r.CapValue,
    };

    internal static FpsSessionOverviewDto ToSessionOverviewDto(FpsSessionRecord r) => new()
    {
        Id = r.Id.ToString(),
        GameKey = r.GameKey,
        Name = r.GameName,
        Store = r.Store,
        StartedUtcMs = r.StartedUtcMs,
        EndedUtcMs = r.EndedUtcMs,
        AvgFps = AverageFps(r.Frames, r.ValidSec),
    };

    // avgFps is sum-of-frames / sum-of-validSec, not an average of
    // per-second values (which would over-weight seconds reporting exactly
    // 1 frame).
    internal static double AverageFps(long frames, long validSec) =>
        validSec > 0 ? Math.Round((double)frames / validSec, 1) : 0;
}

public sealed record FpsGameDto
{
    public string GameKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string Store { get; init; } = "";
    public int? SteamAppId { get; init; }
    public int Sessions { get; init; }
    public long FocusedSec { get; init; }
    public double AvgFps { get; init; }
    public int P1Fps { get; init; }
    public int P99Fps { get; init; }
    public int MinFps { get; init; }
    public int MaxFps { get; init; }
    public long LastPlayedUtcMs { get; init; }
}

public sealed record FpsGamesResponse
{
    public bool Supported { get; init; } = true;
    public IReadOnlyList<FpsGameDto> Games { get; init; } = Array.Empty<FpsGameDto>();
}

public sealed record FpsSessionDto
{
    public string Id { get; init; } = "";
    public long StartedUtcMs { get; init; }
    public long EndedUtcMs { get; init; }
    public int FocusedSec { get; init; }
    public int ValidSec { get; init; }
    public double AvgFps { get; init; }
    public int P1Fps { get; init; }
    public int P99Fps { get; init; }
    public int MinFps { get; init; }
    public int MaxFps { get; init; }
    public int DispW { get; init; }
    public int DispH { get; init; }
    public int RefreshHz { get; init; }
    public bool Fullscreen { get; init; }
    public bool Capped { get; init; }
    public int CapValue { get; init; }
}

public sealed record FpsSessionsResponse
{
    public IReadOnlyList<FpsSessionDto> Sessions { get; init; } = Array.Empty<FpsSessionDto>();
}

public sealed record FpsSessionOverviewDto
{
    public string Id { get; init; } = "";
    public string GameKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string Store { get; init; } = "";
    public long StartedUtcMs { get; init; }
    public long EndedUtcMs { get; init; }
    public double AvgFps { get; init; }
}

public sealed record FpsSessionsOverviewResponse
{
    public IReadOnlyList<FpsSessionOverviewDto> Sessions { get; init; } = Array.Empty<FpsSessionOverviewDto>();
}
