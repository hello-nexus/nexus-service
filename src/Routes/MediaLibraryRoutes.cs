using System.IO;
using Nexus.Service.Auth;
using Nexus.Service.Lifecycle;
using Nexus.Service.Klipy;
using Nexus.Service.Lighting;
using Nexus.Service.Media;
using Nexus.Service.Models;
using Nexus.Service.Models.Klipy;
using Nexus.Service.Models.Media;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

public static class MediaLibraryRoutes
{
    public static void MapMediaLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/media/library", (MediaLibrary lib) =>
            new MediaLibraryResponse { Items = lib.ListItems() }).AllowPanel();

        app.MapPost("/media/import", async (HttpContext ctx, MediaLibrary lib, MultiplexHub hub) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "Expected multipart/form-data" });
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "No file provided" });
            }

            if (file.Length > MediaImporter.MaxFileSize)
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = $"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)" });
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"nexus-import-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                var result = await MediaImporter.ImportAsync(lib, tempPath, file.FileName, form["crop"].ToString());
                if (!result.Ok)
                {
                    return Results.BadRequest(new MediaImportResponse { Error = true, Msg = result.Error ?? "Import failed" });
                }

                PanelTopics.BroadcastMediaLibrary(hub);
                return Results.Ok(new MediaImportResponse { Item = result.Item });
            }
            finally
            {
                try
                { File.Delete(tempPath); }
                catch { }
            }
        }).DisableAntiforgery();

        app.MapPost("/media/stage", async (HttpContext ctx, MediaLibrary lib) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new MediaStageResponse { Error = true, Msg = "Expected multipart/form-data" });
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new MediaStageResponse { Error = true, Msg = "No file provided" });
            }

            if (file.Length > MediaImporter.MaxFileSize)
            {
                return Results.BadRequest(new MediaStageResponse
                {
                    Error = true,
                    Msg = $"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)",
                });
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nexus-media-stage-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                var result = await MediaImporter.StageAsync(lib, tempPath, file.FileName);
                if (!result.Ok)
                {
                    return Results.BadRequest(new MediaStageResponse { Error = true, Msg = result.Error ?? "Stage failed" });
                }

                return Results.Ok(new MediaStageResponse { StageId = result.StageId, MediaKind = MediaKinds.FromPath(file.FileName) });
            }
            finally
            {
                // Only reached if StageAsync didn't move the file (error path).
                try { File.Delete(tempPath); }
                catch { }
            }
        }).AllowPanel().DisableAntiforgery();

        app.MapGet("/media/stage/{stageId}/preview", (string stageId, MediaLibrary lib) =>
        {
            if (!MediaLibrary.IsValidId(stageId))
            {
                return Results.BadRequest("invalid stage id");
            }

            var previewPath = lib.GetStagePreviewPath(stageId);
            if (!File.Exists(previewPath))
            {
                return Results.NotFound();
            }

            return Results.File(previewPath, "image/jpeg");
        }).AllowPanel();

        // The staged source itself, so the cropper can play it; range requests let a <video> seek.
        app.MapGet("/media/stage/{stageId}/raw", (string stageId, HttpContext ctx, MediaLibrary lib) =>
        {
            if (!MediaLibrary.IsValidId(stageId))
            {
                return Results.BadRequest("invalid stage id");
            }

            var rawPath = lib.FindStagedRaw(stageId);
            if (rawPath is null || !File.Exists(rawPath))
            {
                return Results.NotFound();
            }

            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(rawPath, MediaKinds.ContentTypeFor(rawPath), enableRangeProcessing: true);
        }).AllowPanel();

        app.MapPost("/media/commit", async (HttpContext ctx, MediaLibrary lib, MultiplexHub hub) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "Expected multipart/form-data" });
            }

            var form = await ctx.Request.ReadFormAsync();

            var stageId = form["stageId"].ToString();
            if (string.IsNullOrEmpty(stageId))
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "stageId is required" });
            }

            if (!MediaLibrary.IsValidId(stageId))
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "invalid stage id" });
            }

            var cropStr = form["crop"].ToString();
            if (string.IsNullOrEmpty(cropStr))
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "crop is required (x,y,w,h)" });
            }

            if (lib.FindStagedRaw(stageId) is null)
            {
                return Results.NotFound(new MediaImportResponse { Error = true, Msg = "Stage not found or expired" });
            }

            var result = await MediaImporter.CommitAsync(lib, stageId, cropStr, form["name"].ToString());
            if (!result.Ok)
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = result.Error ?? "Commit failed" });
            }

            PanelTopics.BroadcastMediaLibrary(hub);
            return Results.Ok(new MediaImportResponse { Item = result.Item });
        }).AllowPanel().DisableAntiforgery();

        // A Klipy pick lands in staging like an upload, so the cropper runs on
        // it and the ordinary /media/commit finishes the import.
        app.MapPost("/media/klipy/stage", async (
            KlipyImportRequest body, MediaLibrary lib, IKlipyCatalog catalog, HttpContext ctx) =>
        {
            if (!KlipyCatalog.IsValidSlug(body.Slug))
            {
                return Results.BadRequest(new MediaStageResponse { Error = true, Msg = "invalid slug" });
            }

            string? tempPath = null;
            try
            {
                tempPath = await catalog.DownloadAsync(body.Slug, Path.GetTempPath(), ctx.RequestAborted);
                if (tempPath is null)
                {
                    return Results.BadRequest(new MediaStageResponse { Error = true, Msg = "Download failed" });
                }

                var staged = await MediaImporter.StageAsync(lib, tempPath, body.Slug + Path.GetExtension(tempPath));
                if (!staged.Ok)
                {
                    Console.Error.WriteLine($"[media-klipy] stage {body.Slug}: {staged.Error}");
                    return Results.BadRequest(new MediaStageResponse { Error = true, Msg = staged.Error ?? "Stage failed" });
                }

                _ = catalog.TriggerShareAsync(body.Slug);
                return Results.Ok(new MediaStageResponse { StageId = staged.StageId, MediaKind = MediaKinds.FromPath(tempPath) });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[media-klipy] stage {body.Slug} failed: {ex}");
                return Results.BadRequest(new MediaStageResponse { Error = true, Msg = "Stage failed" });
            }
            finally
            {
                // StageAsync moves the file into staging on success; a leftover means it did not.
                if (tempPath is not null)
                {
                    try { File.Delete(tempPath); }
                    catch { }
                }
            }
        }).AllowPanel().DisableAntiforgery();

        app.MapDelete("/media/stage/{stageId}", (string stageId, MediaLibrary lib) =>
        {
            if (!MediaLibrary.IsValidId(stageId))
            {
                return Results.BadRequest(new MediaPlayResponse { Error = true, Msg = "invalid stage id" });
            }

            lib.DeleteStage(stageId);
            return Results.Ok(new MediaPlayResponse());
        }).AllowPanel();

        app.MapDelete("/media/{id}", (string id, MediaLibrary lib, MultiplexHub hub) =>
        {
            if (!MediaLibrary.IsValidId(id))
                return Results.BadRequest(new MediaPlayResponse { Error = true, Msg = "invalid media id" });

            var deleted = lib.DeleteItem(id);
            if (deleted)
            {
                PanelTopics.BroadcastMediaLibrary(hub);
            }

            return deleted
                ? Results.Ok(new MediaPlayResponse())
                : Results.NotFound();
        });

        app.MapPost("/media/library/open", async (MediaLibrary lib, IServiceProvider sp) =>
        {
            try
            {
                var dir = lib.RootDir;
                Directory.CreateDirectory(dir);
#if WINDOWS
                if (OperatingSystem.IsWindows())
                {
                    // The Session-0 service can't show Explorer; the user-session
                    // helper opens the folder and brings it over the app window.
                    var registry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                    if (registry is null ||
                        !await Nexus.Service.Helper.Domains.FileDialogCommands.OpenFolderAsync(registry, dir))
                    {
                        return Results.Problem("no interactive user session");
                    }

                    return Results.Ok(new MediaPlayResponse());
                }
#endif
                await Task.CompletedTask;
                var psi = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false };
                if (OperatingSystem.IsMacOS())
                {
                    psi.FileName = "open";
                    psi.Arguments = $"\"{dir}\"";
                }
                else
                {
#if LINUX
                    // Root daemon: xdg-open must run in the session user's context, not root's.
                    var (file, args) = Nexus.Service.Platform.Linux.LinuxSession.WrapSpawnAsSessionUser(
                        "xdg-open", new List<string> { dir });
                    psi.FileName = file;
                    foreach (var a in args)
                        psi.ArgumentList.Add(a);
#else
                    psi.FileName = "xdg-open";
                    psi.Arguments = $"\"{dir}\"";
#endif
                }
                System.Diagnostics.Process.Start(psi);
                return Results.Ok(new MediaPlayResponse());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[media-library] open folder failed: {ex.Message}");
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/media/{id}/thumbnail", (string id, MediaLibrary lib) =>
        {
            if (!MediaLibrary.IsValidId(id))
                return Results.BadRequest("invalid media id");

            var thumbPath = lib.GetThumbPath(id);
            if (!File.Exists(thumbPath))
            {
                return Results.NotFound();
            }

            return Results.File(thumbPath, "image/jpeg");
        }).AllowPanel();

        app.MapPost("/media/{id}/play", (string id, ILightingProvider lighting, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            if (!MediaLibrary.IsValidId(id))
            {
                return Results.BadRequest(new MediaPlayResponse { Error = true, Msg = "invalid media id" });
            }

            var ok = lighting.StartMedia(id);
            if (ok)
            {
                PanelTopics.BroadcastLighting(hub);
            }

            return ok
                ? Results.Ok(new MediaPlayResponse())
                : Results.NotFound();
        }).AllowPanel();

        app.MapPost("/media/idle", (ILightingProvider lighting, MultiplexHub hub, FeatureGates gates) =>
        {
            if (!gates.Lighting)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Lighting });
            }
            lighting.StartMediaIdle();
            PanelTopics.BroadcastLighting(hub);
            return Results.Ok(ApiResponse.Ok());
        }).AllowPanel();

        app.MapGet("/media/current", (Nexus.Service.Persistence.IConfigStore store, MediaLibrary lib) =>
        {
            var lastId = store.Load().Lighting.LastMediaId;
            return new MediaCurrentResponse
            {
                MediaId = lastId,
                Item = string.IsNullOrEmpty(lastId) ? null : lib.GetItem(lastId),
            };
        }).AllowPanel();
    }
}
