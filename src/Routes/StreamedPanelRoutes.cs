using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
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

        // Global fps cap for every streamed panel. A change wakes the coordinator,
        // which re-mints live sessions at the new rate.
        app.MapGet("/panel/streams/settings", (HttpContext ctx, IConfigStore config, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            return Results.Json(new StreamSettingsDto { FpsCap = config.Load().StreamedPanelFpsCap }, AppJsonContext.Default.StreamSettingsDto);
        });

        app.MapPut("/panel/streams/settings", (JsonElement body, HttpContext ctx, IConfigStore config, StreamedPanelCoordinator coordinator, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            // Only an explicit null means Off; an absent key (a typo) must not silently switch the cap off.
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("fpsCap", out var cap))
                return Results.BadRequest(ApiResponse.Fail("fpsCap is required"));
            int? value = null;
            if (cap.ValueKind == JsonValueKind.Number && cap.TryGetInt32(out var n))
                value = n;
            else if (cap.ValueKind != JsonValueKind.Null)
                return Results.BadRequest(ApiResponse.Fail("fpsCap must be one of 30, 20, 15, 10, 5, 1 or null"));
            if (!StreamFpsCap.IsValid(value))
                return Results.BadRequest(ApiResponse.Fail("fpsCap must be one of 30, 20, 15, 10, 5, 1 or null"));
            config.Update(s => s.StreamedPanelFpsCap = value);
            coordinator.Wake();
            return Results.Json(new StreamSettingsDto { FpsCap = value }, AppJsonContext.Default.StreamSettingsDto);
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
            try
            {
                while (true)
                {
                    var result = await reader.ReadAsync(ctx.RequestAborted);
                    var buffer = result.Buffer;
                    while (StreamFrameReader.TryReadFrame(ref buffer, out var frame))
                    {
                        if (!frame!.IsControl)
                            session.AcceptFrame(frame);
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
