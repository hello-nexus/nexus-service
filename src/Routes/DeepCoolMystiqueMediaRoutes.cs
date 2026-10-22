using System;
using System.Collections.Generic;
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

    // Bounds a kept source, so full slots of uploads up to MediaImporter.MaxFileSize never pile up gigabytes.
    internal const int MystiqueKeepMaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Saves the source a finished stream leaves the screen on and returns the one to send (null for none). A new
    /// item selects its kind, as the screen's own software does, and so does a GIF sent while the pictures play,
    /// unless the user chose a source during the stream. Otherwise an upload under an id the screen holds sends
    /// none: the recorded edits sent no source command after the stream.
    /// </summary>
    internal static string? MystiqueSourceAfterUpload(DeepCoolMystiqueSettings m, bool isGif, bool inPlace, bool chosenDuringStream)
    {
        bool gifNotPlaying = isGif && (!m.SourceEdited || m.MediaSource != DeepCoolMystiqueSettings.SourceGif);
        if (!chosenDuringStream && (!inPlace || gifNotPlaying))
        {
            m.MediaSource = isGif ? DeepCoolMystiqueSettings.SourceGif : DeepCoolMystiqueSettings.SourceImages;
            m.SourceEdited = true;
            return m.MediaSource;
        }
        if (inPlace)
        {
            return null;
        }
        return m.SourceEdited ? m.MediaSource : null;
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

    /// <summary>Puts a failed upload's id in <see cref="DeepCoolMystiqueSettings.Freed"/>, so the next upload of its kind overwrites whatever part of it the screen kept.</summary>
    internal static void FreeFailedMystiqueId(IConfigStore store, string kind, string id) =>
        store.Update(s =>
        {
            var m = s.Devices.DeepCoolMystique;
            m.Media ??= new();
            m.Freed ??= new();
            if (!m.Media.Exists(i => i.Id == id) && !m.Freed.Exists(i => i.Id == id))
            {
                m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = kind, Id = id });
            }
        });

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
                string screenId = id;
                bool inPlace = false;
                bool rotate = false;
                int startChoices = 0;
                store.Read(s =>
                {
                    var m = s.Devices.DeepCoolMystique.Copy();
                    (screenId, inPlace) = isGif ? MystiqueGifSlot(m, id) : (id, m.Media.Exists(i => i.Id == id));
                    rotate = MystiqueRotatesMedia(m);
                    startChoices = MystiqueSourceChoices;
                });
                worker.ReportProgress(MystiqueWorker.ProgressConverting, 0, 1);
                var encoded = isGif
                    ? await MystiqueMediaEncoder.EncodeGifAsync(source, ext, crop, screenId, rotate, budget.Token,
                        (done, total) => worker.ReportProgress(MystiqueWorker.ProgressConverting, done, total))
                    : await MystiqueMediaEncoder.EncodePictureAsync(source, ext, crop, screenId, rotate, budget.Token);
                if (!encoded.Ok)
                {
                    return MystiqueMediaFailure(encoded.Error ?? "Encode failed");
                }
                var package = encoded.Package!;
                // Once streaming starts this waits for the real result, so an item the screen took is always recorded.
                string? SourceAfterStream()
                {
                    string? send = null;
                    store.Update(s => send = MystiqueSourceAfterUpload(s.Devices.DeepCoolMystique, isGif, inPlace, MystiqueSourceChoices != startChoices));
                    return send;
                }
                worker.ReportProgress(MystiqueWorker.ProgressSending, 0, 1);
                if (!await worker.UploadAsync(package, SourceAfterStream, budget.Token))
                {
                    if (isGif)
                    {
                        store.Update(s => SetMystiqueGifSlot(s.Devices.DeepCoolMystique, screenId, ""));
                    }
                    else
                    {
                        FreeFailedMystiqueId(store, kind, id);
                    }
                    return MystiqueMediaFailure("The screen did not take the upload");
                }
                SaveMystiqueThumbnail(id, encoded.Thumbnail!);
                string keptExt = await KeepMystiqueSource(id, ext, source, isGif);
                if (isGif)
                {
                    SaveMystiqueStream(id, MystiqueMedia.SaveGif(package, rotate));
                }
                string name = TruncateName(Path.GetFileNameWithoutExtension(file.FileName), MystiqueNameMax);
                store.Update(s =>
                {
                    var m = s.Devices.DeepCoolMystique;
                    m.Media ??= new();
                    m.Freed ??= new();
                    m.Freed.RemoveAll(i => i.Id == screenId);
                    var item = new DeepCoolMystiqueMediaItem { Kind = kind, Id = id, Name = name, Ext = keptExt, Crop = crop is null ? "" : form["crop"].ToString() };
                    int at = m.Media.FindIndex(i => i.Id == id);
                    if (at >= 0)
                    {
                        m.Media[at] = item;
                    }
                    else
                    {
                        m.Media.Add(item);
                    }
                    if (isGif)
                    {
                        SetMystiqueGifSlot(m, screenId, id);
                    }
                });
                worker.RequestApply();
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

        // Selecting a GIF shows it, as in the screen's own software: its kept frames go up under the slot the screen plays.
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
                if (!m.Media.Exists(i => i.Id == id && i.Kind == DeepCoolMystiqueMediaItem.KindGif))
                {
                    return Results.NotFound();
                }
                if (m.ShownGif == id && MystiqueMedia.IsId(m.GifSlot))
                {
                    store.Update(s =>
                    {
                        var d = s.Devices.DeepCoolMystique;
                        d.MediaSource = DeepCoolMystiqueSettings.SourceGif;
                        d.SourceEdited = true;
                    });
                    worker.RequestApply();
                    return Results.Json(new DeepCoolMystiqueMediaResponse { Id = id }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse);
                }
                var (slot, held) = MystiqueGifSlot(m, id);
                bool rotate = MystiqueRotatesMedia(m);
                var item = m.Media.Find(i => i.Id == id)!;
                var package = await ResolveMystiqueGifAsync(item, slot, rotate,
                    (done, total) => worker.ReportProgress(MystiqueWorker.ProgressConverting, done, total), budget.Token);
                if (package is null)
                {
                    return MystiqueSourceExists(item)
                        ? MystiqueMediaFailure("The GIF could not be converted")
                        : Results.Json(new DeepCoolMystiqueMediaResponse { Error = true, NotKept = true, Msg = "The GIF's original is not kept, so it cannot be sent again" },
                            AppJsonContext.Default.DeepCoolMystiqueMediaResponse);
                }
                int startChoices = MystiqueSourceChoices;
                string? SourceAfterStream()
                {
                    string? send = null;
                    store.Update(s => send = MystiqueSourceAfterUpload(s.Devices.DeepCoolMystique, true, held, MystiqueSourceChoices != startChoices));
                    return send;
                }
                worker.ReportProgress(MystiqueWorker.ProgressSending, 0, 1);
                if (!await worker.UploadAsync(package, SourceAfterStream, budget.Token))
                {
                    store.Update(s => SetMystiqueGifSlot(s.Devices.DeepCoolMystique, slot, ""));
                    return MystiqueMediaFailure("The screen did not take the GIF");
                }
                store.Update(s => SetMystiqueGifSlot(s.Devices.DeepCoolMystique, slot, id));
                worker.RequestApply();
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
                var gone = new List<DeepCoolMystiqueMediaItem>();
                store.Update(s =>
                {
                    var m = s.Devices.DeepCoolMystique;
                    gone = m.Media ?? new();
                    m.Media = new();
                    m.Freed = new();
                    m.GifSlot = "";
                    m.ShownGif = "";
                    m.GifSlotUnsure = false;
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
    /// The item's id, null when the kind's slots are full. The screen replaces an item uploaded under an id it
    /// holds, so a picture reuses the id it is replacing, the one its source already went up under, or a freed
    /// id the screen still holds, before taking a new slot. A GIF goes up under <see cref="MystiqueGifSlot"/>, so its
    /// id only names it here and a freed one is never reused.
    /// </summary>
    internal static string? MystiqueUploadId(IConfigStore store, string kind, byte[] source, string replace)
    {
        var mystique = new DeepCoolMystiqueSettings();
        store.Read(s => mystique = s.Devices.DeepCoolMystique.Copy());
        bool isGif = kind == DeepCoolMystiqueMediaItem.KindGif;
        string sourceId = MystiqueMedia.IdOf(isGif ? MystiqueMedia.KindGif : MystiqueMedia.KindPicture, source);
        var items = mystique.Media.FindAll(i => i.Kind == kind);
        var freed = isGif ? new List<DeepCoolMystiqueMediaItem>() : mystique.Freed.FindAll(i => i.Kind == kind);
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
    /// Drops the matching items from the list with their files, a picture into <see cref="DeepCoolMystiqueSettings.Freed"/>.
    /// Nexus-side only: the screen has no single-item delete, so it keeps each until an upload reuses its id.
    /// </summary>
    private static async Task<int> ForgetMystiqueMedia(IConfigStore store, MystiqueWorker worker, Predicate<DeepCoolMystiqueMediaItem> match, CancellationToken ct)
    {
        await worker.UploadGate.WaitAsync(ct);
        try
        {
            var gone = new List<DeepCoolMystiqueMediaItem>();
            store.Update(s =>
            {
                var m = s.Devices.DeepCoolMystique;
                m.Media ??= new();
                m.Freed ??= new();
                gone = m.Media.FindAll(match);
                m.Media.RemoveAll(match);
                foreach (var item in gone)
                {
                    if (item.Id == m.ShownGif)
                    {
                        m.ShownGif = "";
                    }
                    if (item.Kind == DeepCoolMystiqueMediaItem.KindPicture && !m.Freed.Exists(i => i.Id == item.Id))
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

    /// <summary>Keeps a GIF's encoded frames, so showing it again streams them without a new encode.</summary>
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
            Nexus.Service.Platform.ServiceLog.Warn($"[deepcool-mystique] GIF frames not kept: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The GIF to stream for <paramref name="item"/> under <paramref name="screenId"/>: its kept frames when they load and match
    /// <paramref name="rotate"/>, else a new encode of its kept source; null when neither is kept.
    /// </summary>
    internal static async Task<MystiqueMediaPackage?> ResolveMystiqueGifAsync(
        DeepCoolMystiqueMediaItem item, string screenId, bool rotate, Action<int, int>? converting, CancellationToken ct)
    {
        var package = LoadMystiqueStream(item.Id, screenId, out bool rotated);
        return package is not null && rotated == rotate
            ? package
            : await ReencodeMystiqueGifAsync(item, screenId, rotate, converting, ct);
    }

    /// <summary>The GIF kept under <paramref name="id"/>, rebuilt to go up under <paramref name="screenId"/>; null when none is kept.</summary>
    private static MystiqueMediaPackage? LoadMystiqueStream(string id, string screenId, out bool rotated)
    {
        rotated = false;
        try
        {
            var path = MystiqueStreamPath(id);
            return File.Exists(path) ? MystiqueMedia.LoadGif(File.ReadAllBytes(path), screenId, out rotated) : null;
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[deepcool-mystique] kept GIF frames not read: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Encodes a GIF again from its kept source with its recorded crop and keeps the frames; null when no source is kept or the encode fails.</summary>
    private static async Task<MystiqueMediaPackage?> ReencodeMystiqueGifAsync(
        DeepCoolMystiqueMediaItem item, string screenId, bool rotate, Action<int, int>? converting, CancellationToken ct)
    {
        if (!MystiqueSourceExists(item))
        {
            return null;
        }
        CropRect.TryParse(item.Crop, out var parsed);
        var source = await File.ReadAllBytesAsync(MystiqueSourcePath(item.Id, item.Ext), ct);
        converting?.Invoke(0, 1);
        var encoded = await MystiqueMediaEncoder.EncodeGifAsync(source, item.Ext, parsed.W > 0 ? parsed : null, screenId, rotate, ct, converting);
        if (!encoded.Ok)
        {
            return null;
        }
        SaveMystiqueStream(item.Id, MystiqueMedia.SaveGif(encoded.Package!, rotate));
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
