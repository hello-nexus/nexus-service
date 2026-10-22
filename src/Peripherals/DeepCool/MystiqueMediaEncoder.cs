using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Platform;
using Nexus.Service.Rendering;

namespace Nexus.Service.Peripherals.DeepCool;

public readonly record struct MystiqueEncodeResult(MystiqueMediaPackage? Package, byte[]? Thumbnail, string? Error)
{
    public bool Ok => Package is not null;
    public static MystiqueEncodeResult Success(MystiqueMediaPackage package, byte[] thumbnail) => new(package, thumbnail, null);
    public static MystiqueEncodeResult Failure(string error) => new(null, null, error);
}

/// <summary>
/// Converts an image, GIF or video to the screen's stream: ffmpeg crops and scales to raw frames, then
/// turbojpeg encodes each one with the segment layout and standard Huffman tables of the recorded
/// uploads. Quality steps down, then for a GIF detail is reduced, until the stream fits the recorded
/// sizes; a source that never fits is refused.
/// </summary>
public static class MystiqueMediaEncoder
{
    // Starts at the quality whose quantization tables the recorded uploads carry.
    private static readonly int[] Qualities = { 95, 90, 85, 80, 70, 60, 50, 40, 30, 25, 20 };

    // Tried in turn when no quality fits a GIF, each softer than the last by a round trip through a
    // smaller size: the bundled ffmpeg has scale but no denoise or blur filter.
    private static readonly string[] GifDetailSteps =
    {
        "",
        Soften(MystiqueMedia.Width * 3 / 4, MystiqueMedia.Height * 3 / 4),
        Soften(MystiqueMedia.Width / 2, MystiqueMedia.Height / 2),
    };

    private static string Soften(int width, int height) =>
        $",scale={width}:{height}:flags=area,scale={MystiqueMedia.Width}:{MystiqueMedia.Height}:flags=bicubic";

    private const int TimeoutSeconds = 120;
    private const int FrameBytes = MystiqueMedia.Width * MystiqueMedia.Height * 4;

    // A GIF's frames are spread evenly across the whole source, cut to this span for a longer video. Assumed from the
    // one recorded GIF, whose frames form one whole loop of its unrecorded source.
    internal const int MaxGifSpanMs = 10_000;

    // Assumed, not yet seen on the wire: the duration field is the span the frames cover. Until a capture shows a
    // value other than the recorded one, every GIF sends the recorded value.
    private const bool DurationIsSpan = false;

    public static Task<MystiqueEncodeResult> EncodePictureAsync(byte[] source, string extension, CropRect? crop, string id, CancellationToken ct) =>
        RunInWorkDir(source, extension, async (src, dir) =>
        {
            var frames = await DecodeFrames(src, dir, Filter(crop), 1, null, ct).ConfigureAwait(false);
            if (frames.Count == 0)
            {
                return MystiqueEncodeResult.Failure("ffmpeg produced no frame.");
            }
            foreach (int q in Qualities)
            {
                var jpeg = EncodeJpeg(frames[0], q);
                if (jpeg.Length == 0)
                {
                    return MystiqueEncodeResult.Failure(NoEncoder);
                }
                if (jpeg.Length <= MystiqueMedia.MaxPictureJpegBytes)
                {
                    return MystiqueEncodeResult.Success(MystiqueMedia.BuildPicture(jpeg, id), jpeg);
                }
            }
            return MystiqueEncodeResult.Failure("The picture is too detailed to fit the screen's limit.");
        });

