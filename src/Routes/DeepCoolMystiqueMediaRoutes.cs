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

    // One upload at a time, from the slot check to the saved entry, so two pictures cannot both take the last slot.
    private static readonly SemaphoreSlim MystiqueUploadGate = new(1, 1);

    private static string MystiqueMediaDir() => MediaLibrary.DeviceStoreDir("deepcool-mystique");

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
                await MystiqueUploadGate.WaitAsync(budget.Token);
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
                    return MystiqueMediaFailure("The screen holds its maximum number of pictures; replace one instead.");
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
                string name = TruncateName(Path.GetFileNameWithoutExtension(file.FileName), MystiqueNameMax);
                store.Update(s =>
                {
                    var m = s.Devices.DeepCoolMystique;
                    m.Media ??= new();
                    var item = new DeepCoolMystiqueMediaItem { Kind = kind, Id = package.Id, Name = name };
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
                MystiqueUploadGate.Release();
            }
        }).DisableAntiforgery();

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
    /// The id to upload under, null when a new picture would exceed the slots. The screen replaces an
    /// item uploaded under an id it holds, so a GIF always reuses the stored GIF's id (one GIF slot), and a
    /// picture reuses the id it is replacing or the one its source already went up under.
    /// </summary>
    internal static string? MystiqueUploadId(IConfigStore store, string kind, byte[] source, string replace)
    {
        var media = new System.Collections.Generic.List<DeepCoolMystiqueMediaItem>();
        store.Read(s => media = s.Devices.DeepCoolMystique.Copy().Media);
        bool isGif = kind == DeepCoolMystiqueMediaItem.KindGif;
        string sourceId = MystiqueMedia.IdOf(isGif ? MystiqueMedia.KindGif : MystiqueMedia.KindPicture, source);
        if (isGif)
        {
            return media.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindGif)?.Id ?? sourceId;
        }
        var pictures = media.FindAll(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture);
        if (pictures.Exists(i => i.Id == replace))
        {
            return replace;
        }
        if (pictures.Exists(i => i.Id == sourceId) || pictures.Count < MystiqueMedia.MaxPictures)
        {
            return sourceId;
        }
        return null;
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
