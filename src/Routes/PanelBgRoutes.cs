using System.IO;
using Nexus.Service.Auth;
using Nexus.Service.Klipy;
using Nexus.Service.Media;
using Nexus.Service.Models.Klipy;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Routes;

public static class PanelBgRoutes
{
    /// <summary>The file an asset was baked to.</summary>
    private static string MediaExtFor(PanelBgItem item) => item.Type == "animated"
        ? (item.Alpha ? ".gif" : ".mp4")
        : (item.Alpha ? ".png" : ".jpg");

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        _ => "image/jpeg",
    };

    public static void MapPanelBgEndpoints(this WebApplication app)
    {
        app.MapGet("/panel/devices/{deviceId}/background-media/library",
            (string deviceId, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest(new PanelBgListResponse { Error = true, Msg = "invalid deviceId" });
                }

                return Results.Ok(new PanelBgListResponse { Items = lib.ListItems(deviceId) });
            }).AllowPanel();

        // --- Stage phase: upload raw, extract server-side preview ---

        app.MapPost("/panel/devices/{deviceId}/background-media/stage",
            async (string deviceId, HttpContext ctx, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = "invalid deviceId" });
                }

                if (!ctx.Request.HasFormContentType)
                {
                    return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = "Expected multipart/form-data" });
                }

                var form = await ctx.Request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                {
                    return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = "No file provided" });
                }

                if (file.Length > PanelBgImporter.MaxFileSize)
                {
                    return Results.BadRequest(new PanelBgStageResponse
                    {
                        Error = true,
                        Msg = $"File too large (max {PanelBgImporter.MaxFileSize / 1024 / 1024} MB)",
                    });
                }

                // Write upload to a temp file; StageAsync moves it into staging.
                var tempPath = Path.Combine(
                    Path.GetTempPath(),
                    $"nexus-bg-stage-{System.Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
                try
                {
                    using (var stream = File.Create(tempPath))
                    {
                        await file.CopyToAsync(stream);
                    }

                    var result = await PanelBgImporter.StageAsync(lib, deviceId, tempPath, file.FileName);
                    if (!result.Ok)
                    {
                        return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = result.Error ?? "Stage failed" });
                    }

                    return Results.Ok(new PanelBgStageResponse
                    {
                        StageId = result.StageId,
                        Alpha = result.Alpha,
                        MediaKind = MediaKinds.FromPath(file.FileName),
                    });
                }
                finally
                {
                    // Only reached if StageAsync didn't move the file (error path).
                    try { File.Delete(tempPath); }
                    catch { }
                }
            }).AllowPanel().DisableAntiforgery();

        app.MapGet("/panel/devices/{deviceId}/background-media/stage/{stageId}/preview",
            (string deviceId, string stageId, HttpContext ctx, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest("invalid deviceId");
                }

                var previewPath = lib.FindStagedPreview(deviceId, stageId);
                if (previewPath is null)
                {
                    return Results.NotFound();
                }

                ctx.Response.Headers.CacheControl = "no-store";
                return Results.File(previewPath, ContentTypeFor(previewPath));
            }).AllowPanel();

        // --- Commit phase: bake from staged raw using crop/dimensions ---

        app.MapGet("/panel/devices/{deviceId}/background-media/stage/{stageId}/raw",
            (string deviceId, string stageId, HttpContext ctx, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId) || !PanelBgLibrary.IsValidId(stageId))
                {
                    return Results.BadRequest("invalid id");
                }

                var rawPath = lib.FindStagedRaw(deviceId, stageId);
                if (rawPath is null || !File.Exists(rawPath))
                {
                    return Results.NotFound();
                }

                ctx.Response.Headers.CacheControl = "no-store";
                return Results.File(rawPath, MediaKinds.ContentTypeFor(rawPath), enableRangeProcessing: true);
            }).AllowPanel();

        app.MapPost("/panel/devices/{deviceId}/background-media/commit",
            async (string deviceId, HttpContext ctx, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest(new PanelBgImportResponse { Error = true, Msg = "invalid deviceId" });
                }

                if (!ctx.Request.HasFormContentType)
                {
                    return Results.BadRequest(new PanelBgImportResponse { Error = true, Msg = "Expected multipart/form-data" });
                }

                var form = await ctx.Request.ReadFormAsync();

                var stageId = form["stageId"].ToString();
                if (string.IsNullOrEmpty(stageId))
                {
                    return Results.BadRequest(new PanelBgImportResponse { Error = true, Msg = "stageId is required" });
                }

                var cropStr = form["crop"].ToString();
                if (string.IsNullOrEmpty(cropStr) || !CropRect.TryParse(cropStr, out var cropRect))
                {
                    return Results.BadRequest(new PanelBgImportResponse { Error = true, Msg = "crop is required (x,y,w,h)" });
                }

                if (!int.TryParse(form["w"].ToString(), out var targetW) || targetW < 1 || targetW > 8192)
                {
                    return Results.BadRequest(new PanelBgImportResponse { Error = true, Msg = "w is required (1..8192)" });
                }

                if (!int.TryParse(form["h"].ToString(), out var targetH) || targetH < 1 || targetH > 8192)
                {
                    return Results.BadRequest(new PanelBgImportResponse { Error = true, Msg = "h is required (1..8192)" });
                }

                if (lib.FindStagedRaw(deviceId, stageId) is null)
                {
                    return Results.NotFound(new PanelBgImportResponse { Error = true, Msg = "Stage not found or expired" });
                }

                // Absent means keep: a client too old to send the field gets the default.
                var keepTransparency = form["keepTransparency"].ToString() is not "0" and not "false";
                // Absent means fill, which is what every client sent before the switch existed.
                var fitWhole = form["fit"].ToString() is "1" or "true";

                var result = await PanelBgImporter.CommitAsync(
                    lib, deviceId, stageId, cropRect, targetW, targetH, keepTransparency, fitWhole);
                if (!result.Ok)
                {
                    return Results.BadRequest(new PanelBgImportResponse { Error = true, Msg = result.Error ?? "Commit failed" });
                }

                return Results.Ok(new PanelBgImportResponse { Item = result.Item });
            }).AllowPanel().DisableAntiforgery();

        // A Klipy pick lands in staging like an upload would, so the cropper
        // runs on it and the ordinary /commit finishes the import.
        app.MapPost("/panel/devices/{deviceId}/background-media/klipy/stage",
            async (string deviceId, KlipyPanelBgStageRequest body, PanelBgLibrary lib,
                   IKlipyCatalog catalog, HttpContext ctx) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = "invalid deviceId" });
                }

                if (!KlipyCatalog.IsValidSlug(body.Slug))
                {
                    return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = "invalid slug" });
                }

                string? tempPath = null;
                try
                {
                    tempPath = await catalog.DownloadAsync(body.Slug, Path.GetTempPath(), ctx.RequestAborted);
                    if (tempPath is null)
                    {
                        return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = "Download failed" });
                    }

                    var staged = await PanelBgImporter.StageAsync(lib, deviceId, tempPath, body.Slug + Path.GetExtension(tempPath));
                    if (!staged.Ok)
                    {
                        Console.Error.WriteLine($"[panel-bg-klipy] stage {body.Slug}: {staged.Error}");
                        return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = staged.Error ?? "Stage failed" });
                    }

                    _ = catalog.TriggerShareAsync(body.Slug);
                    return Results.Ok(new PanelBgStageResponse
                    {
                        StageId = staged.StageId,
                        Alpha = staged.Alpha,
                        MediaKind = MediaKinds.FromPath(tempPath),
                    });
                }
                catch (System.Exception ex)
                {
                    Console.Error.WriteLine($"[panel-bg-klipy] stage {body.Slug} failed: {ex}");
                    return Results.BadRequest(new PanelBgStageResponse { Error = true, Msg = "Stage failed" });
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

        // --- Cancel stage ---

        app.MapDelete("/panel/devices/{deviceId}/background-media/stage/{stageId}",
            (string deviceId, string stageId, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest(new PanelBgResponse { Error = true, Msg = "invalid deviceId" });
                }

                lib.DeleteStage(deviceId, stageId);
                return Results.Ok(new PanelBgResponse());
            }).AllowPanel();

        // --- Existing endpoints (unchanged) ---

        app.MapDelete("/panel/devices/{deviceId}/background-media/{id}",
            (string deviceId, string id, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest(new PanelBgResponse { Error = true, Msg = "invalid deviceId" });
                }

                if (!PanelBgLibrary.IsValidId(id))
                {
                    return Results.BadRequest(new PanelBgResponse { Error = true, Msg = "invalid id" });
                }

                return lib.DeleteItem(deviceId, id) ? Results.Ok(new PanelBgResponse()) : Results.NotFound();
            }).AllowPanel();

        app.MapGet("/panel/devices/{deviceId}/background-media/{id}/thumbnail",
            (string deviceId, string id, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest("invalid deviceId");
                }

                if (!PanelBgLibrary.IsValidId(id))
                {
                    return Results.BadRequest("invalid id");
                }

                var path = lib.GetThumbPath(deviceId, id, lib.GetItem(deviceId, id)?.Alpha ?? false);
                return File.Exists(path) ? Results.File(path, ContentTypeFor(path)) : Results.NotFound();
            }).AllowPanel();

        app.MapGet("/panel/devices/{deviceId}/background-media/{id}/file",
            (string deviceId, string id, HttpContext ctx, PanelBgLibrary lib) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest("invalid deviceId");
                }

                if (!PanelBgLibrary.IsValidId(id))
                {
                    return Results.BadRequest("invalid id");
                }

                var item = lib.GetItem(deviceId, id);
                if (item is null)
                {
                    return Results.NotFound();
                }

                var mediaPath = lib.GetMediaPath(deviceId, id, MediaExtFor(item));
                if (!File.Exists(mediaPath))
                {
                    return Results.NotFound();
                }

                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                // Without ranges the Chromium <video> on the panel has to land a
                // multi-MB body in one uninterrupted response and cannot resume.
                return Results.File(mediaPath, ContentTypeFor(mediaPath), enableRangeProcessing: true);
            }).AllowPanel();

        app.MapPost("/panel/devices/{deviceId}/background-media/library/open",
            async (string deviceId, PanelBgLibrary lib, IServiceProvider sp) =>
            {
                if (!PanelBgLibrary.IsValidId(deviceId))
                {
                    return Results.BadRequest(new PanelBgResponse { Error = true, Msg = "invalid deviceId" });
                }

                try
                {
                    var dir = lib.GetDeviceDir(deviceId);
                    Directory.CreateDirectory(dir);
#if WINDOWS
                    if (OperatingSystem.IsWindows())
                    {
                        var registry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                        if (registry is null ||
                            !await Nexus.Service.Helper.Domains.FileDialogCommands.OpenFolderAsync(registry, dir))
                        {
                            return Results.Problem("no interactive user session");
                        }

                        return Results.Ok(new PanelBgResponse());
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
                    return Results.Ok(new PanelBgResponse());
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[panel-bg] open folder failed: {ex.Message}");
                    return Results.Problem(ex.Message);
                }
            }).AllowPanel();
    }
}