    public static Task<MystiqueEncodeResult> EncodeGifAsync(byte[] source, string extension, CropRect? crop, string id, CancellationToken ct) =>
        RunInWorkDir(source, extension, async (src, dir) =>
        {
            int spanMs = await SpanMs(src, ct).ConfigureAwait(false);
            foreach (var detail in GifDetailSteps)
            {
                var frames = await DecodeFrames(src, dir, $"{Filter(crop)}{detail},fps={MystiqueMedia.GifFrames * 1000}/{spanMs}:eof_action=pass",
                    MystiqueMedia.GifFrames, Seconds(spanMs), ct).ConfigureAwait(false);
                if (frames.Count == 0)
                {
                    // ffmpeg 7's fps filter drops a lone still frame; the still then repeats.
                    frames = await DecodeFrames(src, dir, $"{Filter(crop)}{detail}", 1, null, ct).ConfigureAwait(false);
                }
                if (frames.Count == 0)
                {
                    return MystiqueEncodeResult.Failure("ffmpeg produced no frames.");
                }
                // A still's span is one image's, so it keeps the recorded value either way.
                bool still = frames.TrueForAll(f => f.AsSpan().SequenceEqual(frames[0]));
                int durationMs = DurationIsSpan && !still ? spanMs : MystiqueMedia.GifDurationMs;
                foreach (int q in Qualities)
                {
                    var encoded = new byte[frames.Count][];
                    var jpegs = new List<byte[]>(MystiqueMedia.GifFrames);
                    for (int i = 0; i < MystiqueMedia.GifFrames; i++)
                    {
                        int k = SlotSource(i, frames.Count);
                        jpegs.Add(encoded[k] ??= EncodeJpeg(frames[k], q));
                    }
                    if (!jpegs.TrueForAll(j => j.Length > 0))
                    {
                        return MystiqueEncodeResult.Failure(NoEncoder);
                    }
                    if (MystiqueMedia.StreamLength(jpegs) <= MystiqueMedia.MaxGifStreamBytes)
                    {
                        return MystiqueEncodeResult.Success(MystiqueMedia.BuildGif(jpegs, id, durationMs), jpegs[0]);
                    }
                }
            }
            return MystiqueEncodeResult.Failure("The GIF is too detailed to fit the screen's limit.");
        });

    /// <summary>The source's length up to <see cref="MaxGifSpanMs"/>, or the recorded GIF duration when ffmpeg reports none.</summary>
    private static async Task<int> SpanMs(string src, CancellationToken ct)
    {
        // rawvideo: the bundled ffmpeg has no encoder for the null muxer's default.
        var stderr = await MediaImporter.RunFfmpeg(TimeoutSeconds, ct, new[] { "-i", src, "-frames:v", "1", "-c:v", "rawvideo", "-f", "null", "-" })
            .ConfigureAwait(false);
        return HydroShift2CurveMedia.ParseDuration(stderr) is double seconds and > 0
            ? (int)Math.Clamp(Math.Round(seconds * 1000), 1, MaxGifSpanMs)
            : MystiqueMedia.GifDurationMs;
    }

    /// <summary>The decoded frame a GIF slot shows: with fewer frames than slots, the repeats spread evenly across the loop.</summary>
    internal static int SlotSource(int slot, int frameCount) => slot * frameCount / MystiqueMedia.GifFrames;

