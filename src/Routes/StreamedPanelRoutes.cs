using System.IO;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Nexus.Service.Auth;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static class StreamedPanelRoutes
{
    public static void MapStreamedPanelEndpoints(this WebApplication app)
    {
        // Desired stream sessions; the overlay reconciles its off-screen
        // render hosts against this on every prefs poll, like
        // /displays/assignments for kiosks.
        app.MapGet("/panel/streams/assignments", (HttpContext ctx, StreamedPanelCoordinator coordinator, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            return Results.Json(coordinator.GetAssignments(), AppJsonContext.Default.StreamAssignmentsResponse);
        });

        // One long-lived chunked POST per stream session carrying framed
        // H.264 access units (see StreamFraming). The body never ends while
        // the session is healthy.
        app.MapPost("/panel/streams/{sessionId}/ingest", async (string sessionId, HttpContext ctx, StreamedPanelCoordinator coordinator, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var session = coordinator.TryBindIngest(sessionId, ctx);
            if (session is null)
                return Results.NotFound();

            // The global MaxRequestBodySize (sized for media imports) caps a
            // multi-hour stream, and the default MinDataRate would abort the
            // request during a capture stall; both must be lifted for this
            // request only. Keepalive control frames keep liveness observable
            // instead.
            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = null;
            ctx.Features.Get<IHttpMinRequestBodyDataRateFeature>()?.MinDataRate = null;

            var reader = ctx.Request.BodyReader;
            // Arrival gaps are routine (capture is change-driven), so the
            // session keeps only the window maximum for its stats line.
            long lastFrameTicks = 0;
            try
            {
                while (true)
                {
                    var result = await reader.ReadAsync(ctx.RequestAborted);
                    var buffer = result.Buffer;
                    while (StreamFrameReader.TryReadFrame(ref buffer, out var frame))
                    {
                        if (!frame!.IsControl)
                        {
                            var now = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (lastFrameTicks != 0)
                                session.RecordIngestGap((now - lastFrameTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency);
                            lastFrameTicks = now;
                            var native = session.Upscaler is { } upscaler ? upscaler.Apply(frame) : frame;
                            if (native is not null)
                                session.Enqueue(native);
                        }
                    }
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted) break;
                }
            }
            catch (InvalidDataException)
            {
                // Framing corruption: kill the connection; the overlay tears
                // the host down and respawns via reconcile.
                ctx.Abort();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                coordinator.OnIngestClosed(sessionId, ctx);
            }
            return Results.Ok();
        });
    }
}
