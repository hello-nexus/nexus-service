using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;
using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.DeepCool;

public readonly record struct MystiqueEncodeResult(MystiqueMediaPackage? Package, byte[]? Thumbnail, string? Error)
{
    public bool Ok => Package is not null;
    public static MystiqueEncodeResult Success(MystiqueMediaPackage package, byte[] thumbnail) => new(package, thumbnail, null);
    public static MystiqueEncodeResult Failure(string error) => new(null, null, error);
}

/// <summary>
/// Converts an image, GIF or video to the screen's stream: ffmpeg crops and scales to raw frames, then
/// libjpeg (turbojpeg, else Skia) encodes each one, giving the segment layout of the recorded uploads.
/// Quality steps down until the stream fits the recorded sizes; a source that never fits is refused.
/// </summary>
public static class MystiqueMediaEncoder
{
    // The recorded uploads' quantization tables match libjpeg quality 95.
    private static readonly int[] Qualities = { 95, 90, 85, 80, 70, 60, 50, 40 };

    private const int TimeoutSeconds = 120;
    private const int FrameBytes = MystiqueMedia.Width * MystiqueMedia.Height * 4;

    // A GIF is the first GifDurationMs of the clip at GifFrames over that span; a shorter clip repeats.
    private static readonly string GifRate = $"{MystiqueMedia.GifFrames * 1000}/{MystiqueMedia.GifDurationMs}";
    private const string GifReadSeconds = "3";

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
                if (jpeg.Length > 0 && jpeg.Length <= MystiqueMedia.MaxPictureJpegBytes)
                {
                    return MystiqueEncodeResult.Success(MystiqueMedia.BuildPicture(jpeg, id), jpeg);
                }
            }
            return MystiqueEncodeResult.Failure("The picture is too detailed to fit the screen's limit.");
        });

    public static Task<MystiqueEncodeResult> EncodeGifAsync(byte[] source, string extension, CropRect? crop, string id, CancellationToken ct) =>
        RunInWorkDir(source, extension, async (src, dir) =>
        {
            var frames = await DecodeFrames(src, dir, $"{Filter(crop)},fps={GifRate}", MystiqueMedia.GifFrames, GifReadSeconds, ct)
                .ConfigureAwait(false);
            if (frames.Count == 0)
            {
                return MystiqueEncodeResult.Failure("ffmpeg produced no frames.");
            }
            foreach (int q in Qualities)
            {
                var encoded = new byte[frames.Count][];
                var jpegs = new List<byte[]>(MystiqueMedia.GifFrames);
                for (int i = 0; i < MystiqueMedia.GifFrames; i++)
                {
                    int k = i % frames.Count;
                    jpegs.Add(encoded[k] ??= EncodeJpeg(frames[k], q));
                }
                if (jpegs.TrueForAll(j => j.Length > 0) && MystiqueMedia.StreamLength(jpegs) <= MystiqueMedia.MaxGifStreamBytes)
                {
                    return MystiqueEncodeResult.Success(MystiqueMedia.BuildGif(jpegs, id), jpegs[0]);
                }
            }
            return MystiqueEncodeResult.Failure("The GIF is too detailed to fit the screen's limit.");
        });

    /// <summary>Optional cropper rectangle, then cover-fit to the screen size.</summary>
    internal static string Filter(CropRect? crop)
    {
        var fit = $"scale={MystiqueMedia.Width}:{MystiqueMedia.Height}:force_original_aspect_ratio=increase:flags=lanczos,"
            + $"crop={MystiqueMedia.Width}:{MystiqueMedia.Height}";
        return crop is { IsIdentity: false } c ? $"{c.ToFfmpegCrop()},{fit}" : fit;
    }

    /// <summary>Baseline 4:2:0 JPEG of one RGBX frame at the screen size, or empty when both encoders fail.</summary>
    internal static unsafe byte[] EncodeJpeg(byte[] rgbx, int quality)
    {
        if (TurboJpegOneShot.TryCompress(rgbx, MystiqueMedia.Width, MystiqueMedia.Height, TurboJpeg.PixelFormatRgbx, quality) is { } native)
        {
            return native;
        }
        var info = new SKImageInfo(MystiqueMedia.Width, MystiqueMedia.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        fixed (byte* p = rgbx)
        {
            using var pixmap = new SKPixmap(info, (IntPtr)p, MystiqueMedia.Width * 4);
            using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
            return data?.ToArray() ?? Array.Empty<byte>();
        }
    }

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