    private static string Seconds(int ms) => (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    // Long side of a reduced kept picture: several times the screen's long side, so a tight crop still fills it.
    internal const int KeepLongSide = 1920;

    // Long side and CRF of a reduced kept clip, tried in turn until one fits: a clip's bytes grow with its length.
    private static readonly (int LongSide, int Crf)[] ClipKeepSteps = { (1280, 23), (854, 30) };

    /// <summary>
    /// A smaller copy of a source for a later re-crop, at most <paramref name="maxBytes"/>, null when ffmpeg made
    /// none that fits: a picture as a JPEG at most <see cref="KeepLongSide"/> on the long side, a GIF or video as
    /// an H.264 MP4 of the span the encoder reads at the first of <see cref="ClipKeepSteps"/> that fits. Not
    /// cancellable: it runs after the screen took the upload, which is recorded regardless.
    /// </summary>
    public static async Task<(byte[] Bytes, string Ext)?> ReduceForKeepingAsync(byte[] source, string extension, bool clip, long maxBytes)
    {
        if (source.Length == 0 || FfmpegResolver.Path is null)
        {
            return null;
        }
        var dir = Path.Combine(Path.GetTempPath(), "nexus-mystique-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var src = Path.Combine(dir, "source" + SafeExtension(extension));
            await File.WriteAllBytesAsync(src, source).ConfigureAwait(false);
            string ext = clip ? ".mp4" : ".jpg";
            var kept = Path.Combine(dir, "kept" + ext);
            var steps = clip ? ClipKeepSteps : new[] { (KeepLongSide, 0) };
            foreach (var (longSide, crf) in steps)
            {
                var scale = $"scale=w='min(iw,{longSide})':h='min(ih,{longSide})':force_original_aspect_ratio=decrease:force_divisible_by=2";
                string[] args = clip
                    ? new[]
                    {
                        "-y", "-t", Seconds(MaxGifSpanMs), "-i", src, "-vf", scale, "-an",
                        "-c:v", "libx264", "-preset", "veryfast", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", kept,
                    }
                    : new[] { "-y", "-i", src, "-vf", scale, "-frames:v", "1", "-q:v", "2", kept };
                await MediaImporter.RunFfmpeg(TimeoutSeconds, CancellationToken.None, args).ConfigureAwait(false);
                if (File.Exists(kept) && new FileInfo(kept).Length is > 0 and var length && length <= maxBytes)
                {
                    return (await File.ReadAllBytesAsync(kept).ConfigureAwait(false), ext);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[deepcool-mystique] reduced source not made: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Optional cropper rectangle, then cover-fit to the screen size.</summary>
    internal static string Filter(CropRect? crop)
    {
        var fit = $"scale={MystiqueMedia.Width}:{MystiqueMedia.Height}:force_original_aspect_ratio=increase:flags=lanczos,"
            + $"crop={MystiqueMedia.Width}:{MystiqueMedia.Height}";
        return crop is { IsIdentity: false } c ? $"{c.ToFfmpegCrop()},{fit}" : fit;
    }

    private const string NoEncoder = "The JPEG encoder is unavailable.";

    /// <summary>
    /// Baseline 4:2:0 JPEG of one RGBX frame at the screen size, or empty without turbojpeg. Skia's encoder
    /// is not a fallback: it optimizes the Huffman tables, a layout the screen has never been seen to take.
    /// </summary>
    internal static byte[] EncodeJpeg(byte[] rgbx, int quality) =>
        TurboJpegOneShot.TryCompress(rgbx, MystiqueMedia.Width, MystiqueMedia.Height, TurboJpeg.PixelFormatRgbx, quality) ?? Array.Empty<byte>();

    private static async Task<List<byte[]>> DecodeFrames(string src, string dir, string filter, int count, string? readSeconds, CancellationToken ct)
    {
        var raw = Path.Combine(dir, "frames.raw");
        var args = new List<string> { "-y" };
        if (readSeconds is not null)
        {
            args.AddRange(new[] { "-t", readSeconds });
        }
        args.AddRange(new[]
        {
            "-i", src, "-vf", filter, "-frames:v", count.ToString(CultureInfo.InvariantCulture),
            "-f", "rawvideo", "-pix_fmt", "rgb0", raw,
        });
        await MediaImporter.RunFfmpeg(TimeoutSeconds, ct, args.ToArray()).ConfigureAwait(false);
        var frames = new List<byte[]>();
        if (!File.Exists(raw))
        {
            return frames;
        }
        var bytes = await File.ReadAllBytesAsync(raw, ct).ConfigureAwait(false);
        for (int offset = 0; offset + FrameBytes <= bytes.Length && frames.Count < count; offset += FrameBytes)
        {
            frames.Add(bytes.AsSpan(offset, FrameBytes).ToArray());
        }
        return frames;
    }

    private static async Task<MystiqueEncodeResult> RunInWorkDir(
        byte[] source, string extension, Func<string, string, Task<MystiqueEncodeResult>> encode)
    {
        if (source.Length == 0)
        {
            return MystiqueEncodeResult.Failure("Empty source.");
        }
        if (FfmpegResolver.Path is null)
        {
            return MystiqueEncodeResult.Failure("Media encode requires ffmpeg, which is missing. Reinstall nexus-service.");
        }
        var dir = Path.Combine(Path.GetTempPath(), "nexus-mystique-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "source" + SafeExtension(extension));
            await File.WriteAllBytesAsync(src, source).ConfigureAwait(false);
            return await encode(src, dir).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MystiqueEncodeResult.Failure(ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static string SafeExtension(string extension)
    {
        var e = extension.StartsWith('.') ? extension[1..] : extension;
        foreach (char c in e)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return "";
            }
        }
        return e.Length is > 0 and <= 5 ? "." + e : "";
    }
}
