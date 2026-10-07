using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Models.Update;
using Nexus.Service.Serialization;
using Nexus.Service.Update;

namespace Nexus.Service.Routes;

/// <summary>
/// OTA self-update surface. All routes are localhost-only and require a dashboard
/// token. No .AllowPanel() - panel-session tokens cannot reach these.
/// </summary>
public static class UpdateRoutes
{
    public static void MapUpdateEndpoints(this WebApplication app)
    {
        // Current update availability and state.
        app.MapGet("/update/status", (UpdateService svc) =>
        {
            var status = svc.Status;
#if DEV_TOOLS
            status = Nexus.Service.Dev.DevSimEvents.Current?.ApplyUpdate(status) ?? status;
#endif
            return Results.Json(status, AppJsonContext.Default.UpdateStatusResponse);
        }).LocalhostOnly();

        // Trigger an immediate check. Always returns 200 (network failures are
        // surfaced in lastCheckError).
        app.MapPost("/update/check", async (UpdateService svc, CancellationToken ct) =>
        {
            var status = await svc.CheckNowAsync(ct);
            return Results.Json(status, AppJsonContext.Default.UpdateStatusResponse);
        }).LocalhostOnly();

        // Start a download+verify+install sequence.
        app.MapPost("/update/start", (UpdateStartRequest? body, UpdateService svc) =>
        {
            var (started, reason) = svc.StartUpdate(body?.Version, body?.ReopenAfter ?? false);
            if (started)
            {
                return Results.Json(
                    new UpdateStartResponse { Started = true },
                    AppJsonContext.Default.UpdateStartResponse);
            }

            Console.Error.WriteLine($"[update] start rejected: {reason}");
            return Results.Json(
                new UpdateStartResponse { Error = true, Started = false, Msg = reason },
                AppJsonContext.Default.UpdateStartResponse,
                statusCode: StatusCodes.Status409Conflict);
        }).LocalhostOnly();

        // Progress polling during an active install.
        app.MapGet("/update/progress", (UpdateService svc) =>
            Results.Json(svc.Progress, AppJsonContext.Default.UpdateProgressResponse))
            .LocalhostOnly();
    }
}
