using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;
using Nexus.Service.Rendering;

namespace Nexus.Service.Peripherals.Ryujin;

public readonly record struct RyujinEncodeResult(byte[]? Avi, int Frames, string? Error)
{
    public bool Ok => Avi is not null;
}

/// <summary>Turns an image or GIF into the Extreme's MJPEG AVI: ffmpeg crops and scales to raw frames, turbojpeg compresses each.</summary>
public static class RyujinMediaEncoder
{
    internal const int MaxFrames = 60;
    internal const int MaxSpanSeconds = 4;

    /// <summary>Ceiling on the finished file; the flash slot size is not known, Armoury Crate's capture was 1.67 MB.</summary>
    internal const int MaxAviBytes = 3 * 1024 * 1024;

    private const int TimeoutSeconds = 120;
    private const int FrameBytes = RyujinMediaAvi.Width * RyujinMediaAvi.Height * 4;
    private static readonly int[] Qualities = { 90, 75, 60, 45, 30 };

    public static async Task<RyujinEncodeResult> EncodeAsync(byte[] source, string extension, CancellationToken ct)
    {
        if (source.Length == 0)
        {
            return new(null, 0, "Empty source.");
        }
        if (FfmpegResolver.Path is null)
        {
            return new(null, 0, "Media encode requires ffmpeg, which is missing. Reinstall nexus-service.");
        }
        var dir = Path.Combine(Path.GetTempPath(), "nexus-ryujin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "source" + SafeExtension(extension));
            await File.WriteAllBytesAsync(src, source, ct).ConfigureAwait(false);
            var (raw, decoded) = (string.Empty, 0);
            try
            {
                (raw, decoded) = await DecodeAsync(src, dir, withFps: true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceLog.Warn($"[ryujin-upload] fps decode failed, trying a single frame: {ex.Message}");
                decoded = 0;
            }
            if (decoded == 0)
            {
                (raw, decoded) = await DecodeAsync(src, dir, withFps: false, ct).ConfigureAwait(false);
            }
            if (decoded == 0)
            {
                return new(null, 0, "ffmpeg produced no frames.");
            }
            return await CompressAsync(raw, decoded, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(null, 0, ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static async Task<RyujinEncodeResult> CompressAsync(string raw, int decoded, CancellationToken ct)
    {
        int count = decoded == 1 || await AllIdenticalAsync(raw, decoded, ct).ConfigureAwait(false) ? 1 : decoded;
        bool variantLogged = false;
        foreach (int quality in Qualities)
        {
            var jpegs = new List<byte[]>(count);
            long total = RyujinMediaAvi.MoviDataOffset;
            var frame = new byte[FrameBytes];
            await using var stream = File.OpenRead(raw);
            for (int i = 0; i < count && total <= MaxAviBytes; i++)
            {
                await stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
                var jpeg = TurboJpegOneShot.TryCompress(frame, RyujinMediaAvi.Width, RyujinMediaAvi.Height, TurboJpeg.PixelFormatRgbx, quality, optimize: true, out bool optimized);
                if (jpeg is null)
                {
                    return new(null, 0, "The JPEG encoder is unavailable.");
                }
                jpeg = RyujinMediaAvi.WithComment(jpeg);
                if (!variantLogged)
                {
                    variantLogged = true;
                    ServiceLog.Info($"[ryujin-upload] jpeg variant: huffman={(optimized ? "optimized" : "standard (optimize refused)")}, JFIF APP0 stripped, Lavc comment, merged DQT/DHT, 4:2:0 baseline");
                }
                jpegs.Add(jpeg);
                total += 8 + jpeg.Length + (jpeg.Length & 1) + 16;
            }
            if (total <= MaxAviBytes)
            {
                return new(RyujinMediaAvi.Build(jpegs), jpegs.Count, null);
            }
        }
        return new(null, 0, "The file is too detailed to fit the screen's storage.");
    }

    private static async Task<bool> AllIdenticalAsync(string raw, int decoded, CancellationToken ct)
    {
        await using var stream = File.OpenRead(raw);
        var first = new byte[FrameBytes];
        var next = new byte[FrameBytes];
        await stream.ReadExactlyAsync(first, ct).ConfigureAwait(false);
        for (int i = 1; i < decoded; i++)
        {
            await stream.ReadExactlyAsync(next, ct).ConfigureAwait(false);
            if (!next.AsSpan().SequenceEqual(first))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<(string Raw, int Frames)> DecodeAsync(string src, string dir, bool withFps, CancellationToken ct)
    {
        var raw = Path.Combine(dir, withFps ? "frames-fps.raw" : "frames.raw");
        var w = RyujinMediaAvi.Width.ToString(CultureInfo.InvariantCulture);
        var h = RyujinMediaAvi.Height.ToString(CultureInfo.InvariantCulture);
        var filter = $"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h}";
        if (withFps)
        {
            filter += $",fps={RyujinMediaAvi.Fps}:eof_action=pass";
        }
        int count = withFps ? MaxFrames : 1;
        await MediaImporter.RunFfmpeg(TimeoutSeconds, ct, new[]
        {
            "-y", "-t", MaxSpanSeconds.ToString(CultureInfo.InvariantCulture), "-i", src, "-vf", filter,
            "-frames:v", count.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", "rgb0", raw,
        }).ConfigureAwait(false);
        long length = File.Exists(raw) ? new FileInfo(raw).Length : 0;
        return (raw, (int)Math.Min(count, length / FrameBytes));
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
