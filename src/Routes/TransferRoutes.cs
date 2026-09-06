using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Transfer;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Clipboard;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Nexus.Service.Transfer;

namespace Nexus.Service.Routes;

/// <summary>
/// Phone→PC transfer surface. LAN-only by construction: "/transfer" is not in
/// <see cref="Nexus.Service.Relay.RelayHttpAllowlist"/>'s allowed prefixes, so
/// relayed sessions can never reach it.
/// </summary>
public static class TransferRoutes
{
    public static void MapTransferEndpoints(this WebApplication app)
    {
        app.MapPost("/transfer/items", async (HttpContext ctx, TransferInbox inbox, MultiplexHub hub, PanelPhonePairingService pairing) =>
        {
            var bodySize = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
                bodySize.MaxRequestBodySize = TransferInbox.MaxUploadBytes;

            if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var contentType) ||
                !"multipart/form-data".Equals(contentType.MediaType.Value, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new TransferItemsResponse { Error = true, Msg = "Expected multipart/form-data" });
            }
            var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
            if (string.IsNullOrEmpty(boundary))
                return Results.BadRequest(new TransferItemsResponse { Error = true, Msg = "Malformed form data" });

            TransferInbox.InboxTarget target;
            try
            {
                target = inbox.ResolveTarget();
            }
            // Locking the ProgramData fallback (ACL write, owner reset) can throw
            // UnauthorizedAccess / PrivilegeNotHeld / IO as well as the explicit
            // ownership refusal; none of them is the phone's fault.
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[transfer] inbox unavailable: {e.GetType().Name}: {e.Message}");
                return Results.Json(new TransferItemsResponse { Error = true, Msg = "transfer inbox unavailable" }, AppJsonContext.Default.TransferItemsResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            using var _ = target;
            var dir = target.Dir;
            TransferInbox.SweepStalePartials(target);

            var from = SenderName(ctx, pairing);
            var saved = new List<TransferSavedItem>();
            var failures = 0;
            try
            {
                // Streamed straight to the inbox: ReadFormAsync would buffer every
                // part to the service's %TEMP% first - a second full write and a
                // system-drive exhaustion risk at 2 GiB.
                // BodyLengthLimit is per-section; the request total is capped by
                // the MaxRequestBodySize override above.
                var reader = new MultipartReader(boundary, ctx.Request.Body) { BodyLengthLimit = TransferInbox.MaxUploadBytes };
                while (await reader.ReadNextSectionAsync(ctx.RequestAborted) is { } section)
                {
                    var disposition = section.GetContentDispositionHeader();
                    if (disposition is null || !disposition.IsFileDisposition())
                        continue;
                    var rawName = HeaderUtilities.RemoveQuotes(
                        !StringSegment.IsNullOrEmpty(disposition.FileNameStar) ? disposition.FileNameStar : disposition.FileName).Value;
                    try
                    {
                        var item = await inbox.SaveAsync(section.Body, rawName, target, ctx.RequestAborted);
                        saved.Add(item);
                        PanelTopics.BroadcastTransfer(hub, new TransferReceivedFrame
                        {
                            Kind = "file",
                            Name = item.Name,
                            Size = item.Size,
                            From = from,
                            Inbox = dir,
                        });
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        failures++;
                    }
                }
            }
            // The reader surfaces malformed bodies as InvalidDataException and a
            // truncated/empty multipart stream as a raw IOException.
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // Files saved before the malformed section stay saved (their
                // broadcasts already fired); report partial success.
                if (saved.Count == 0)
                    return Results.BadRequest(new TransferItemsResponse { Error = true, Msg = "Malformed form data" });
                failures++;
            }

            if (saved.Count == 0)
            {
                return Results.BadRequest(new TransferItemsResponse
                {
                    Error = true,
                    Msg = failures > 0 ? $"{failures} file(s) failed to save" : "No files provided",
                });
            }

            // No dashboard subscribed to the topic means nobody saw the WS
            // toast - surface a native notification instead. One summary
            // notice per request, not per file (a 20-photo batch must not
            // pop 20 balloons).
            if (!hub.TopicHasSubscribers(PanelTopics.Transfer))
            {
                // Interaction hints ("Click to open…") are appended by the
                // platform consumer - osascript notifications have no click.
                var sender = BalloonSender(from);
                var text = saved.Count == 1
                    ? $"{saved[0].Name} from {sender}."
                    : $"{saved.Count} files from {sender}.";
                inbox.RaiseAttention(new TransferAttentionNotice("Nexus transfer received", text, dir));
            }

            return Results.Ok(new TransferItemsResponse
            {
                Saved = saved,
                Inbox = dir,
                Msg = failures > 0 ? $"{failures} file(s) failed to save" : null,
            });
        }).AllowPanel();

        app.MapPost("/transfer/clipboard", (TransferClipboardBody body, HttpContext ctx, IClipboardProvider clipboard, MultiplexHub hub, PanelPhonePairingService pairing, TransferInbox inbox) =>
        {
            var text = body.Text ?? "";
            if (text.Length == 0)
                return ApiResponse.Fail("text required");
            if (text.Length > TransferInbox.MaxClipboardChars)
                return ApiResponse.Fail("text too large");
            // Set only - no paste chord. The user pastes when ready; injecting
            // keystrokes is /system/input/text's job.
            if (!clipboard.SetText(text))
                return ApiResponse.Fail("clipboard unavailable");
            var from = SenderName(ctx, pairing);
            PanelTopics.BroadcastTransfer(hub, new TransferReceivedFrame
            {
                Kind = "clipboard",
                Size = text.Length,
                From = from,
            });
            if (!hub.TopicHasSubscribers(PanelTopics.Transfer))
            {
                inbox.RaiseAttention(new TransferAttentionNotice(
                    "Nexus transfer received", $"Clipboard from {BalloonSender(from)}. Ready to paste.", null));
            }
            return ApiResponse.Ok();
        }).AllowPanel();
    }

    private static string SenderName(HttpContext ctx, PanelPhonePairingService pairing)
    {
        var sessionId = ctx.Items.TryGetValue(PathAuthMiddleware.PhoneSessionIdItem, out var raw) ? raw as string : null;
        return string.IsNullOrEmpty(sessionId) ? "" : pairing.GetSessionDisplayName(sessionId);
    }

    /// <summary>Phone-supplied display names aren't control-char-stripped at claim; balloons render newlines literally.</summary>
    private static string BalloonSender(string from)
    {
        if (string.IsNullOrEmpty(from))
            return "your phone";
        var chars = from.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < ' ')
                chars[i] = ' ';
        }
        return new string(chars);
    }
}
