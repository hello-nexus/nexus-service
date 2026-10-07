using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Nexus.Service.Telemetry;

internal static class ErrorCaptureMiddleware
{
    /// <summary>Reports an exception escaping the pipeline, then rethrows it unchanged. Context is the route pattern, never the concrete path or query.</summary>
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
        });

    // Always false: observes the exception in the filter without altering unwinding.
    private static bool Capture(ErrorReporter reporter, HttpContext ctx, Exception ex)
    {
        // Marked even when skipped, so the logger provider never reports the same exception.
        try { ex.Data[ErrorKinds.ReportedMarker] = true; } catch { /* read-only Data */ }
        if (ex is OperationCanceledException || ctx.RequestAborted.IsCancellationRequested)
            return false;
        var pattern = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        reporter.Report(ex, ErrorKinds.Request, pattern);
        return false;
    }
}
