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

                return Results.Ok(new MediaStageResponse { StageId = result.StageId });
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

        // One call, no staging: a pick has nothing to preview or crop by hand. The
        // body carries a slug; the URL comes from the catalog's search memo.
        app.MapPost("/media/klipy/import", async (
            KlipyImportRequest body, MediaLibrary lib, IKlipyCatalog catalog, MultiplexHub hub, HttpContext ctx) =>
        {
            if (!KlipyCatalog.IsValidSlug(body.Slug))
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "invalid slug" });
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"nexus-klipy-{Guid.NewGuid()}.gif");
            try
            {
                var downloaded = await catalog.DownloadAsync(body.Slug, tempPath, ctx.RequestAborted);
                if (!downloaded)
                {
                    return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "Download failed" });
                }

                var result = await MediaImporter.ImportAsync(lib, tempPath, $"{body.Slug}.gif", body.Crop);
                if (!result.Ok)
                {
                    return Results.BadRequest(new MediaImportResponse { Error = true, Msg = result.Error ?? "Import failed" });
                }

                _ = catalog.TriggerShareAsync(body.Slug);
                PanelTopics.BroadcastMediaLibrary(hub);
                return Results.Ok(new MediaImportResponse { Item = result.Item });
            }
            catch (Exception ex)
            {
                // An unhandled throw here answers with an empty body, which reads
                // as a silent failure in the picker.
                Console.Error.WriteLine($"[media-klipy] import {body.Slug} failed: {ex}");
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "Import failed" });
            }
            finally
            {
                try
                { File.Delete(tempPath); }
                catch { }
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
