using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Auth;
using Nexus.Service.Gallery;
using Nexus.Service.Media;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Gallery widget backend: per-system shared image sources (referenced
/// files/folders + uploads). Item reads are panel-accessible; source
/// management and the native file-picker dialog are desktop-tier only and
/// must never be reachable from a paired panel session.
/// </summary>
public static class GalleryRoutes
{
    public static void MapGalleryEndpoints(this WebApplication app)
    {
        app.MapGet("/gallery/sources", (GalleryLibrary lib) =>
            new GallerySourcesResponse { Sources = lib.ListSources() });

        app.MapPost("/gallery/sources", (AddGallerySourceBody body, GalleryLibrary lib, MultiplexHub hub) =>
        {
            var result = lib.AddReference(body.Path, body.Kind);
            if (result.Error)
            {
                return Results.BadRequest(result);
            }

            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(result);
        });

        app.MapDelete("/gallery/sources/{id}", (string id, GalleryLibrary lib, GalleryResizeCache resize, MultiplexHub hub) =>
        {
            // IsValidId is load-bearing here, not hygiene: the static-asset
            // auth lane is method-blind, so "DELETE /gallery/sources/x.json"
            // reaches this handler unauthenticated - the dot-rejecting id
            // check is what turns it into a 404.
            if (!MediaLibrary.IsValidId(id) || !lib.RemoveSource(id))
            {
                return Results.NotFound();
            }

            PurgeDerivatives(lib, resize);
            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(new GallerySourceMutationResponse());
        });

        app.MapGet("/gallery/items", (GalleryLibrary lib) =>
            new GalleryItemsResponse { Items = lib.EnumerateItems() }).AllowPanel();

        app.MapGet("/gallery/items/{id}/file",
            async Task<IResult> (string id, GalleryLibrary lib, GalleryResizeCache resize, HttpContext ctx) =>
        {
            var path = MediaLibrary.IsValidId(id) ? lib.ResolveItemPath(id) : null;
            if (path is null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            // ?w= asks for a panel-sized derivative instead of the user's
            // full-resolution file. Parsed leniently on purpose: a malformed
            // width is a request for "whatever you have", not a 400, so no
            // client ever has to handle a resize error. Every failure inside
            // the cache - unsupported format, no ffmpeg, an encode that timed
            // out or was too busy - comes back null and falls through here too.
            if (int.TryParse(ctx.Request.Query["w"], out var requested) && requested > 0)
            {
                var derived = await resize.GetAsync(id, path, requested, ctx.RequestAborted);
                if (derived is not null)
                {
                    // Opened here rather than handed to Kestrel as a path: the
                    // cache sweep could otherwise unlink it between this check
                    // and the response being written, which faults the request
                    // instead of falling back. Holding the handle also makes
                    // the sweep's delete fail and skip a file being served.
                    try
                    {
                        var stream = new FileStream(derived, FileMode.Open, FileAccess.Read, FileShare.Read);
                        // The file name doubles as the ETag and covers the
                        // source's mtime and size, so a photo replaced on disk
                        // invalidates the panel's copy. The URL alone cannot do
                        // that: item ids hash the path, not the contents. Hence
                        // must-revalidate, which turns a revisit into a 304
                        // rather than another full body over the panel link.
                        ctx.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
                        var etag = new EntityTagHeaderValue($"\"{Path.GetFileNameWithoutExtension(derived)}\"");
                        return Results.File(stream, "image/jpeg", entityTag: etag);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Swept out from under us; the original still works.
                    }
                }
            }

            return Results.File(path, ContentTypeFor(path));
        }).AllowPanel();

        // Hide a folder item / clear a source's exclusion list. References
        // only - nothing on disk is ever touched.
        app.MapPost("/gallery/sources/{id}/exclude", (string id, GalleryExcludeBody body, GalleryLibrary lib, GalleryResizeCache resize, MultiplexHub hub) =>
        {
            if (!MediaLibrary.IsValidId(id) || !lib.ExcludeItem(id, body.ItemId))
            {
                return Results.NotFound();
            }

            PurgeDerivatives(lib, resize);
            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(new GallerySourceMutationResponse());
        });

        app.MapPost("/gallery/sources/{id}/restore", (string id, GalleryLibrary lib, MultiplexHub hub) =>
        {
            if (!MediaLibrary.IsValidId(id) || !lib.RestoreExclusions(id))
            {
                return Results.NotFound();
            }

            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(new GallerySourceMutationResponse());
        });

        // Native OS file/folder picker on the host PC. Desktop-tier only and
        // deliberately NOT relayed - the dialog opens on the host's screen.
        // RequestAborted flows in so closing the page abandons the wait (and
        // kills the dialog child process on macOS/Linux). IFileDialogPicker
        // is shared with /system/pick-path (SystemRoutes.cs); this route
        // always requests the image-filter multiselect mode.
        app.MapPost("/gallery/pick", async (GalleryPickBody body, IFileDialogPicker picker, HttpContext ctx) =>
        {
            var mode = body.Folder ? FileDialogPickMode.Folder : FileDialogPickMode.ImagesMultiSelect;
            var result = await picker.PickAsync(mode, ctx.RequestAborted);
            return new GalleryPickResponse
            {
                Paths = result.Paths,
                Cancelled = result.Cancelled,
                Error = result.Error,
                Msg = result.Msg,
            };
        });
    }

    /// <summary>
    /// Drops derivatives of items the mutation just removed from the gallery.
    /// Best-effort and inline: source mutations are rare user actions, and a
    /// failed sweep only leaves reclaimable bytes behind, never a wrong image.
    /// </summary>
    private static void PurgeDerivatives(GalleryLibrary lib, GalleryResizeCache resize)
    {
        try
        {
            var live = new HashSet<string>(lib.EnumerateItems().Select(i => i.Id), StringComparer.Ordinal);
            resize.PurgeExcept(live);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gallery-resize] purge failed: {ex.Message}");
        }
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            _ => "application/octet-stream",
        };
}
