using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static class StreamedPanelRoutes
{
    // An idle line this often keeps a host's per-read timeout from firing and shows the service a dead socket.
    private static readonly TimeSpan TouchKeepalive = TimeSpan.FromSeconds(15);
    private static readonly byte[] TouchKeepaliveLine = "\n"u8.ToArray();

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
            var session = coordinator.TryBindIngest(sessionId, ctx, ctx.Request.Headers[StreamFrameSection.NonceHeader].ToString());
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

        // One long-lived GET per touch session: a line per contact, "<pointerId> <down|move|up> <x> <y>",
        // with x and y fractions of the page's width and height. Blank lines are keepalives.
        app.MapGet("/panel/streams/{sessionId}/touch", async (string sessionId, HttpContext ctx, StreamedPanelCoordinator coordinator, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var feed = coordinator.TryGetTouchFeed(sessionId);
            if (feed?.Subscribe() is not { } reader)
                return Results.NotFound();
            try
            {
                await StreamTouchAsync(ctx, reader, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                feed.Unsubscribe(reader);
            }
            return Results.Empty;
        });
    }

    private static async Task StreamTouchAsync(HttpContext ctx, ChannelReader<PanelTouchContact> reader, CancellationToken ct)
    {
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.StartAsync(ct);
        var body = ctx.Response.Body;
        var lines = new StringBuilder();
        while (true)
        {
            bool more;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TouchKeepalive);
                try
                {
                    more = await reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await body.WriteAsync(TouchKeepaliveLine, ct);
                    await body.FlushAsync(ct);
                    continue;
                }
            }
            if (!more)
                return;
            lines.Clear();
            while (reader.TryRead(out var contact))
                lines.Append(FormatTouch(contact)).Append('\n');
            await body.WriteAsync(Encoding.UTF8.GetBytes(lines.ToString()), ct);
            await body.FlushAsync(ct);
        }
    }

    internal static string FormatTouch(PanelTouchContact contact) => string.Create(CultureInfo.InvariantCulture,
        $"{contact.PointerId} {contact.Phase switch { TouchPhase.Down => "down", TouchPhase.Move => "move", _ => "up" }} {contact.X:0.#####} {contact.Y:0.#####}");
}
