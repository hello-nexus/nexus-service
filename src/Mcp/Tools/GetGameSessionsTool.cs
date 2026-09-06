using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Games;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Read-only history tool: per-game fps rollups, or one game's
/// recent sessions when 'game' is given.</summary>
public sealed class GetGameSessionsTool : IMcpTool
{
    internal const int DefaultSessionsLimit = 50;
    internal const int MaxSessionsLimit = 200;

    private readonly BinaryFpsSessionStore _store;
    private readonly IConfigStore _config;

    public GetGameSessionsTool(BinaryFpsSessionStore store, IConfigStore config)
    {
        _store = store;
        _config = config;
    }

    public string Name => "get_game_sessions";
    public string Title => "Game Sessions";

    public string Description =>
        "Without 'game': every tracked game's fps rollup (sessions, focused time, frames, min/max/avg " +
        "fps, last played). With 'game' (a gameKey from that rollup): the game's recent sessions " +
        "(started/ended, resolution, refresh rate, fullscreen, capped, min/max/avg fps). supported is " +
        "false on a host with no fps recorder; trackingEnabled reflects the local fps tracking switch.";

    public McpCapability Capability => McpCapability.History;
    public bool ReadOnly => true;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"game\":{\"type\":\"string\",\"description\":\"gameKey from a prior get_game_sessions call without 'game'.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Maximum recent sessions to return when 'game' is given.\"}" +
        "},\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var supported = OperatingSystem.IsWindows();
        var trackingEnabled = _config.Load().Fps?.TrackingEnabled ?? true;
        var game = McpArgs.StringArg(args, "game");

        if (string.IsNullOrEmpty(game))
        {
            var games = supported
                ? _store.QueryGameSummaries().Select(ToSummary).OrderByDescending(g => g.LastPlayedUtcMs).ToList()
                : new List<McpGameSummary>();
            var listResult = new McpGameSessionsResult { Supported = supported, TrackingEnabled = trackingEnabled, Games = games };
            return Task.FromResult(Ok(listResult));
        }

        var summaries = _store.QueryGameSummaries();
        if (!summaries.Any(g => string.Equals(g.GameKey, game, StringComparison.Ordinal)))
        {
            var known = summaries.Select(g => g.GameKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var list = known.Count == 0 ? "(no games recorded)" : string.Join(", ", known);
            return Task.FromResult(McpToolExecutionResult.Error(
                $"Unknown game key '{game}'. Known game keys: {list}. Call get_game_sessions without 'game' to list them with names."));
        }

        var limit = McpArgs.IntArg(args, "limit") is { } requested && requested > 0
            ? Math.Min(requested, MaxSessionsLimit)
            : DefaultSessionsLimit;
        var sessions = _store.QuerySessions(game, limit).Select(ToSessionEntry).ToList();
        var sessionsResult = new McpGameSessionsResult
        {
            Supported = supported,
            TrackingEnabled = trackingEnabled,
            Game = game,
            Sessions = sessions,
        };
        return Task.FromResult(Ok(sessionsResult));
    }

    private static McpToolExecutionResult Ok(McpGameSessionsResult result) =>
        McpToolExecutionResult.Ok(JsonSerializer.Serialize(result, AppJsonContext.Default.McpGameSessionsResult));

    private static McpGameSummary ToSummary(FpsGameSummary s) => new()
    {
        GameKey = s.GameKey,
        Name = s.GameName,
        Store = s.Store,
        Sessions = s.Sessions,
        FocusedSec = s.FocusedSec,
        Frames = s.Frames,
        MinFps = s.MinFps,
        MaxFps = s.MaxFps,
        AvgFps = FpsRoutes.AverageFps(s.Frames, s.ValidSec),
        LastPlayedUtcMs = s.LastPlayedUtcMs,
    };

    private static McpGameSessionEntry ToSessionEntry(FpsSessionRecord r) => new()
    {
        StartedUtcMs = r.StartedUtcMs,
        EndedUtcMs = r.EndedUtcMs,
        AvgFps = FpsRoutes.AverageFps(r.Frames, r.ValidSec),
        MinFps = r.MinFps,
        MaxFps = r.MaxFps,
        DispW = r.DispW,
        DispH = r.DispH,
        RefreshHz = r.RefreshHz,
        Fullscreen = r.Fullscreen,
        Capped = r.Capped,
        CapValue = r.CapValue,
    };
}
