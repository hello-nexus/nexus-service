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
                if (e.Kind is not ("window-error" or "unhandled-rejection" or "render") || string.IsNullOrEmpty(e.Fingerprint))
                    continue;
                reporter.ReportClient(e.Kind, e.Fingerprint, e.Type ?? "", e.Message ?? "", e.Stack ?? "", e.Context, e.Count);
            }
            return Results.NoContent();
        }).AllowPanel();
    }

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
