using System.IO;
using System.Security.Cryptography;
using System.Linq;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
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
}
