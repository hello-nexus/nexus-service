using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Media;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static readonly TimeSpan MystiqueUploadBudget = TimeSpan.FromMinutes(3);
    private const int MystiqueNameMax = 64;

    private static string MystiqueMediaDir() => MediaLibrary.DeviceStoreDir("deepcool-mystique");

    private static string MystiqueSourcePath(string id, string ext) => Path.Combine(MystiqueMediaDir(), "sources", id + ext);

    internal static bool MystiqueSourceExists(DeepCoolMystiqueMediaItem item) =>
        IsKeepableExt(item.Ext) && MystiqueMedia.IsId(item.Id) && File.Exists(MystiqueSourcePath(item.Id, item.Ext));

    // Lowercase letters and digits only: the extension becomes part of a file name.
    private static bool IsKeepableExt(string ext) =>
        ext.Length is >= 2 and <= 6 && ext[0] == '.' && ext.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789") < 0;

    private static IResult MystiqueMediaFailure(string msg, int status = 200) => Results.Json(
        new DeepCoolMystiqueMediaResponse { Error = true, Msg = msg }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse, statusCode: status);

    private static void MapDeepCoolMystiqueMediaEndpoints(WebApplication app)
    {
        app.MapPost("/devices/deepcool-mystique/media", async (HttpContext ctx, IConfigStore store, MystiqueWorker worker) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return MystiqueMediaFailure("Expected multipart/form-data", 400);
            }
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0)
            {
                return MystiqueMediaFailure("No file provided", 400);
            }
            if (file.Length > MediaImporter.MaxFileSize)
            {
                return MystiqueMediaFailure($"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)", 400);
            }
            string kind = form["kind"].ToString();
            if (kind is not (DeepCoolMystiqueMediaItem.KindPicture or DeepCoolMystiqueMediaItem.KindGif))
            {
                return MystiqueMediaFailure("Unknown kind", 400);
            }
            if (!worker.IsConnected)
            {
                return MystiqueMediaFailure("The screen is not connected");
            }

            CropRect.TryParse(form["crop"].ToString(), out var parsed);
            CropRect? crop = parsed.W > 0 ? parsed : null;
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);
            var source = ms.ToArray();
            var ext = Path.GetExtension(file.FileName);
            bool isGif = kind == DeepCoolMystiqueMediaItem.KindGif;

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            budget.CancelAfter(MystiqueUploadBudget);
            try
            {
                await worker.UploadGate.WaitAsync(budget.Token);
            }
            catch (OperationCanceledException)
            {
                return MystiqueMediaFailure("The upload took too long");
            }
            try
            {
                var id = MystiqueUploadId(store, kind, source, form["replace"].ToString());
                if (id is null)
                {
                    return MystiqueMediaFailure(isGif
                        ? "The screen holds its maximum number of GIFs; replace or remove one first."
                        : "The screen holds its maximum number of pictures; replace or remove one first.");
                }
                var encoded = isGif
                    ? await MystiqueMediaEncoder.EncodeGifAsync(source, ext, crop, id, budget.Token)
                    : await MystiqueMediaEncoder.EncodePictureAsync(source, ext, crop, id, budget.Token);
                if (!encoded.Ok)
                {
                    return MystiqueMediaFailure(encoded.Error ?? "Encode failed");
                }
                var package = encoded.Package!;
                // Once streaming starts this waits for the real result, so an item the screen took is always recorded.
                if (!await worker.UploadAsync(package, budget.Token))
                {
                    return MystiqueMediaFailure("The screen did not take the upload");
                }
                SaveMystiqueThumbnail(package.Id, encoded.Thumbnail!);
                string keptExt = ext.ToLowerInvariant();
                if (!IsKeepableExt(keptExt) || !SaveMystiqueSource(package.Id, keptExt, source))
                {
                    keptExt = "";
                }
                string name = TruncateName(Path.GetFileNameWithoutExtension(file.FileName), MystiqueNameMax);
                store.Update(s =>
                {
                    var m = s.Devices.DeepCoolMystique;
                    m.Media ??= new();
                    m.Freed ??= new();
                    m.Freed.RemoveAll(i => i.Id == package.Id);
                    var item = new DeepCoolMystiqueMediaItem { Kind = kind, Id = package.Id, Name = name, Ext = keptExt };
                    int at = m.Media.FindIndex(i => i.Id == package.Id);
                    if (at >= 0)
                    {
                        m.Media[at] = item;
                    }
                    else
                    {
                        m.Media.Add(item);
                    }
                    m.MediaSource = isGif ? DeepCoolMystiqueSettings.SourceGif : DeepCoolMystiqueSettings.SourceImages;
                    m.SourceEdited = true;
                });
                worker.RequestApply();
                return Results.Json(new DeepCoolMystiqueMediaResponse { Id = package.Id }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse);
            }
            catch (OperationCanceledException)
            {
                return MystiqueMediaFailure("The upload took too long");
            }
            finally
            {
                worker.UploadGate.Release();
            }
        }).DisableAntiforgery();

        app.MapDelete("/devices/deepcool-mystique/media/{id}", async (string id, HttpContext ctx, IConfigStore store, MystiqueWorker worker) =>
        {
            if (!MystiqueMedia.IsId(id))
            {
                return Results.NotFound();
            }
            return await ForgetMystiqueMedia(store, worker, i => i.Id == id, ctx.RequestAborted) > 0
                ? Results.Json(new DeepCoolMystiqueMediaResponse { Id = id }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse)
                : Results.NotFound();
        });

        // The screen's own format command, so unlike a single removal this erases the screen too.
        app.MapDelete("/devices/deepcool-mystique/media", async (HttpContext ctx, IConfigStore store, MystiqueWorker worker) =>
        {
            if (!worker.IsConnected)
            {
                return MystiqueMediaFailure("The screen is not connected");
            }
            await worker.UploadGate.WaitAsync(ctx.RequestAborted);
            try
            {
                if (!await worker.FormatAsync(ctx.RequestAborted))
                {
                    return MystiqueMediaFailure("The screen did not take the format command");
                }
                var gone = new System.Collections.Generic.List<DeepCoolMystiqueMediaItem>();
                store.Update(s =>
                {
                    var m = s.Devices.DeepCoolMystique;
                    gone = m.Media ?? new();
                    m.Media = new();
                    m.Freed = new();
                });
                gone.ForEach(DeleteMystiqueFiles);
                return Results.Json(new DeepCoolMystiqueMediaResponse(), AppJsonContext.Default.DeepCoolMystiqueMediaResponse);
            }
            finally
            {
                worker.UploadGate.Release();
            }
        });

        app.MapGet("/devices/deepcool-mystique/media/{id}/source", (string id, IConfigStore store) =>
        {
            if (!MystiqueMedia.IsId(id))
            {
                return Results.NotFound();
            }
            DeepCoolMystiqueMediaItem? item = null;
            store.Read(s => item = s.Devices.DeepCoolMystique.Copy().Media.Find(i => i.Id == id));
            if (item is null || !MystiqueSourceExists(item))
            {
                return Results.NotFound();
            }
            var path = MystiqueSourcePath(item.Id, item.Ext);
            return Results.File(path, MediaKinds.ContentTypeFor(path));
        });

        app.MapGet("/devices/deepcool-mystique/media/{id}/thumb", (string id) =>
        {
            if (!MystiqueMedia.IsId(id))
            {
                return Results.NotFound();
            }
            var path = Path.Combine(MystiqueMediaDir(), id + ".jpg");
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
        });
    }

    /// <summary>
    /// The id to upload under, null when the kind's slots are full. The screen replaces an item uploaded under
    /// an id it holds, so an upload reuses the id it is replacing, the one its source already went up under,
    /// or a freed id the screen still holds, before taking a new slot.
    /// </summary>
    internal static string? MystiqueUploadId(IConfigStore store, string kind, byte[] source, string replace)
    {
        var mystique = new DeepCoolMystiqueSettings();
        store.Read(s => mystique = s.Devices.DeepCoolMystique.Copy());
        bool isGif = kind == DeepCoolMystiqueMediaItem.KindGif;
        string sourceId = MystiqueMedia.IdOf(isGif ? MystiqueMedia.KindGif : MystiqueMedia.KindPicture, source);
        var items = mystique.Media.FindAll(i => i.Kind == kind);
        var freed = mystique.Freed.FindAll(i => i.Kind == kind);
        if (items.Exists(i => i.Id == replace))
        {
            return replace;
        }
        if (items.Exists(i => i.Id == sourceId) || freed.Exists(i => i.Id == sourceId))
        {
            return sourceId;
        }
        if (freed.Count > 0)
        {
            return freed[0].Id;
        }
        return items.Count < (isGif ? MystiqueMedia.MaxGifs : MystiqueMedia.MaxPictures) ? sourceId : null;
    }

    /// <summary>
    /// Drops the matching items from the list into <see cref="DeepCoolMystiqueSettings.Freed"/> with their files.
    /// Nexus-side only: the screen has no single-item delete, so it keeps each until an upload reuses its id.
    /// </summary>
    private static async Task<int> ForgetMystiqueMedia(IConfigStore store, MystiqueWorker worker, Predicate<DeepCoolMystiqueMediaItem> match, CancellationToken ct)
    {
        await worker.UploadGate.WaitAsync(ct);
        try
        {
            var gone = new System.Collections.Generic.List<DeepCoolMystiqueMediaItem>();
            store.Update(s =>
            {
                var m = s.Devices.DeepCoolMystique;
                m.Media ??= new();
                m.Freed ??= new();
                gone = m.Media.FindAll(match);
                m.Media.RemoveAll(match);
                foreach (var item in gone)
                {
                    if (!m.Freed.Exists(i => i.Id == item.Id))
                    {
                        m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = item.Kind, Id = item.Id });
                    }
                }
            });
            gone.ForEach(DeleteMystiqueFiles);
            return gone.Count;
        }
        finally
        {
            worker.UploadGate.Release();
        }
    }

    private static void DeleteMystiqueFiles(DeepCoolMystiqueMediaItem item)
    {
        if (!MystiqueMedia.IsId(item.Id))
        {
            return;
        }
        DeleteMystiqueFile(Path.Combine(MystiqueMediaDir(), item.Id + ".jpg"));
        if (IsKeepableExt(item.Ext ?? ""))
        {
            DeleteMystiqueFile(MystiqueSourcePath(item.Id, item.Ext!));
        }
    }

    private static void DeleteMystiqueFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[deepcool-mystique] media file not deleted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>At most <paramref name="max"/> UTF-16 units, never splitting a surrogate pair.</summary>
    internal static string TruncateName(string name, int max)
    {
        if (name.Length <= max)
        {
            return name;
        }
        int cut = char.IsHighSurrogate(name[max - 1]) ? max - 1 : max;
        return name[..cut];
    }

    private static bool SaveMystiqueSource(string id, string ext, byte[] source)
    {
        try
        {
            var path = MystiqueSourcePath(id, ext);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            foreach (var old in Directory.GetFiles(Path.GetDirectoryName(path)!, id + ".*"))
            {
                File.Delete(old);
            }
            File.WriteAllBytes(path, source);
            return true;
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[deepcool-mystique] source not kept: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void SaveMystiqueThumbnail(string id, byte[] jpeg)
    {
        try
        {
            Directory.CreateDirectory(MystiqueMediaDir());
            File.WriteAllBytes(Path.Combine(MystiqueMediaDir(), id + ".jpg"), jpeg);
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[deepcool-mystique] thumbnail not saved: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
