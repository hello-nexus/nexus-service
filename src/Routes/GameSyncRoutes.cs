using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.GameSync;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class GameSyncRoutes
{
    public static void MapGameSyncEndpoints(this WebApplication app)
    {
        // Activate Game Sync mode and deploy the Chroma shim DLLs into
        // System32/SysWOW64 when not already current. The shim install is
        // idempotent: no-op if the files are already ours at the same version.
        app.MapPost("/lighting/game-sync/start", (ILightingProvider l, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            l.StartGameSync();
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).LocalhostOnly();

        // Per-device frame receiver. The native shim posts one body per device
        // per rendered Chroma frame. Frames are only forwarded when Game Sync
        // is the active mode; otherwise the shim's continuous output is silently
        // dropped (returns 409 so the shim can log and continue).
        app.MapPost("/lighting/game-sync/frame",
            (GameSyncFrameBody body, ILightingProvider l) =>
            {
                var effect = l.ActiveGameSyncEffect();
                if (effect is null)
                {
                    return Results.Json(
                        new ApiResponse { Error = true, Msg = "Game Sync not active" },
                        AppJsonContext.Default.ApiResponse,
                        statusCode: 409);
                }

                effect.IngestFrame(
                    body.Device ?? "",
                    body.Effect ?? "",
                    body.Rows,
                    body.Cols,
                    body.Colors ?? System.Array.Empty<int>(),
                    body.App ?? "");

                return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
            }).LocalhostOnly();

        // Detected games with Chroma/GSI evidence. Returns cached results plus
        // current scan status. refresh=true forces a background scan; otherwise
        // a scan is auto-triggered only when none has run yet or the cache is
        // stale. This endpoint is hit only when the Game Sync surface loads (its
        // sole caller), not on a timer, so the auto-scan never runs with the UI closed.
        app.MapGet("/lighting/game-sync/games",
            (GameSyncGameScanner scanner, HttpContext ctx) =>
            {
                if (ctx.Request.Query.TryGetValue("refresh", out var refreshVal) &&
                    refreshVal.ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    scanner.RequestScan();
                }
                else
                {
                    scanner.RequestScanIfStale();
                }

                return Results.Json(
                    new GameSyncGamesResponse
                    {
                        Scanning = scanner.Scanning,
                        ScannedAt = scanner.ScannedAt,
                        Games = new List<DetectedGame>(scanner.Games),
                    },
                    AppJsonContext.Default.GameSyncGamesResponse);
            }).AllowPanel();

        // Trigger a background scan of installed game stores.
        app.MapPost("/lighting/game-sync/games/scan",
            (GameSyncGameScanner scanner) =>
            {
                scanner.RequestScan();
                return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
            }).AllowPanel();

        // CS2 GSI receiver. CS2 posts live game state here; the mapper converts
        // state to a whole-rig fill color ingested by the active GameSyncEffect.
        app.MapPost("/lighting/game-sync/gsi",
            async (HttpContext ctx, ILightingProvider l) =>
            {
                Cs2GsiPayload? payload;
                try
                {
                    payload = await System.Text.Json.JsonSerializer.DeserializeAsync(
                        ctx.Request.Body,
                        AppJsonContext.Default.Cs2GsiPayload);
                }
                catch
                {
                    return Results.Json(
                        new ApiResponse { Error = true, Msg = "invalid payload" },
                        AppJsonContext.Default.ApiResponse,
                        statusCode: 400);
                }

                if (payload is null)
                {
                    return Results.Json(
                        new ApiResponse { Error = true, Msg = "invalid payload" },
                        AppJsonContext.Default.ApiResponse,
                        statusCode: 400);
                }

                var effect = l.ActiveGameSyncEffect();
                if (effect is null)
                {
                    return Results.Json(
                        new ApiResponse { Error = true, Msg = "Game Sync not active" },
                        AppJsonContext.Default.ApiResponse,
                        statusCode: 409);
                }

                var (r, g, b) = GsiLightingMapper.MapToColor(payload);
                effect.IngestAuthoredFill(r, g, b, "Counter-Strike 2");
                return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
            }).LocalhostOnly();

        // Current Game Sync mode state: whether it is active, whether our
        // Chroma shim DLLs are installed, whether a real Razer SDK conflicts,
        // and which devices the mode will drive.
        app.MapGet("/lighting/game-sync/state",
            (ILightingProvider l, LightingEngine engine, ILightingDeviceProvider deviceProvider) =>
            {
                var active = l.GetSync() == "gamesync";
                var shimState = GameSyncShimInstaller.GetState();
                var deviceList = deviceProvider.GetAll();
                // Build id->name lookup from the canonical device registry.
                var nameById = new System.Collections.Generic.Dictionary<string, string>(
                    deviceList.Devices.Count, System.StringComparer.Ordinal);
                foreach (var d in deviceList.Devices)
                {
                    nameById[d.Id] = d.Name;
                }

                var frames = engine.Devices;
                var infos = new System.Collections.Generic.List<GameSyncDeviceInfo>(frames.Length);
                foreach (var frame in frames)
                {
                    infos.Add(new GameSyncDeviceInfo
                    {
                        Name = nameById.TryGetValue(frame.Id, out var n) ? n : frame.Id,
                        Archetype = frame.Archetype ?? "ambient",
                        LedCount = frame.LedCount,
                    });
                }

                var eff = l.ActiveGameSyncEffect();
                return Results.Json(
                    new GameSyncStateResponse
                    {
                        Active = active,
                        ProviderInstalled = shimState.ProviderInstalled,
                        SynapseConflict = shimState.SynapseConflict,
                        Devices = infos,
                        LastFrameAt = eff?.LastFrameAtMs,
                        ActiveApp = string.IsNullOrEmpty(eff?.ActiveApp) ? null : eff.ActiveApp,
                    },
                    AppJsonContext.Default.GameSyncStateResponse);
            }).AllowPanel();
    }
}
