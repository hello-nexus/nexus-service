using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nexus.Service.Models;
using Nexus.Service.Serialization;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Routes;

/// <summary>
/// Telemetry consent surface (dashboard-only, loopback). The Settings → General
/// toggle reads and flips the single anonymous-data opt-out that gates BOTH the
/// fleet heartbeat and product events. Dashboard-only by design - a paired phone
/// shouldn't be able to turn the whole install's telemetry on/off, so these are
/// <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/> (no .AllowPanel()).
///   GET  /telemetry/consent  -> { enabled }
///   POST /telemetry/consent  -> set + return { enabled }
/// A true/false flip fires opt_in/opt_out; posting the same value as the fresh-install default is not a transition.
/// </summary>
internal static class TelemetryRoutes
{
    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        app.MapGet("/telemetry/consent", (IConfigStore store) =>
            Results.Ok(new TelemetryConsentDto { Enabled = store.Load().Telemetry.CollectAnonymousData }))
            .LocalhostOnly();

        app.MapPost("/telemetry/consent", (TelemetryConsentBody body, IConfigStore store, FleetEventService fleet) =>
        {
            var was = store.Load().Telemetry.CollectAnonymousData;
            var now = body.Enabled;
            var transitionType = was == now ? null : (now ? TelemetryEvents.OptIn : TelemetryEvents.OptOut);

            if (transitionType is not null)
            {
                // Crash-safe: the marker hits disk before the flag flips, so a crash mid-transition still has it to recover.
                store.Update(s =>
                {
                    s.Telemetry.FleetPendingConsentEvent = transitionType;
                    s.Telemetry.FleetPendingConsentSince = DateTimeOffset.UtcNow.ToString("o");
                });
                store.FlushNow();
            }

            store.Update(s => s.Telemetry.CollectAnonymousData = now);

            if (transitionType is not null)
            {
                // Flush the flip too, or a crash before the debounce window reverts the on-disk flag while the marker still claims it changed.
                store.FlushNow();
                // Fire-and-forget: the persisted marker guarantees a retry if this attempt is lost.
                _ = DeliverInBackground(fleet, transitionType);
            }

            return Results.Ok(new TelemetryConsentDto
            {
                Enabled = store.Load().Telemetry.CollectAnonymousData,
            });
        }).LocalhostOnly();

        // Dashboard-only: the SPA reports which page is open; AppUsageTracker dedupes to once per page per UTC day and consent gates the capture.
        app.MapPost("/telemetry/page-viewed/{page}", (string page, AppUsageTracker usage) =>
        {
            if (!PageSlug.IsMatch(page))
                return Results.Json(ApiResponse.Fail("invalid page"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            usage.NotePage(page);
            return Results.NoContent();
        }).LocalhostOnly();

        // nexus-web (dashboard and paired panels/phones) relays browser errors here; the same opt-out, scrubbing and caps as service errors apply.
        app.MapPost("/telemetry/client-errors", async (HttpContext ctx, ErrorReporter reporter) =>
        {
            if (ctx.Request.ContentLength is > MaxClientErrorsBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var bodySize = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
                bodySize.MaxRequestBodySize = MaxClientErrorsBytes;
            ClientErrorsBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AppJsonContext.Default.ClientErrorsBody, ctx.RequestAborted);
            }
            catch (BadHttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            catch (JsonException)
            {
                return Results.BadRequest(ApiResponse.Fail("body is malformed"));
            }
            if (body?.Errors is null or { Count: 0 or > MaxClientErrors })
                return Results.BadRequest(ApiResponse.Fail("1 to 10 errors required"));
            foreach (var e in body.Errors)
            {
                if (e is null)
                    continue;
                if (e.Kind is not ("window-error" or "unhandled-rejection" or "render" or "app" or "app-log") || string.IsNullOrEmpty(e.Fingerprint))
                    continue;
                // Local support log line, written whether or not anonymous data is on; only the upload below honours the opt-out.
                if (e.Kind is "app" or "app-log")
                    LogAppError(e.App, e.Message);
                // app-log is free-form author text from nexus.log: local log only, never uploaded.
                if (e.Kind == "app-log")
                    continue;
                reporter.ReportClient(e.Kind, e.Fingerprint, e.Type ?? "", e.Message ?? "", e.Stack ?? "", e.Context, Math.Clamp(e.Count, 1, ErrorReporter.MaxCount));
            }
            return Results.NoContent();
        }).AllowPanel();
    }

    private static readonly System.Text.RegularExpressions.Regex PageSlug = new(
        "^[a-z0-9][a-z0-9-]{0,39}\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private const int MaxAppErrorLogsPerMinute = 30;
    private const int MaxAppErrorTagLength = 160;
    private const int MaxAppErrorMessageLength = 500;
    private static readonly AppErrorLogLimiter AppErrorLimiter = new(MaxAppErrorLogsPerMinute);

    /// <summary>One WRN line per SDK app error, so a support bundle shows which app broke; capped per minute against a chatty client.</summary>
    internal static bool LogAppError(string? app, string? message)
    {
        if (!AppErrorLimiter.TryAcquire(Environment.TickCount64))
            return false;
        ServiceLog.Warn(FormatAppError(app, message));
        return true;
    }

    internal static string FormatAppError(string? app, string? message) =>
        $"[app-error] {LogText(app, MaxAppErrorTagLength)}: {LogText(message, MaxAppErrorMessageLength)}";

    // Untrusted text: scrub like the upload, cap the length and neutralize anything that can break or forge a log line.
    private static string LogText(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        s = ErrorReporter.Scrub(s);
        if (s.Length > max) s = s[..max];
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++) span[i] = IsUnsafeLogChar(src[i]) ? ' ' : src[i];
        });
    }

    // C0/C1 controls and DEL, line and paragraph separators, and the bidi marks and overrides.
    private static bool IsUnsafeLogChar(char c) =>
        char.IsControl(c)
        || c is '\u2028' or '\u2029' or '\u200E' or '\u200F'
        || c is >= '\u202A' and <= '\u202E'
        || c is >= '\u2066' and <= '\u2069';

    private const int MaxClientErrors = 10;
    private const int MaxClientErrorsBytes = 128 * 1024;

    private static async Task DeliverInBackground(FleetEventService fleet, string transitionType)
    {
        try
        {
            await fleet.DeliverConsentTransitionAsync(transitionType, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fleet-event] consent delivery failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

public sealed class TelemetryConsentBody { public bool Enabled { get; set; } }
public sealed class TelemetryConsentDto { public bool Enabled { get; set; } }

/// <summary>Fixed-window counter; one instance per cap so the state is not shared across callers.</summary>
internal sealed class AppErrorLogLimiter(int maxPerMinute)
{
    private readonly object _gate = new();
    private long _start;
    private int _count;

    public bool TryAcquire(long nowMs)
    {
        lock (_gate)
        {
            if (nowMs - _start >= 60_000 || nowMs < _start)
            {
                _start = nowMs;
                _count = 0;
            }
            if (_count >= maxPerMinute)
                return false;
            _count++;
            return true;
        }
    }
}
