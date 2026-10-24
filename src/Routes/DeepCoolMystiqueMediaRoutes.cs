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
    /// Saves the source a finished stream leaves the screen on and returns it, null for none. A new item selects its
    /// kind, as the screen's own software does, and so does a GIF sent while the pictures play, unless the user chose
    /// a source during the stream. The first new picture after a format selects pictures without sending the command
    /// (<see cref="DeepCoolMystiqueSettings.QuietSinceFormat"/>). An upload under an id the screen holds selects nothing,
    /// as the recorded edits sent no source command after the stream.
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

    /// <summary>The id the screen holds an item under: a picture's moves when a click swaps it into the first place.</summary>
    internal static string MystiqueScreenId(DeepCoolMystiqueMediaItem item) => MystiqueMedia.IsId(item.ScreenId) ? item.ScreenId : item.Id;

    /// <summary>The lowest stored picture id by ordinal order, where the screen starts the pictures and a click swaps the chosen one; null with no picture.</summary>
    internal static string? MystiquePictureSlot(DeepCoolMystiqueSettings m)
    {
        var order = MystiquePictureOrder(m);
        return order.Count > 0 ? order.Min(StringComparer.Ordinal) : null;
    }

    /// <summary>
    /// The picture screen ids the screen holds, oldest first by when it first stored each: the recorded order, which
    /// leaves out an id given to a picture the screen has not stored yet, else the list order taking each picture's own
    /// id while the screen still holds it, as a swap leaves it in place, with held ids outside it first.
    /// </summary>
    internal static List<string> MystiquePictureOrder(DeepCoolMystiqueSettings m)
    {
        var pictures = m.Media?.FindAll(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture) ?? new();
        var held = pictures.ConvertAll(MystiqueScreenId);
        held.AddRange(m.Freed?.FindAll(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture).ConvertAll(i => i.Id) ?? new());
        held = held.Distinct().ToList();
        if (m.PictureIds is { Count: > 0 })
        {
            return m.PictureIds.FindAll(held.Contains);
        }
        var order = pictures.ConvertAll(i => held.Contains(i.Id) ? i.Id : MystiqueScreenId(i)).Distinct().ToList();
        return held.FindAll(id => !order.Contains(id)).Concat(order).ToList();
    }

    /// <summary>Records the order, adding <paramref name="screenId"/> as the newest when the screen did not hold it.</summary>
    internal static void NoteMystiquePictureStored(DeepCoolMystiqueSettings m, string screenId)
    {
        var order = MystiquePictureOrder(m);
        if (!order.Contains(screenId))
        {
            order.Add(screenId);
        }
        m.PictureIds = order;
    }

    /// <summary>The id a show of <paramref name="item"/> streams into: <see cref="MystiquePictureSlot"/>, else the item's own screen id, which a stream then records as stored.</summary>
    internal static string MystiqueShowSlot(DeepCoolMystiqueSettings m, DeepCoolMystiqueMediaItem item) =>
        MystiquePictureSlot(m) ?? MystiqueScreenId(item);

    /// <summary>The listed picture whose whole copy holds <see cref="MystiquePictureSlot"/>, empty when none does.</summary>
    internal static string MystiqueShownPicture(DeepCoolMystiqueSettings m)
    {
        string? slot = MystiquePictureSlot(m);
        return m.Media?.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture && !i.Unsent && MystiqueScreenId(i) == slot)?.Id ?? "";
    }

    /// <summary>
    /// Records what a picture show left on the screen once <paramref name="streamed"/> of <paramref name="sent"/> finished:
    /// each picture under the id holding its newest whole copy, else under a broken one marked
    /// <see cref="DeepCoolMystiqueMediaItem.Unsent"/>, and every other id the show touched in <see cref="DeepCoolMystiqueSettings.Freed"/>.
    /// </summary>
    internal static void RecordMystiquePictureStreams(DeepCoolMystiqueSettings m, IReadOnlyList<(string ItemId, string ScreenId)> sent, int streamed)
    {
        m.Media ??= new();
        m.Freed ??= new();
        m.PictureIds = MystiquePictureOrder(m);
        var pictures = m.Media.FindAll(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture);
        // Screen id to the picture whose whole copy it holds, null for a broken copy.
        var screen = new Dictionary<string, string?>();
        foreach (var p in pictures)
        {
            screen[MystiqueScreenId(p)] = p.Unsent ? null : p.Id;
        }
        for (int i = 0; i < Math.Min(streamed + 1, sent.Count); i++)
        {
            screen[sent[i].ScreenId] = i < streamed ? sent[i].ItemId : null;
            // A landed stream stores its id, and a failed one may have stored part of it.
            if (!m.PictureIds.Contains(sent[i].ScreenId))
            {
                m.PictureIds.Add(sent[i].ScreenId);
            }
        }
        var olds = pictures.ConvertAll(MystiqueScreenId);
        var ids = new string?[pictures.Count];
        for (int n = 0; n < pictures.Count; n++)
        {
            for (int i = Math.Min(streamed, sent.Count) - 1; i >= 0 && ids[n] is null; i--)
            {
                if (sent[i].ItemId == pictures[n].Id && screen[sent[i].ScreenId] == pictures[n].Id)
                {
                    ids[n] = sent[i].ScreenId;
                }
            }
            ids[n] ??= screen.GetValueOrDefault(olds[n]) == pictures[n].Id ? olds[n] : null;
        }
        var claimed = new HashSet<string>(ids.OfType<string>());
        for (int n = 0; n < pictures.Count; n++)
        {
            var p = pictures[n];
            p.Unsent = ids[n] is null;
            if (ids[n] is null)
            {
                // A picture keeps its broken id unless a stream that landed took it, which only the next leg of a swap fills.
                string old = olds[n];
                bool taken = sent.Take(streamed).Any(x => x.ScreenId == old && x.ItemId != p.Id);
                string? failed = streamed < sent.Count && sent[streamed].ItemId == p.Id ? sent[streamed].ScreenId : null;
                ids[n] = new[] { taken ? failed : old, failed, old }.FirstOrDefault(c => c is not null && !claimed.Contains(c))
                    ?? m.Freed.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture && !claimed.Contains(i.Id))?.Id
                    ?? Guid.NewGuid().ToString("N");
                claimed.Add(ids[n]!);
            }
            p.ScreenId = ids[n] == p.Id ? "" : ids[n]!;
        }
        m.Freed.RemoveAll(i => claimed.Contains(i.Id));
        foreach (var id in screen.Keys)
        {
            if (!claimed.Contains(id) && !m.Freed.Exists(i => i.Id == id))
            {
                m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = DeepCoolMystiqueMediaItem.KindPicture, Id = id });
            }
        }
    }

    /// <summary>
    /// Puts a failed upload's screen id in <see cref="DeepCoolMystiqueSettings.Freed"/>, so the next upload of its kind
    /// overwrites whatever part of it the screen kept, or marks the listed picture it replaced <see cref="DeepCoolMystiqueMediaItem.Unsent"/>.
    /// </summary>
    internal static void FreeFailedMystiqueId(IConfigStore store, string kind, string id) =>
        store.Update(s =>
        {
            var m = s.Devices.DeepCoolMystique;
            m.Media ??= new();
            m.Freed ??= new();
            var held = m.Media.Find(i => MystiqueScreenId(i) == id);
            if (held is not null)
            {
                held.Unsent = held.Kind == DeepCoolMystiqueMediaItem.KindPicture;
            }
            else if (!m.Freed.Exists(i => i.Id == id))
            {
                // The screen may have stored part of it, and a show into an id it lacks stores it there anyway.
                if (kind == DeepCoolMystiqueMediaItem.KindPicture)
                {
                    NoteMystiquePictureStored(m, id);
                }
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

    private static IResult MystiqueNotResolved(DeepCoolMystiqueMediaItem item) => MystiqueSourceExists(item)
        ? MystiqueMediaFailure("The item could not be converted")
        : Results.Json(new DeepCoolMystiqueMediaResponse { Error = true, NotKept = true, Msg = "The original is not kept, so it cannot be sent again" },
            AppJsonContext.Default.DeepCoolMystiqueMediaResponse);

    /// <summary>
    /// Shows a picture by streaming it under <see cref="MystiquePictureSlot"/> and the picture that held that id under the
    /// clicked one's old id, then picking the pictures, which the screen starts from the first; no select command is
    /// known. Pictures marked <see cref="DeepCoolMystiqueMediaItem.Unsent"/> go again with them. A picture already in the
    /// first place with nothing to resend only picks the pictures again.
    /// </summary>
    private static async Task<IResult> ShowMystiquePictureAsync(
        IConfigStore store, MystiqueWorker worker, DeepCoolMystiqueSettings m, DeepCoolMystiqueMediaItem item, CancellationToken ct)
    {
        var order = MystiquePictureOrder(m);
        string slot = MystiqueShowSlot(m, item);
        Nexus.Service.Platform.ServiceLog.Info("[deepcool-mystique] picture ids oldest first: "
            + string.Join(", ", order.Select(id => $"{id}={m.Media.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture && MystiqueScreenId(i) == id)?.Id ?? "freed"}"))
            + $"; recorded {m.PictureIds.Count > 0}, lowest {(order.Count > 0 ? order.Min(StringComparer.Ordinal) : "none")}"
            + $", highest {(order.Count > 0 ? order.Max(StringComparer.Ordinal) : "none")}, slot {slot}");
        void Playing() => store.Update(s =>
        {
            var d = s.Devices.DeepCoolMystique;
            d.MediaSource = DeepCoolMystiqueSettings.SourceImages;
            d.SourceEdited = true;
            d.QuietSinceFormat = false;
            d.PictureIds = MystiquePictureOrder(d);
        });
        var plan = MystiquePicturePlan(m, item, slot);
        bool rotate = MystiqueRotatesMedia(m);
        void Converting(int done, int total) => worker.ReportProgress(MystiqueWorker.ProgressConverting, done, total);
        var packages = new List<MystiqueMediaPackage>();
        var sent = new List<(string ItemId, string ScreenId)>();
        foreach (var (target, screenId) in plan)
        {
            var package = await ResolveMystiqueMediaAsync(target, screenId, rotate, Converting, ct);
            if (package is null && target.Id == item.Id)
            {
                return MystiqueNotResolved(item);
            }
            if (package is null)
            {
                // Its screen copy is being overwritten or is already broken, and cannot be rebuilt.
                ForgetMystiquePicture(store, target.Id);
                continue;
            }
            packages.Add(package);
            sent.Add((target.Id, screenId));
        }
        var shown = Results.Json(new DeepCoolMystiqueMediaResponse { Id = item.Id }, AppJsonContext.Default.DeepCoolMystiqueMediaResponse);
        if (packages.Count == 0)
        {
            if (!await worker.SendSourceAsync(DeepCoolMystiqueSettings.SourceImages, ct))
            {
                return MystiqueMediaFailure("The screen did not take the source command");
            }
            Playing();
            return shown;
        }
        worker.ReportProgress(MystiqueWorker.ProgressSending, 0, 1);
        int streamed = await worker.UploadAsync(packages, () =>
        {
            Playing();
            return new MystiqueSourceAfter(DeepCoolMystiqueSettings.SourceImages, true);
        }, ct);
        store.Update(s => RecordMystiquePictureStreams(s.Devices.DeepCoolMystique, sent, streamed));
        worker.RequestApply();
        return streamed == packages.Count ? shown : MystiqueMediaFailure("The screen did not take the picture");
    }

    /// <summary>
    /// The streams that show <paramref name="item"/> from <paramref name="slot"/>: it into the slot when it is elsewhere or
    /// unsent, the picture there into its old id, then every other unsent picture under an id no other stream takes.
    /// </summary>
    internal static List<(DeepCoolMystiqueMediaItem Item, string ScreenId)> MystiquePicturePlan(
        DeepCoolMystiqueSettings m, DeepCoolMystiqueMediaItem item, string slot)
    {
        string from = MystiqueScreenId(item);
        var plan = new List<(DeepCoolMystiqueMediaItem Item, string ScreenId)>();
        if (from != slot || item.Unsent)
        {
            plan.Add((item, slot));
        }
        var displaced = from == slot
            ? null
            : m.Media.Find(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture && i.Id != item.Id && MystiqueScreenId(i) == slot);
        if (displaced is not null)
        {
            plan.Add((displaced, from));
        }
        foreach (var unsent in m.Media.FindAll(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture && i.Unsent))
        {
            string id = MystiqueScreenId(unsent);
            if (!plan.Exists(p => p.Item.Id == unsent.Id || p.ScreenId == id))
            {
                plan.Add((unsent, id));
            }
        }
        return plan;
    }

    /// <summary>Drops a picture from the list with its files, its screen id into <see cref="DeepCoolMystiqueSettings.Freed"/>; the caller holds the upload gate.</summary>
    private static void ForgetMystiquePicture(IConfigStore store, string id)
    {
        DeepCoolMystiqueMediaItem? gone = null;
        store.Update(s =>
        {
            var m = s.Devices.DeepCoolMystique;
            m.Media ??= new();
            m.Freed ??= new();
            m.PictureIds = MystiquePictureOrder(m);
            gone = m.Media.Find(i => i.Id == id);
            if (gone is null)
            {
                return;
            }
            m.Media.Remove(gone);
            if (!m.Freed.Exists(i => i.Id == MystiqueScreenId(gone)))
            {
                m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = DeepCoolMystiqueMediaItem.KindPicture, Id = MystiqueScreenId(gone) });
            }
        });
        if (gone is not null)
        {
            DeleteMystiqueFiles(gone);
        }
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
                var target = MystiqueUploadId(store, kind, source, form["replace"].ToString());
                if (target is null)
                {
                    return MystiqueMediaFailure(isGif
                        ? "The screen holds its maximum number of GIFs; replace or remove one first."
                        : "The screen holds its maximum number of pictures; replace or remove one first.");
                }
                var (id, pictureScreenId) = target.Value;
                string screenId = id;
                bool inPlace = false;
                bool rotate = false;
                int startChoices = 0;
                store.Read(s =>
                {
                    var m = s.Devices.DeepCoolMystique.Copy();
                    (screenId, inPlace) = isGif ? MystiqueGifSlot(m, id) : (pictureScreenId, m.Media.Exists(i => i.Id == id));
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
                MystiqueSourceAfter? SourceAfterStream()
                {
                    MystiqueSourceAfter? after = null;
                    store.Update(s => after = MystiqueSourceAfterUpload(s.Devices.DeepCoolMystique, isGif, inPlace, MystiqueSourceChoices != startChoices));
                    return after;
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
                        FreeFailedMystiqueId(store, kind, screenId);
                    }
                    return MystiqueMediaFailure("The screen did not take the upload");
                }
                SaveMystiqueThumbnail(id, encoded.Thumbnail!);
                string keptExt = await KeepMystiqueSource(id, ext, source, isGif);
                SaveMystiqueStream(id, isGif ? MystiqueMedia.SaveGif(package, rotate) : MystiqueMedia.SavePicture(package, rotate));
                string name = TruncateName(Path.GetFileNameWithoutExtension(file.FileName), MystiqueNameMax);
                store.Update(s =>
                {
                    var m = s.Devices.DeepCoolMystique;
                    m.Media ??= new();
                    m.Freed ??= new();
                    if (!isGif)
                    {
                        NoteMystiquePictureStored(m, screenId);
                    }
                    m.Freed.RemoveAll(i => i.Id == screenId);
                    var item = new DeepCoolMystiqueMediaItem
                    {
                        Kind = kind, Id = id, ScreenId = isGif || screenId == id ? "" : screenId, Name = name, Ext = keptExt,
                        Crop = crop is null ? "" : form["crop"].ToString(),
                    };
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

        // Selecting an item shows it: a GIF's kept frames go up under the slot the screen plays, as in the screen's own
        // software, and a picture is swapped into the place the pictures start from.
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
                if (item.Kind == DeepCoolMystiqueMediaItem.KindPicture)
                {
                    return await ShowMystiquePictureAsync(store, worker, m, item, budget.Token);
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
                var package = await ResolveMystiqueMediaAsync(item, slot, rotate,
                    (done, total) => worker.ReportProgress(MystiqueWorker.ProgressConverting, done, total), budget.Token);
                if (package is null)
                {
                    return MystiqueNotResolved(item);
                }
                int startChoices = MystiqueSourceChoices;
                MystiqueSourceAfter? SourceAfterStream()
                {
                    MystiqueSourceAfter? after = null;
                    store.Update(s => after = MystiqueSourceAfterUpload(s.Devices.DeepCoolMystique, true, held, MystiqueSourceChoices != startChoices));
                    return after;
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
                    m.PictureIds = new();
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
    /// The item's id and the screen id a picture goes up under, null when the kind's slots are full. The screen
    /// replaces an item uploaded under an id it holds, so a picture reuses the item it is replacing, the one its
    /// source already went up as, or a freed id the screen still holds, before taking a new slot. A GIF goes up
    /// under <see cref="MystiqueGifSlot"/>, so its id only names it here and a freed one is never reused.
    /// </summary>
    internal static (string Id, string ScreenId)? MystiqueUploadId(IConfigStore store, string kind, byte[] source, string replace)
    {
        var mystique = new DeepCoolMystiqueSettings();
        store.Read(s => mystique = s.Devices.DeepCoolMystique.Copy());
        bool isGif = kind == DeepCoolMystiqueMediaItem.KindGif;
        string sourceId = MystiqueMedia.IdOf(isGif ? MystiqueMedia.KindGif : MystiqueMedia.KindPicture, source);
        var items = mystique.Media.FindAll(i => i.Kind == kind);
        var freed = isGif ? new List<DeepCoolMystiqueMediaItem>() : mystique.Freed.FindAll(i => i.Kind == kind);
        var existing = items.Find(i => i.Id == replace) ?? items.Find(i => i.Id == sourceId);
        if (existing is not null)
        {
            return (existing.Id, MystiqueScreenId(existing));
        }
        if (freed.Count > 0)
        {
            return (sourceId, (freed.Find(i => i.Id == sourceId) ?? freed[0]).Id);
        }
        if (items.Count >= (isGif ? MystiqueMedia.MaxGifs : MystiqueMedia.MaxPictures))
        {
            return null;
        }
        // A swap can leave another picture on the screen under this source's id.
        return (sourceId, items.Exists(i => MystiqueScreenId(i) == sourceId) ? Guid.NewGuid().ToString("N") : sourceId);
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
                if (gone.Exists(i => i.Kind == DeepCoolMystiqueMediaItem.KindPicture))
                {
                    m.PictureIds = MystiquePictureOrder(m);
                }
                m.Media.RemoveAll(match);
                foreach (var item in gone)
                {
                    if (item.Id == m.ShownGif)
                    {
                        m.ShownGif = "";
                    }
                    if (item.Kind == DeepCoolMystiqueMediaItem.KindPicture && !m.Freed.Exists(i => i.Id == MystiqueScreenId(item)))
                    {
                        m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = item.Kind, Id = MystiqueScreenId(item) });
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
