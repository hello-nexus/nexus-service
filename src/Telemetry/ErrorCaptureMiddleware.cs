using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Nexus.Service.Telemetry;

internal static class ErrorCaptureMiddleware
{
    /// <summary>Reports an exception escaping the pipeline, then rethrows it unchanged, and a 500 a route returned without throwing. Context is the route pattern, never the concrete path or query.</summary>
    public static IApplicationBuilder UseErrorCapture(this IApplicationBuilder app, ErrorReporter reporter) =>
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (Exception ex) when (Capture(reporter, ctx, ex))
            {
                throw;
            }
            // Only 500: several routes answer 502-504 by design while a device or upstream is unavailable.
            if (ctx.Response.StatusCode == StatusCodes.Status500InternalServerError && !ctx.RequestAborted.IsCancellationRequested)
                reporter.ReportStatus(StatusCodes.Status500InternalServerError, RoutePattern(ctx));
        });

    // Always false: observes the exception in the filter without altering unwinding.
    private static bool Capture(ErrorReporter reporter, HttpContext ctx, Exception ex)
    {
        // Marked even when skipped, so the logger provider never reports the same exception.
        try { ex.Data[ErrorKinds.ReportedMarker] = true; } catch { /* read-only Data */ }
        if (ex is OperationCanceledException || ctx.RequestAborted.IsCancellationRequested)
            return false;
        reporter.Report(ex, ErrorKinds.Request, RoutePattern(ctx));
        return false;
    }

    private static string RoutePattern(HttpContext ctx) =>
        (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
}
