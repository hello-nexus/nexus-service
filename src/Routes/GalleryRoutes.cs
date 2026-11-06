using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Auth;
using Nexus.Service.Gallery;
using Nexus.Service.Media;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Gallery widget backend: per-system shared image and video sources
/// (referenced files/folders). Item reads are panel-accessible; source
/// management and the native file-picker dialog are desktop-tier only and
/// must never be reachable from a paired panel session.
/// </summary>
public static class GalleryRoutes
{
    public static void MapGalleryEndpoints(this WebApplication app)
    {
        app.MapGet("/gallery/sources", (GalleryLibrary lib) =>
            new GallerySourcesResponse { Sources = lib.ListSources() });

        app.MapPost("/gallery/sources", async (AddGallerySourceBody body, GalleryLibrary lib, IGalleryImporter importer, MultiplexHub hub, HttpContext ctx) =>
        {
            var result = lib.AddReference(body.Path, body.Kind);
            if (result.Error && result.Code == GalleryErrorCodes.Unreachable)
            {
                result = await ImportCopyAsync(body, lib, importer, ctx.RequestAborted);
            }

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
        {
            var e = lib.Enumerate();
            return new GalleryItemsResponse
            {
                Items = e.Items,
                Truncated = e.Truncated,
                Playlists = lib.ListPlaylists(),
            };
        }).AllowPanel();

        // Playlist management is desktop-tier like source management. Reads
        // ride on /gallery/items, so no playlist route needs panel reach.
        app.MapPost("/gallery/playlists", (GalleryPlaylistBody body, GalleryLibrary lib, MultiplexHub hub) =>
        {
            var result = lib.CreatePlaylist(body.Name);
            if (result.Error)
            {
                return Results.BadRequest(result);
            }

            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(result);
        });

        app.MapPut("/gallery/playlists/{id}", (string id, GalleryPlaylistBody body, GalleryLibrary lib, MultiplexHub hub) =>
        {
            if (!MediaLibrary.IsValidId(id))
            {
                return Results.NotFound();
            }

            var result = lib.UpdatePlaylist(id, body);
            if (result.Error)
            {
                return result.Code == GalleryErrorCodes.NotFound
                    ? Results.NotFound()
                    : Results.BadRequest(result);
            }

            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(result);
        });

        // Asked before a delete so the confirmation can name the widgets that
        // would fall back to the whole library.
        app.MapGet("/gallery/playlists/{id}/usage", (string id, IConfigStore store) =>
            MediaLibrary.IsValidId(id)
                ? Results.Ok(new GalleryPlaylistUsageResponse { Uses = GalleryPlaylistUsage.Find(store.Load(), id) })
                : Results.NotFound());

        app.MapDelete("/gallery/playlists/{id}", (string id, GalleryLibrary lib, MultiplexHub hub) =>
        {
            if (!MediaLibrary.IsValidId(id) || !lib.DeletePlaylist(id))
            {
                return Results.NotFound();
            }

            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(new GalleryPlaylistMutationResponse());
        });

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
            var wantsStill = int.TryParse(ctx.Request.Query["w"], out var requested) && requested > 0;
            var isVideo = GalleryLibrary.IsVideoFile(path);
            if (wantsStill)
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

                // A video has no still to fall back on: 404 rather than land
                // the whole clip in an <img>. The un-suffixed URL still plays it.
                if (isVideo)
                {
                    return Results.NotFound();
                }
            }

            // A OneDrive/iCloud placeholder hydrates on first read, which the cloud
            // filter can deny the service; probing one byte makes that a 404.
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
            try
            {
                await file.ReadAtLeastAsync(new byte[1], 1, throwOnEndOfStream: false, ctx.RequestAborted);
                file.Position = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await file.DisposeAsync();
                return Results.NotFound();
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }
            // Without ranges the Chromium <video> on the panel has to land the
            // whole clip in one uninterrupted response and cannot seek or loop
            // without refetching it.
            return Results.File(file, ContentTypeFor(path), lastModified: File.GetLastWriteTimeUtc(path), enableRangeProcessing: isVideo);
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
        // always requests the media-filter multiselect mode.
        app.MapPost("/gallery/pick", async (GalleryPickBody body, IFileDialogPicker picker, HttpContext ctx) =>
        {
            var mode = body.Folder ? FileDialogPickMode.Folder : FileDialogPickMode.MediaMultiSelect;
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
    /// The service cannot read the path (a mapped network drive on Windows),
    /// so the user-session importer copies it into gallery storage and the
    /// copy becomes the source.
    /// </summary>
    private static async Task<GallerySourceMutationResponse> ImportCopyAsync(
        AddGallerySourceBody body, GalleryLibrary lib, IGalleryImporter importer, CancellationToken ct)
    {
        var importDir = lib.CreateImportDir();
        var added = false;
        try
        {
            var copy = await importer.ImportAsync(
                new GalleryImportRequest { Path = body.Path, Kind = body.Kind, DestDir = importDir }, ct);
            if (copy.Error is not null)
            {
                return new GallerySourceMutationResponse { Error = true, Msg = copy.Error, Code = copy.Code };
            }

            var result = lib.AddImported(importDir, copy, body.Path);
            added = !result.Error;
            return result;
        }
        finally
        {
            if (!added)
            {
                lib.DiscardImport(importDir);
            }
        }
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
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream",
        };
}
