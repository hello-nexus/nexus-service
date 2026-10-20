using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Media;
using Nexus.Service.Models.Deck;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Deck key icon image upload/serve, plus the site-icon lookup for a URL key.
/// AllowPanel (not LocalhostOnly) throughout: the physical deck editor and the
/// virtual deck widget both render on paired phone/Y70 panels, which must reach
/// GET to show the icon.
/// </summary>
public static class DeckImageRoutes
{
    public static void MapDeckImageEndpoints(this WebApplication app)
    {
        app.MapPost("/deck/images", async (HttpRequest req, DeckImageStore store, CancellationToken ct) =>
        {
            if (!req.HasFormContentType)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "Expected multipart/form-data" });
            }

            // Reject before ReadFormAsync buffers the body: the framework's
            // multipart limit (~128MB) would otherwise let a body far over the
            // animated-image cap be spooled to disk before the length check below.
            // The envelope allowance covers the multipart boundary + headers.
            if (req.ContentLength is > DeckImageStore.MaxAnimatedBytes + 16 * 1024)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "File too large" });
            }

            var form = await req.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "No file provided" });
            }

            if (file.Length > DeckImageStore.MaxAnimatedBytes)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "File too large" });
            }

            var bytes = new byte[file.Length];
            await using (var upload = file.OpenReadStream())
            {
                await upload.ReadExactlyAsync(bytes);
            }

            // A crop reshapes an animated or video upload; stills arrive already cropped by the client.
            var cropRaw = form["crop"].ToString();
            if (cropRaw.Length > 0 && !CropRect.TryParse(cropRaw, out _))
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "invalid crop" });
            }
            var sniffed = DeckImageStore.SniffExtension(bytes);
            if (CropRect.TryParse(cropRaw, out _) && sniffed is ".gif" or ".mp4" or ".webm")
            {
                var kind = sniffed == ".gif" ? DeckImageTranscoder.Kind.Gif : DeckImageTranscoder.Kind.Video;
                var cropped = await TranscodeBytesAsync(bytes, sniffed!, kind, cropRaw, ct);
                if (cropped.Bytes is null)
                {
                    return TranscodeFailure(cropped.Error);
                }
                bytes = cropped.Bytes;
            }

            return (await StoreResultAsync(store, bytes, ct)).Result;
        }).AllowPanel().DisableAntiforgery();

        // A Klipy pick is staged by POST /media/klipy/stage; this crops and transcodes the staged file into a deck icon.
        app.MapPost("/deck/images/stage/{stageId}", async (string stageId, HttpRequest req, MediaLibrary lib, DeckImageStore store, CancellationToken ct) =>
        {
            if (!MediaLibrary.IsValidId(stageId))
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "invalid stage id" });
            }

            // The body only carries a crop string: refuse anything bigger before it is buffered.
            if (req.ContentLength is > StageBodyLimit)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "Request too large" });
            }
            if (req.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = StageBodyLimit;
            }

            string? cropRaw = null;
            try
            {
                if (req.HasFormContentType)
                {
                    cropRaw = (await req.ReadFormAsync(ct))["crop"].ToString();
                }
                else if (req.HasJsonContentType())
                {
                    cropRaw = (await req.ReadFromJsonAsync(AppJsonContext.Default.DeckImageStageRequest, ct))?.Crop;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or Microsoft.AspNetCore.Http.BadHttpRequestException)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "Invalid request body" });
            }
            if (!string.IsNullOrEmpty(cropRaw) && !CropRect.TryParse(cropRaw, out _))
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "invalid crop" });
            }

            var stagedPath = lib.FindStagedRaw(stageId);
            if (stagedPath is null || !File.Exists(stagedPath))
            {
                return Results.NotFound(new DeckImageUploadResponse { Error = true, Msg = "Stage not found or expired" });
            }
            if (DeckImageTranscoder.KindForExtension(Path.GetExtension(stagedPath)) is not { } kind)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "Unsupported image format" });
            }

            var result = await DeckImageTranscoder.RunAsync(stagedPath, kind, cropRaw, ct);
            if (result.Bytes is null)
            {
                return TranscodeFailure(result.Error);
            }

            var stored = await StoreResultAsync(store, result.Bytes, ct);
            if (stored.Ok)
            {
                lib.DeleteStage(stageId);
            }
            return stored.Result;
        }).AllowPanel().DisableAntiforgery();

        app.MapGet("/deck/images/{id}", (string id, HttpContext ctx, DeckImageStore store) =>
        {
            if (!DeckImageStore.IsValidId(id))
            {
                return Results.NotFound();
            }

            var loaded = store.TryLoad(id);
            if (loaded is null)
            {
                return Results.NotFound();
            }

            var (bytes, contentType) = loaded.Value;
            // Content-addressed: the id IS the hash, so it is a valid ETag with no extra hashing.
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(bytes, contentType, entityTag: new EntityTagHeaderValue($"\"{id}\""), enableRangeProcessing: true);
        }).AllowPanel();

        // A panel has no route to the open internet, so the fetch has to happen
        // here even though the caller is a browser that could do it itself.
        app.MapGet("/deck/site-icon", async (string? url, HttpContext ctx, ISiteIconResolver resolver) =>
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return Results.BadRequest();
            }

            var (bytes, contentType) = await resolver.ResolveAsync(url, ctx.RequestAborted);
            if (bytes.Length == 0)
            {
                // Short, so a site that gains an icon is not iconless for a day; the resolver's negative cache absorbs the retries.
                ctx.Response.Headers.CacheControl = "private, max-age=300";
                return Results.NotFound();
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            ctx.Response.Headers.CacheControl = "private, max-age=86400, must-revalidate";
            // These bytes come from a third-party site and can be SVG, which a
            // top-level navigation would render as a DOCUMENT on this origin
            // (the panel cookie is SameSite=Lax, so it rides along). Inert in
            // the <img> the deck actually uses; sandboxed here so the direct-
            // navigation path cannot run script against the service's routes.
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(bytes, contentType, entityTag: new EntityTagHeaderValue($"\"{hash}\""));
        }).AllowPanel();
    }

    private static async Task<(byte[]? Bytes, string? Error)> TranscodeBytesAsync(byte[] bytes, string ext, DeckImageTranscoder.Kind kind, string cropRaw, CancellationToken ct)
    {
        var input = Path.Combine(Path.GetTempPath(), $"nexus-deck-upload-{Guid.NewGuid():N}{ext}");
        try
        {
            await File.WriteAllBytesAsync(input, bytes, ct);
            return await DeckImageTranscoder.RunAsync(input, kind, cropRaw, ct);
        }
        finally
        {
            try { File.Delete(input); }
            catch { }
        }
    }

    private const long StageBodyLimit = 16 * 1024;

    private static IResult TranscodeFailure(string? error) => error == DeckImageTranscoder.BusyMessage
        ? Results.Json(new DeckImageUploadResponse { Error = true, Msg = DeckImageTranscoder.BusyMessage }, AppJsonContext.Default.DeckImageUploadResponse, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = error ?? "Unsupported image format" });

    private static async Task<(IResult Result, bool Ok)> StoreResultAsync(DeckImageStore store, byte[] bytes, CancellationToken ct)
    {
        var id = store.Store(bytes);
        if (id is null)
        {
            return (Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "Unsupported image format" }), false);
        }

        if (DeckImageStore.IsVideoExtension(DeckImageStore.SniffExtension(bytes)!))
        {
            await store.EnsurePosterAsync(id, ct);
        }

        return (Results.Json(new DeckImageUploadResponse { Id = id }, AppJsonContext.Default.DeckImageUploadResponse), true);
    }
}
