using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Bounds a kept source, so full slots of uploads up to MediaImporter.MaxFileSize never pile up gigabytes.
    internal const int MystiqueKeepMaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Saves the source a finished stream leaves the screen on and returns it, null for none. A new item selects its
    /// kind, as the screen's own software does, and so does a GIF sent while the pictures play, unless the user chose
    /// a source during the stream. The first new picture after a format selects pictures without sending the command
    /// (<see cref="DeepCoolMystiqueSettings.QuietSinceFormat"/>). A GIF replacing the slot the screen holds selects nothing
    /// while GIFs play, as the recorded edits sent no source command after the stream.
    /// </summary>
    internal static MystiqueSourceAfter? MystiqueSourceAfterUpload(DeepCoolMystiqueSettings m, bool isGif, bool inPlace, bool chosenDuringStream)
    {
        bool quiet = m.QuietSinceFormat && !isGif && !inPlace && !chosenDuringStream;
        if (!inPlace)
        {
            m.QuietSinceFormat = false;
        }
        bool gifNotPlaying = isGif && (!m.SourceEdited || m.MediaSource != DeepCoolMystiqueSettings.SourceGif);
        if (!chosenDuringStream && (!inPlace || gifNotPlaying))
        {
            m.MediaSource = isGif ? DeepCoolMystiqueSettings.SourceGif : DeepCoolMystiqueSettings.SourceImages;
            m.SourceEdited = true;
            return new MystiqueSourceAfter(m.MediaSource, !quiet);
        }
        if (inPlace)
        {
            return null;
        }
        return m.SourceEdited ? new MystiqueSourceAfter(m.MediaSource, true) : null;
    }

    /// <summary>
    /// The screen id a GIF goes up under, and whether the screen already holds it. Every GIF shares one id, so showing
    /// another replaces it in place; this assumes, from a customer report that the screen kept playing its first
    /// stored GIF while later ones were added, that the screen plays the oldest GIF it holds. Before a slot is recorded
    /// a GIF id the screen may hold is taken, freed ones first, then listed ones; no upload times are kept, so this is
    /// a guess that <see cref="DeepCoolMystiqueSettings.GifSlotUnsure"/> reports until a format.
    /// </summary>
    internal static (string Id, bool Held) MystiqueGifSlot(DeepCoolMystiqueSettings m, string itemId)
    {
        if (MystiqueMedia.IsId(m.GifSlot))
        {
            return (m.GifSlot, true);
        }
        var held = m.Freed?.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindGif) ?? m.Media?.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindGif);
        return held is not null ? (held.Id, true) : (itemId, false);
    }

    /// <summary>
    /// Records what a finished or failed GIF stream left in the slot: the shown item, or none when the stream failed.
    /// Freed GIF ids are dropped: GIFs never go up under them once a slot is recorded.
    /// </summary>
    internal static void SetMystiqueGifSlot(DeepCoolMystiqueSettings m, string slot, string shown)
    {
        m.GifSlot = slot;
        m.ShownGif = shown;
        m.Freed?.RemoveAll(i => i.Id == slot || i.Kind == DeepCoolMystiqueMediaItem.KindGif);
    }

    /// <summary>
    /// The screen id a picture goes up under, and whether the screen already holds it. The screen keeps one picture, as it
    /// keeps one GIF in <see cref="MystiqueGifSlot"/>, so each show replaces it in place. Before a slot is recorded a picture
    /// id the screen may hold is taken, listed ones first, then freed ones; which of several the screen shows is unknown,
    /// so <see cref="MystiquePicturesUnsure"/> reports it until a format.
    /// </summary>
    internal static (string Id, bool Held) MystiquePictureSlot(DeepCoolMystiqueSettings m, string itemId)
    {
        if (MystiqueMedia.IsId(m.PictureSlot))
        {
            return (m.PictureSlot, true);
        }
        var held = m.Media?.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture) ?? m.Freed?.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture);
        return held is not null ? (held.Id, true) : (itemId, false);
    }

    /// <summary>True while no picture slot is recorded and the screen may hold more than one picture id from earlier builds.</summary>
    internal static bool MystiquePicturesUnsure(DeepCoolMystiqueSettings m) =>
        !MystiqueMedia.IsId(m.PictureSlot)
        && (m.Media ?? new()).Concat(m.Freed ?? new()).Where(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture).Select(i => i.Id).Distinct().Count() > 1;

    /// <summary>
    /// Records what a finished or failed picture stream left in the slot: the shown item, or none when the stream failed.
    /// Freed picture ids are dropped: pictures never go up under them once a slot is recorded.
    /// </summary>
    internal static void SetMystiquePictureSlot(DeepCoolMystiqueSettings m, string slot, string shown)
    {
        m.GifSlotUnsure |= MystiquePicturesUnsure(m);
        m.PictureSlot = slot;
        m.ShownPicture = shown;
        m.Freed?.RemoveAll(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture);
    }

    /// <summary>Whether uploads stream half a turn round: while the screen holds gyroscope byte 1, which is both its out-of-box value and what the session sends for Gyroscope off.</summary>
    internal static bool MystiqueRotatesMedia(DeepCoolMystiqueSettings m) => !m.Gyroscope;

    /// <summary>Test seam: a media root for the current async flow only, so parallel tests never share it.</summary>
    internal static readonly AsyncLocal<string?> MystiqueMediaRootForTests = new();

    private static string MystiqueMediaDir() => MystiqueMediaRootForTests.Value ?? MediaLibrary.DeviceStoreDir("deepcool-mystique");

    private static string MystiqueSourcePath(string id, string ext) => Path.Combine(MystiqueMediaDir(), "sources", id + ext);

    internal static bool MystiqueSourceExists(DeepCoolMystiqueMediaItem item) =>
        IsKeepableExt(item.Ext) && MystiqueMedia.IsId(item.Id) && File.Exists(MystiqueSourcePath(item.Id, item.Ext));

    // Lowercase letters and digits only: the extension becomes part of a file name.
    private static bool IsKeepableExt(string ext) =>
        ext.Length is >= 2 and <= 6 && ext[0] == '.' && ext.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789") < 0;

    private static IResult MystiqueMediaFailure(string msg, int status = 200) => Results.Json(
        new DeepCoolMystiqueMediaResponse { Error = true, Msg = msg }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse, statusCode: status);

    private static IResult MystiqueNotResolved(DeepCoolMystiqueMediaItem item) => MystiqueSourceExists(item)
        ? MystiqueMediaFailure("The item could not be converted")
        : Results.Json(new DeepCoolMystiqueMediaResponse { Error = true, NotKept = true, Msg = "The original is not kept, so it cannot be sent again" },
            AppJsonContext.Default.DeepCoolMystiqueMediaResponse);

    /// <summary>
    /// Puts a GIF up where the screen holds one: a format, the shown picture again, the GIF under its own id last, and one
    /// source command after it, so the screen never stores two GIFs; overwriting a large one hung a screen until its power
    /// was removed. Only screen-side state is reset, never the library. A slot is recorded only for an item that goes up,
    /// with nothing shown until its stream lands, so the next show retries. Returns 1 once the GIF landed, 0 when it did
    /// not, or -1 when the format was not taken.
    /// </summary>
    private static async Task<int> StreamMystiqueFreshGifAsync(
        IConfigStore store, MystiqueWorker worker, MystiqueMediaPackage gif, string gifId, int startChoices, CancellationToken ct)
    {
        var m = new DeepCoolMystiqueSettings();
        store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
        var (picture, frames) = await ResolveMystiquePictureToKeepAsync(m, ct);
        var packages = frames is null ? new List<MystiqueMediaPackage> { gif } : new List<MystiqueMediaPackage> { frames, gif };
        worker.ReportProgress(MystiqueWorker.ProgressSending, 0, 1);
        bool formatted = await worker.FormatAsync(ct);
        store.Update(s =>
        {
            var d = s.Devices.DeepCoolMystique;
            d.ShownGif = "";
            d.ShownPicture = "";
            if (formatted)
            {
                d.GifSlot = gifId;
                d.PictureSlot = picture?.Id ?? "";
                d.Freed = new();
                d.GifSlotUnsure = false;
                d.QuietSinceFormat = false;
            }
        });
        if (!formatted)
        {
            return -1;
        }
        Nexus.Service.Platform.ServiceLog.Info($"[deepcool-mystique] "
            + (picture is null ? "" : $"picture {picture.Id} into slot {picture.Id}, then ")
            + $"gif {gifId} into slot {gifId}, after a format");
        // The GIF goes last so its source command follows it whenever it lands.
        MystiqueSourceAfter? SourceAfterStream()
        {
            var after = new MystiqueSourceAfter(DeepCoolMystiqueSettings.SourceGif, true);
            store.Update(s =>
            {
                var d = s.Devices.DeepCoolMystique;
                var chosen = MystiqueSourceAfterUpload(d, isGif: true, inPlace: false, MystiqueSourceChoices != startChoices);
                after = new MystiqueSourceAfter(chosen?.Source ?? d.MediaSource, true);
            });
            return after;
        }
        int streamed = await worker.UploadAsync(packages, SourceAfterStream, ct);
        if (picture is not null && streamed >= 1)
        {
            store.Update(s => SetMystiquePictureSlot(s.Devices.DeepCoolMystique, picture.Id, picture.Id));
        }
        if (streamed < packages.Count)
        {
            return 0;
        }
        store.Update(s => SetMystiqueGifSlot(s.Devices.DeepCoolMystique, gifId, gifId));
        return 1;
    }

    /// <summary>The picture to show again after a format, the shown one first, and its frames; none when no listed picture can be sent.</summary>
    private static async Task<(DeepCoolMystiqueMediaItem? Item, MystiqueMediaPackage? Frames)> ResolveMystiquePictureToKeepAsync(
        DeepCoolMystiqueSettings m, CancellationToken ct)
    {
        var pictures = m.Media.FindAll(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture);
        foreach (var picture in pictures.Where(i => i.Id == m.ShownPicture).Concat(pictures.Where(i => i.Id != m.ShownPicture)))
        {
            if (await ResolveMystiqueMediaAsync(picture, picture.Id, MystiqueRotatesMedia(m), null, ct) is { } frames)
            {
                return (picture, frames);
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Streams <paramref name="package"/> into the <paramref name="kind"/> slot with the source command after it, and records
    /// what the slot holds: <paramref name="itemId"/> once it lands, nothing once the stream fails, and no change when the
    /// screen never took the turn. Returns the worker's count. A held GIF slot needs no source command while GIFs play,
    /// as the screen's own software sends none; a picture always selects the pictures.
    /// </summary>
    private static async Task<int> StreamMystiqueSlotAsync(
        IConfigStore store, MystiqueWorker worker, string kind, string slot, bool held, MystiqueMediaPackage package, string itemId,
        int startChoices, CancellationToken ct)
    {
        bool isGif = kind == DeepCoolMystiqueMediaItem.KindGif;
        // Once streaming starts this waits for the real result, so an item the screen took is always recorded.
        MystiqueSourceAfter? SourceAfterStream()
        {
            MystiqueSourceAfter? after = null;
            store.Update(s => after = MystiqueSourceAfterUpload(s.Devices.DeepCoolMystique, isGif, isGif && held, MystiqueSourceChoices != startChoices));
            return after;
        }
        Nexus.Service.Platform.ServiceLog.Info($"[deepcool-mystique] {kind} {itemId} into slot {slot}{(held ? "" : ", new")}");
        worker.ReportProgress(MystiqueWorker.ProgressSending, 0, 1);
        int streamed = await worker.UploadAsync(new[] { package }, SourceAfterStream, ct);
        if (streamed >= 0)
        {
            string shown = streamed == 1 ? itemId : "";
            store.Update(s =>
            {
                if (isGif)
                {
                    SetMystiqueGifSlot(s.Devices.DeepCoolMystique, slot, shown);
                }
                else
                {
                    SetMystiquePictureSlot(s.Devices.DeepCoolMystique, slot, shown);
                }
            });
        }
        worker.RequestApply();
        return streamed;
    }

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
                string? id = MystiqueUploadId(store, kind, source, form["replace"].ToString());
                if (id is null)
                {
                    return MystiqueMediaFailure(isGif
                        ? "Nexus keeps its maximum number of GIFs; replace or remove one first."
                        : "Nexus keeps its maximum number of pictures; replace or remove one first.");
                }
                string slot = id;
                bool held = false;
                bool rotate = false;
                int startChoices = 0;
                store.Read(s =>
                {
                    var m = s.Devices.DeepCoolMystique.Copy();
                    (slot, held) = isGif ? MystiqueGifSlot(m, id) : MystiquePictureSlot(m, id);
                    rotate = MystiqueRotatesMedia(m);
                    startChoices = MystiqueSourceChoices;
                });
                worker.ReportProgress(MystiqueWorker.ProgressConverting, 0, 1);
                // A GIF never overwrites one the screen holds; it goes up fresh under its own id.
                bool fresh = isGif && held;
                if (fresh)
                {
                    slot = id;
                }
                var encoded = isGif
                    ? await MystiqueMediaEncoder.EncodeGifAsync(source, ext, crop, slot, rotate, budget.Token,
                        (done, total) => worker.ReportProgress(MystiqueWorker.ProgressConverting, done, total))
                    : await MystiqueMediaEncoder.EncodePictureAsync(source, ext, crop, slot, rotate, budget.Token);
                if (!encoded.Ok)
                {
                    return MystiqueMediaFailure(encoded.Error ?? "Encode failed");
                }
                var package = encoded.Package!;
                int landed = fresh
                    ? await StreamMystiqueFreshGifAsync(store, worker, package, id, startChoices, budget.Token)
                    : await StreamMystiqueSlotAsync(store, worker, kind, slot, held, package, id, startChoices, budget.Token);
                if (landed != 1)
                {
                    return MystiqueMediaFailure("The screen did not take the upload");
                }
                SaveMystiqueThumbnail(id, encoded.Thumbnail!);
                string keptExt = await KeepMystiqueSource(id, ext, source, isGif);
                SaveMystiqueStream(id, isGif ? MystiqueMedia.SaveGif(package, rotate) : MystiqueMedia.SavePicture(package, rotate));
                var item = new DeepCoolMystiqueMediaItem
                {
                    Kind = kind, Id = id, Name = TruncateName(Path.GetFileNameWithoutExtension(file.FileName), MystiqueNameMax), Ext = keptExt,
                    Crop = crop is null ? "" : form["crop"].ToString(),
                };
                store.Update(s =>
                {
                    var media = s.Devices.DeepCoolMystique.Media ??= new();
                    int at = media.FindIndex(i => i.Id == id);
                    if (at >= 0)
                    {
                        media[at] = item;
                    }
                    else
                    {
                        media.Add(item);
                    }
                });
                return Results.Json(new DeepCoolMystiqueMediaResponse { Id = id }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse);
            }
            catch (OperationCanceledException)
            {
                return MystiqueMediaFailure("The upload took too long");
            }
            finally
            {
                worker.ClearProgress();
                worker.UploadGate.Release();
            }
        }).DisableAntiforgery();

        // Selecting an item shows it: its kept frames go up under the one slot of its kind, as a GIF does in the screen's own software.
        app.MapPost("/devices/deepcool-mystique/media/{id}/show", async (string id, HttpContext ctx, IConfigStore store, MystiqueWorker worker) =>
        {
            if (!MystiqueMedia.IsId(id))
            {
                return Results.NotFound();
            }
            if (!worker.IsConnected)
            {
                return MystiqueMediaFailure("The screen is not connected");
            }
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
                var m = new DeepCoolMystiqueSettings();
                store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
                var item = m.Media.Find(i => i.Id == id);
                if (item is null)
                {
                    return Results.NotFound();
                }
                bool isGif = item.Kind == DeepCoolMystiqueMediaItem.KindGif;
                var shown = Results.Json(new DeepCoolMystiqueMediaResponse { Id = id }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse);
                if ((isGif ? m.ShownGif : m.ShownPicture) == id && MystiqueMedia.IsId(isGif ? m.GifSlot : m.PictureSlot))
                {
                    store.Update(s =>
                    {
                        var d = s.Devices.DeepCoolMystique;
                        d.MediaSource = isGif ? DeepCoolMystiqueSettings.SourceGif : DeepCoolMystiqueSettings.SourceImages;
                        d.SourceEdited = true;
                    });
                    worker.RequestApply();
                    return shown;
                }
                var (slot, held) = isGif ? MystiqueGifSlot(m, id) : MystiquePictureSlot(m, id);
                bool fresh = isGif && held;
                if (fresh)
                {
                    slot = id;
                }
                var package = await ResolveMystiqueMediaAsync(item, slot, MystiqueRotatesMedia(m),
                    (done, total) => worker.ReportProgress(MystiqueWorker.ProgressConverting, done, total), budget.Token);
                if (package is null)
                {
                    return MystiqueNotResolved(item);
                }
                int landed = fresh
                    ? await StreamMystiqueFreshGifAsync(store, worker, package, id, MystiqueSourceChoices, budget.Token)
                    : await StreamMystiqueSlotAsync(store, worker, item.Kind, slot, held, package, id, MystiqueSourceChoices, budget.Token);
                return landed == 1
                    ? shown
                    : MystiqueMediaFailure(isGif ? "The screen did not take the GIF" : "The screen did not take the picture");
            }
            catch (OperationCanceledException)
            {
                return MystiqueMediaFailure("The upload took too long");
            }
            finally
            {
                worker.ClearProgress();
                worker.UploadGate.Release();
            }
        });

        app.MapGet("/devices/deepcool-mystique/media/progress", (MystiqueWorker worker) =>
        {
            var (phase, percent) = worker.Progress;
            return Results.Json(new DeepCoolMystiqueProgressResponse { Phase = phase, Percent = percent }, AppJsonContext.Default.DeepCoolMystiqueProgressResponse);
        });

        app.MapDelete("/devices/deepcool-mystique/media/{id}", async (string id, HttpContext ctx, IConfigStore store, MystiqueWorker worker) =>
        {
            if (!MystiqueMedia.IsId(id))
            {
                return Results.NotFound();
            }
            int removed = await ForgetMystiqueMedia(store, worker, i => i.Id == id, ctx.RequestAborted);
            return removed < 0 ? MystiqueMediaFailure("The screen is busy")
                : removed > 0 ? Results.Json(new DeepCoolMystiqueMediaResponse { Id = id }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse)
                : Results.NotFound();
        });

        // The screen's own format command, so unlike a single removal this erases the screen too.
        app.MapDelete("/devices/deepcool-mystique/media", async (HttpContext ctx, IConfigStore store, MystiqueWorker worker) =>
        {
            if (!worker.IsConnected)
            {
                return MystiqueMediaFailure("The screen is not connected");
            }
            if (!await WaitMystiqueGateAsync(worker, ctx.RequestAborted))
            {
                return MystiqueMediaFailure("The screen is busy");
            }
            try
            {
                if (!await worker.FormatAsync(ctx.RequestAborted))
                {
                    return MystiqueMediaFailure("The screen did not take the format command");
                }
                var gone = new List<DeepCoolMystiqueMediaItem>();
                store.Update(s =>
                {
                    var m = s.Devices.DeepCoolMystique;
                    gone = m.Media ?? new();
                    m.Media = new();
                    m.Freed = new();
                    m.GifSlot = "";
                    m.ShownGif = "";
                    m.PictureSlot = "";
                    m.ShownPicture = "";
                    m.GifSlotUnsure = false;
                    m.QuietSinceFormat = true;
                });
                gone.ForEach(DeleteMystiqueFiles);
                DeleteMystiqueStreamLeftovers();
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
    /// The id an upload is listed under, null when the kind's library is full: the item it replaces, the one its source
    /// already went up as, else its source's id.
    /// </summary>
    internal static string? MystiqueUploadId(IConfigStore store, string kind, byte[] source, string replace)
    {
        var mystique = new DeepCoolMystiqueSettings();
        store.Read(s => mystique = s.Devices.DeepCoolMystique.Copy());
        string sourceId = MystiqueMedia.IdOf(kind == DeepCoolMystiqueMediaItem.KindGif ? MystiqueMedia.KindGif : MystiqueMedia.KindPicture, source);
        var items = mystique.Media.FindAll(i => i.Kind == kind);
        var existing = items.Find(i => i.Id == replace) ?? items.Find(i => i.Id == sourceId);
        if (existing is not null)
        {
            return existing.Id;
        }
        return items.Count >= (kind == DeepCoolMystiqueMediaItem.KindGif ? MystiqueMedia.MaxGifs : MystiqueMedia.MaxPictures) ? null : sourceId;
    }

    /// <summary>Takes the upload gate within the upload budget; false when it stayed held.</summary>
    private static async Task<bool> WaitMystiqueGateAsync(MystiqueWorker worker, CancellationToken aborted)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        budget.CancelAfter(MystiqueUploadBudget);
        try
        {
            await worker.UploadGate.WaitAsync(budget.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Drops the matching items from the list with their files and returns how many, or -1 when the upload gate stayed
    /// held. Nexus-side only: the screen has no single-item delete, so it keeps showing a removed item until the next
    /// show replaces it.
    /// </summary>
    private static async Task<int> ForgetMystiqueMedia(IConfigStore store, MystiqueWorker worker, Predicate<DeepCoolMystiqueMediaItem> match, CancellationToken ct)
    {
        if (!await WaitMystiqueGateAsync(worker, ct))
        {
            return -1;
        }
        try
        {
            var gone = new List<DeepCoolMystiqueMediaItem>();
            store.Update(s =>
            {
                var m = s.Devices.DeepCoolMystique;
                m.Media ??= new();
                bool unsure = MystiquePicturesUnsure(m);
                gone = m.Media.FindAll(match);
                m.Media.RemoveAll(match);
                // The removed picture may still be on the screen, so fewer listed ids does not make the slot sure.
                m.GifSlotUnsure |= unsure && gone.Count > 0;
                if (gone.Exists(i => i.Id == m.ShownGif))
                {
                    m.ShownGif = "";
                }
                if (gone.Exists(i => i.Id == m.ShownPicture))
                {
                    m.ShownPicture = "";
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
        DeleteMystiqueFile(MystiqueStreamPath(item.Id));
        DeleteMystiqueFile(MystiqueStreamPath(item.Id) + ".tmp");
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

    /// <summary>
    /// Keeps the source for a later re-crop, as is up to <see cref="MystiqueKeepMaxBytes"/> and otherwise as a
    /// reduced copy within that bound. Returns the kept extension, empty when nothing is kept, in which case
    /// any source an earlier upload under this id kept is deleted too.
    /// </summary>
    internal static async Task<string> KeepMystiqueSource(string id, string ext, byte[] source, bool isGif)
    {
        string keptExt = ext.ToLowerInvariant();
        if (source.Length > MystiqueKeepMaxBytes)
        {
            var reduced = await MystiqueMediaEncoder.ReduceForKeepingAsync(source, ext, isGif, MystiqueKeepMaxBytes);
            (source, keptExt) = reduced is { } r ? (r.Bytes, r.Ext) : (Array.Empty<byte>(), "");
        }
        if (IsKeepableExt(keptExt) && SaveMystiqueSource(id, keptExt, source))
        {
            return keptExt;
        }
        DeleteMystiqueSources(id);
        return "";
    }

    private static bool SaveMystiqueSource(string id, string ext, byte[] source)
    {
        try
        {
            var path = MystiqueSourcePath(id, ext);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            DeleteMystiqueSources(id);
            File.WriteAllBytes(path, source);
            return true;
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[deepcool-mystique] source not kept: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void DeleteMystiqueSources(string id)
    {
        var dir = Path.Combine(MystiqueMediaDir(), "sources");
        if (!MystiqueMedia.IsId(id) || !Directory.Exists(dir))
        {
            return;
        }
        foreach (var old in Directory.GetFiles(dir, id + ".*"))
        {
            DeleteMystiqueFile(old);
        }
    }

    private static string MystiqueStreamDir() => Path.Combine(MystiqueMediaDir(), "streams");

    private static string MystiqueStreamPath(string id) => Path.Combine(MystiqueStreamDir(), id + ".bin");

    /// <summary>Deletes the temp files of stream writes a crash cut short.</summary>
    private static void DeleteMystiqueStreamLeftovers()
    {
        var dir = MystiqueStreamDir();
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (var temp in Directory.GetFiles(dir, "*.tmp"))
        {
            DeleteMystiqueFile(temp);
        }
    }

    /// <summary>Keeps an item's encoded frames, so showing it again streams them without a new encode.</summary>
    private static void SaveMystiqueStream(string id, byte[] saved)
    {
        try
        {
            var path = MystiqueStreamPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, saved);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[deepcool-mystique] encoded frames not kept: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The picture or GIF to stream for <paramref name="item"/> under <paramref name="screenId"/>: its kept frames when they load
    /// and match <paramref name="rotate"/>, else a new encode of its kept source; null when neither is kept or the encode fails.
    /// </summary>
    internal static async Task<MystiqueMediaPackage?> ResolveMystiqueMediaAsync(
        DeepCoolMystiqueMediaItem item, string screenId, bool rotate, Action<int, int>? converting, CancellationToken ct)
    {
        var package = LoadMystiqueStream(item, screenId, out bool rotated);
        return package is not null && rotated == rotate
            ? package
            : await ReencodeMystiqueMediaAsync(item, screenId, rotate, converting, ct);
    }

    /// <summary>The frames kept for <paramref name="item"/>, rebuilt to go up under <paramref name="screenId"/>; null when none are kept.</summary>
    private static MystiqueMediaPackage? LoadMystiqueStream(DeepCoolMystiqueMediaItem item, string screenId, out bool rotated)
    {
        rotated = false;
        try
        {
            var path = MystiqueStreamPath(item.Id);
            if (!File.Exists(path))
            {
                return null;
            }
            var saved = File.ReadAllBytes(path);
            return item.Kind == DeepCoolMystiqueMediaItem.KindGif
                ? MystiqueMedia.LoadGif(saved, screenId, out rotated)
                : MystiqueMedia.LoadPicture(saved, screenId, out rotated);
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[deepcool-mystique] kept frames not read: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Encodes an item again from its kept source with its recorded crop and keeps the frames; null when no source is kept or the encode fails.</summary>
    private static async Task<MystiqueMediaPackage?> ReencodeMystiqueMediaAsync(
        DeepCoolMystiqueMediaItem item, string screenId, bool rotate, Action<int, int>? converting, CancellationToken ct)
    {
        if (!MystiqueSourceExists(item))
        {
            return null;
        }
        CropRect.TryParse(item.Crop, out var parsed);
        var source = await File.ReadAllBytesAsync(MystiqueSourcePath(item.Id, item.Ext), ct);
        converting?.Invoke(0, 1);
        CropRect? crop = parsed.W > 0 ? parsed : null;
        bool isGif = item.Kind == DeepCoolMystiqueMediaItem.KindGif;
        var encoded = isGif
            ? await MystiqueMediaEncoder.EncodeGifAsync(source, item.Ext, crop, screenId, rotate, ct, converting)
            : await MystiqueMediaEncoder.EncodePictureAsync(source, item.Ext, crop, screenId, rotate, ct);
        if (!encoded.Ok)
        {
            return null;
        }
        SaveMystiqueStream(item.Id, isGif ? MystiqueMedia.SaveGif(encoded.Package!, rotate) : MystiqueMedia.SavePicture(encoded.Package!, rotate));
        return encoded.Package;
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
