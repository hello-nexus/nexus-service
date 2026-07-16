using System.IO;
using System.Linq;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Models.Deck;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// Deck key icon image upload/serve. AllowPanel (not LocalhostOnly) on both
/// routes: the physical deck editor and the virtual deck widget both render
/// on paired phone/Y70 panels, which must reach GET to show the icon.
/// </summary>
public static class DeckImageRoutes
{
    public static void MapDeckImageEndpoints(this WebApplication app)
    {
        app.MapPost("/deck/images", async (HttpRequest req, DeckImageStore store) =>
        {
            if (!req.HasFormContentType)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "Expected multipart/form-data" });
            }

            // Reject before ReadFormAsync buffers the body: the framework's
            // multipart limit (~128MB) would otherwise let a body far over the
            // per-image cap be spooled to disk before the length check below.
            // The envelope allowance covers the multipart boundary + headers.
            if (req.ContentLength is > DeckImageStore.MaxBytes + 16 * 1024)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "File too large" });
            }

            var form = await req.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "No file provided" });
            }

            if (file.Length > DeckImageStore.MaxBytes)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "File too large" });
            }

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var id = store.Store(bytes);
            if (id is null)
            {
                return Results.BadRequest(new DeckImageUploadResponse { Error = true, Msg = "Unsupported image format" });
            }

            return Results.Json(new DeckImageUploadResponse { Id = id }, AppJsonContext.Default.DeckImageUploadResponse);
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
            return Results.File(bytes, contentType, entityTag: new EntityTagHeaderValue($"\"{id}\""));
        }).AllowPanel();
    }
}
