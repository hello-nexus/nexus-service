#if DEV_TOOLS
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Serialization;
using Nexus.Service.Telemetry;
using Nexus.Service.Widgets;

namespace Nexus.Service.Routes;

/// <summary>Dev-tools routes for app-sent analytics and the buffer that shows what was sent.</summary>
public static class AppTelemetryRoutes
{
    public static void MapAppTelemetryEndpoints(this WebApplication app)
    {
        app.MapPost("/apps-api/telemetry/{appId}",
            (string appId, AppTelemetryRequest body, AppRegistry registry, ITelemetry telemetry,
             AppTelemetryRateLimiter limiter) =>
        {
            if (!AppIds.IsValid(appId) || !registry.TryGet(appId, out var entry))
            {
                return Fail("app not installed", 404);
            }
            if (!entry.Manifest.Capabilities.Telemetry)
            {
                return Fail("app does not declare capabilities.telemetry", 403);
            }
            var appProps = AppTelemetryValidator.Validate(body, out var error);
            if (appProps is null)
            {
                return Fail(error ?? "invalid request", 400);
            }
            if (!limiter.TryAcquire(appId))
            {
                return Fail("rate limit exceeded", 429);
            }
            telemetry.Capture(TelemetryEvents.AppEvent,
                AppTelemetryValidator.Compose(appId, entry.Manifest.Version, body, appProps));
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).AllowPanel();

        app.MapPost("/apps-api/page-closed/{appId}",
            (string appId, AppPageClosedRequest body, AppRegistry registry, ITelemetry telemetry) =>
        {
            if (!AppIds.IsValid(appId) || !registry.TryGet(appId, out var entry))
            {
                return Fail("app not installed", 404);
            }
            telemetry.Capture(TelemetryEvents.AppPageClosed,
                AppTelemetryValidator.ComposePageClosed(appId, entry.Manifest.Version, body.DurationMs));
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).AllowPanel();

        app.MapGet("/apps-api/telemetry/recent", (AppEventRecorder recorder) =>
            Results.Bytes(recorder.RecentJson(), "application/json")).AllowPanel();
    }

    private static IResult Fail(string msg, int status) =>
        Results.Json(ApiResponse.Fail(msg), AppJsonContext.Default.ApiResponse, statusCode: status);
}
#endif
