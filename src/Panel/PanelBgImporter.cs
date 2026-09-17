using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Models.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel;

/// <summary>
/// Two-phase panel-background import: Stage (upload + server preview) then
/// Commit (crop + bake). The staged raw file is deleted on commit or cancel.
/// No source is retained after commit.
///
/// Neither mjpeg nor H.264 carries alpha, so a transparent source bakes to the
/// png/gif pair instead. Dropping an alpha channel keeps whatever RGB sat under
/// it, so the opaque path composites onto black explicitly.
/// </summary>
public static class PanelBgImporter
{
    public const long MaxFileSize = 500L * 1024 * 1024;

    // An rgba pixel format only says alpha CAN be carried; most exported pngs
    // are rgba and fully opaque, and baking those lossless costs ~10x the bytes.
    private const int AlphaProbeSize = 64;
    private const int AlphaProbeFrames = 8;
    private const int AlphaProbeTimeoutSeconds = 20;
    // Not 255: swscale rounding leaves stray 254s that are not transparency.
    private const byte AlphaOpaqueFloor = 250;

    // Sized here rather than by force_original_aspect_ratio=decrease: the black
    // matte needs a canvas of known size.
    private const int ThumbBox = 480;

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif" };
    private static readonly string[] AnimatedExtensions = { ".gif", ".mp4", ".webm", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".mpg", ".mpeg" };

    /// <summary>
    /// Stores the raw upload and extracts a 1280-wide preview jpg via ffmpeg.
    /// Returns StageResult with stageId on success, or Error on unsupported/unreadable input.
    /// </summary>
    public static async Task<StageResult> StageAsync(
        PanelBgLibrary library,
        string deviceId,
        string sourcePath,
        string originalName)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        bool isImage = Array.Exists(ImageExtensions, e => e == ext);
        bool isAnimated = Array.Exists(AnimatedExtensions, e => e == ext);
        if (!isImage && !isAnimated)
        {
            return StageResult.Failure("Unsupported file format");
        }

        if (FfmpegResolver.Path is null)
        {
            return StageResult.Failure("Media conversion requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        library.SweepStaging(deviceId, maxAgeMinutes: 30);

        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var stageId = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");
        var stagingDir = library.GetStagingDir(deviceId);
        Directory.CreateDirectory(stagingDir);

        var stagedPath = Path.Combine(stagingDir, stageId + ext);
        try
        {
            File.Move(sourcePath, stagedPath);
        }
        catch (Exception ex)
        {
            return StageResult.Failure($"Failed to store upload: {ex.Message}");
        }

        // A png preview shows the cropper the transparency it is about to keep,
        // not the arbitrary RGB hiding under it.
        var alpha = await HasTransparencyAsync(stagedPath);
        var previewPath = library.GetStagePreviewPath(deviceId, stageId, alpha);
        try
        {
            var quality = alpha ? new[] { "-frames:v", "1" } : new[] { "-frames:v", "1", "-q:v", "4" };
            var args = new List<string> { "-y", "-i", stagedPath };
            args.AddRange(quality);
            args.AddRange(new[] { "-vf", "scale=1280:1280:force_original_aspect_ratio=decrease", previewPath });
            await MediaImporter.RunFfmpeg(args.ToArray());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-bg-stage] ffmpeg preview failed: {ex.Message}");
        }

        if (!File.Exists(previewPath) || new FileInfo(previewPath).Length == 0)
        {
            try { File.Delete(stagedPath); }
            catch { }
            try { File.Delete(previewPath); }
            catch { }
            return StageResult.Failure("Unsupported or unreadable video");
        }

        return StageResult.Success(stageId, alpha);
    }

    /// <summary>
    /// Bakes the staged raw file into a committed asset (cropped + scaled media + thumb),
    /// then deletes the staging files for stageId.
    /// </summary>
    public static async Task<CommitResult> CommitAsync(
        PanelBgLibrary library,
        string deviceId,
        string stageId,
        CropRect crop,
        int targetW,
        int targetH,
        bool keepTransparency = true,
        bool fitWhole = false)
    {
        var stagedPath = library.FindStagedRaw(deviceId, stageId);
        if (stagedPath is null || !File.Exists(stagedPath))
        {
            return CommitResult.Failure("Stage not found or expired");
        }

        if (FfmpegResolver.Path is null)
        {
            return CommitResult.Failure("Media conversion requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        var originalName = Path.GetFileName(stagedPath);
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var assetId = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");

        bool isImage = Array.Exists(ImageExtensions, e => e == ext);
        bool isAnimated = Array.Exists(AnimatedExtensions, e => e == ext);

        var sourceHasAlpha = await HasTransparencyAsync(stagedPath);
        // gif is the one animated container this build writes with alpha, and
        // transcoding a real video into it costs more than the alpha is worth.
        var alpha = keepTransparency && sourceHasAlpha && (!isAnimated || ext == ".gif");
        var matte = sourceHasAlpha && !alpha;

        var dir = library.GetItemDir(deviceId, assetId);
        try
        {
            Directory.CreateDirectory(dir);

            if (isAnimated && alpha)
            {
                // palettegen/paletteuse is the only route to a gif that keeps its
                // transparent index; a plain pal8 conversion drops it. No -r: a
                // forced rate duplicates frames and inflates the file.
                var scale = fitWhole
                    ? $"scale={targetW}:{targetH}:force_original_aspect_ratio=decrease:flags=lanczos,pad={targetW}:{targetH}:-1:-1:color=black@0"
                    : $"scale={targetW}:{targetH}:flags=lanczos";
                var filter = $"{crop.ToFfmpegCrop()},{scale},"
                    + "split[s0][s1];[s0]palettegen=reserve_transparent=1[p];"
                    + "[s1][p]paletteuse=alpha_threshold=128";
                var mediaPath = library.GetMediaPath(deviceId, assetId, ".gif");
                await MediaImporter.RunFfmpeg("-y", "-i", stagedPath,
                    "-vf", filter,
                    "-loop", "0",
                    "-t", "30", mediaPath);

                if (!File.Exists(mediaPath))
                {
                    library.DeleteItem(deviceId, assetId);
                    return CommitResult.Failure("ffmpeg produced no media file");
                }
            }
            else if (isAnimated)
            {
                int ew = targetW - (targetW & 1);
                int eh = targetH - (targetH & 1);
                var mediaPath = library.GetMediaPath(deviceId, assetId, ".mp4");
                var args = new List<string> { "-y", "-i", stagedPath };
                args.AddRange(CropScaleArgs(crop, ew, eh, matte, animated: true, fitWhole));
                args.AddRange(new[]
                {
                    "-r", "30",
                    "-c:v", "libx264", "-profile:v", "main", "-preset", "veryfast", "-crf", "23",
                    "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart",
                    "-t", "30", mediaPath,
                });
                await MediaImporter.RunFfmpeg(args.ToArray());

                if (!File.Exists(mediaPath))
                {
                    library.DeleteItem(deviceId, assetId);
                    return CommitResult.Failure("ffmpeg produced no media file");
                }
            }
            else
            {
                var mediaPath = library.GetMediaPath(deviceId, assetId, alpha ? ".png" : ".jpg");
                var args = new List<string> { "-y", "-i", stagedPath };
                args.AddRange(CropScaleArgs(crop, targetW, targetH, matte, animated: false, fitWhole, alpha));
                args.Add("-frames:v");
                args.Add("1");
                if (!alpha)
                {
                    args.Add("-q:v");
                    args.Add("3");
                }
                args.Add(mediaPath);
                await MediaImporter.RunFfmpeg(args.ToArray());

                if (!File.Exists(mediaPath))
                {
                    library.DeleteItem(deviceId, assetId);
                    return CommitResult.Failure("ffmpeg produced no media file");
                }
            }

            var (thumbW, thumbH) = ThumbSize(targetW, targetH);
            var thumbPath = library.GetThumbPath(deviceId, assetId, alpha);
            var thumbArgs = new List<string> { "-y", "-i", stagedPath };
            thumbArgs.AddRange(CropScaleArgs(crop, thumbW, thumbH, matte, animated: false, fitWhole, alpha));
            thumbArgs.Add("-frames:v");
            thumbArgs.Add("1");
            if (!alpha)
            {
                thumbArgs.Add("-q:v");
                thumbArgs.Add("5");
            }
            thumbArgs.Add(thumbPath);
            await MediaImporter.RunFfmpeg(thumbArgs.ToArray());

            if (!File.Exists(thumbPath))
            {
                library.DeleteItem(deviceId, assetId);
                return CommitResult.Failure("ffmpeg produced no thumbnail");
            }

            double durationSec = 0;
            if (isAnimated)
            {
                durationSec = await ProbeDurationSec(library.GetMediaPath(deviceId, assetId, alpha ? ".gif" : ".mp4"));
            }

            var item = new PanelBgItem
            {
                Id = assetId,
                Name = Path.GetFileName(originalName),
                Type = isAnimated ? "animated" : "static",
                Width = targetW,
                Height = targetH,
                ImportedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DurationSec = durationSec,
                Alpha = alpha,
            };
            library.SaveMeta(deviceId, item);

            library.DeleteStage(deviceId, stageId);

            return CommitResult.Success(item);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-bg-commit] failed: {ex.Message}");
            library.DeleteItem(deviceId, assetId);
            return CommitResult.Failure(ex.Message);
        }
    }

    /// <summary>True when the source carries transparency; any failure answers false, keeping an unreadable probe on the opaque pipeline.</summary>
    internal static async Task<bool> HasTransparencyAsync(string sourcePath)
    {
        var ffmpegPath = FfmpegResolver.Path;
        if (ffmpegPath is null)
        {
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-v", "error", "-i", sourcePath,
                     "-vf", $"scale={AlphaProbeSize}:{AlphaProbeSize}",
                     "-frames:v", AlphaProbeFrames.ToString(CultureInfo.InvariantCulture),
                     "-f", "rawvideo", "-pix_fmt", "rgba", "-",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AlphaProbeTimeoutSeconds));
            using var buffer = new MemoryStream();
            var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(buffer, cts.Token);
            // Drained so a full stderr pipe cannot wedge ffmpeg.
            var stderrTask = proc.StandardError.ReadToEndAsync();
            try
            {
                await copyTask;
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch { }
                return false;
            }
            await stderrTask;

            var bytes = buffer.GetBuffer();
            var length = (int)buffer.Length;
            for (var i = 3; i < length; i += 4)
            {
                if (bytes[i] < AlphaOpaqueFloor)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-bg] alpha probe failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The 480-box thumbnail size for a cropped frame of w x h, never upscaled.</summary>
    internal static (int W, int H) ThumbSize(int w, int h)
    {
        if (w <= 0 || h <= 0)
        {
            return (ThumbBox, ThumbBox);
        }
        var scale = Math.Min(1.0, Math.Min((double)ThumbBox / w, (double)ThumbBox / h));
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    /// <summary>Crop+scale that flattens alpha onto black; the matte runs after the scale so the canvas size is known without probing the source.</summary>
    private static string[] MatteArgs(CropRect crop, int w, int h, bool animated, bool fitWhole)
    {
        var rate = animated ? ":r=30" : "";
        return new[]
        {
            "-filter_complex",
            $"[0:v]{ScaleFilter(crop, w, h, fitWhole, alpha: false)}[fg];color=black:s={w}x{h}{rate}[bg];[bg][fg]overlay=shortest=1",
        };
    }

    /// <summary>Crop+scale for a source with nothing to matte.</summary>
    private static string[] ScaleArgs(CropRect crop, int w, int h, bool fitWhole, bool alpha) =>
        new[] { "-vf", ScaleFilter(crop, w, h, fitWhole, alpha) };

    /// <summary>
    /// Fills the target by default: the crop is already at the target aspect, so
    /// a plain scale lands on it. fitWhole keeps the whole frame instead, which
    /// needs decrease+pad - a plain scale would stretch it to the target. The
    /// bars are transparent when the output keeps alpha, opaque otherwise.
    /// </summary>
    private static string ScaleFilter(CropRect crop, int w, int h, bool fitWhole, bool alpha) =>
        fitWhole
            ? $"{crop.ToFfmpegCrop()},scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:-1:-1:color={(alpha ? "black@0" : "black")}"
            : $"{crop.ToFfmpegCrop()},scale={w}:{h}";

    private static string[] CropScaleArgs(CropRect crop, int w, int h, bool matte, bool animated, bool fitWhole, bool alpha = false) =>
        matte ? MatteArgs(crop, w, h, animated, fitWhole) : ScaleArgs(crop, w, h, fitWhole, alpha);

    // ffmpeg exits non-zero when invoked with only -i (no output), so stderr
    // must be captured with the exit code ignored.
    private static async Task<double> ProbeDurationSec(string path)
    {
        var ffmpegPath = FfmpegResolver.Path;
        if (ffmpegPath is null)
        {
            return 0;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return 0;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 0;
        }

        var stderr = await stderrTask;
        var m = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success)
        {
            return 0;
        }

        var hours = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return hours * 3600 + minutes * 60 + seconds;
    }

    public readonly record struct StageResult(string? StageId, string? Error, bool Alpha = false)
    {
        public bool Ok => StageId is not null;
        public static StageResult Success(string stageId, bool alpha) => new(stageId, null, alpha);
        public static StageResult Failure(string error) => new(null, error);
    }

    public readonly record struct CommitResult(PanelBgItem? Item, string? Error)
    {
        public bool Ok => Item is not null;
        public static CommitResult Success(PanelBgItem item) => new(item, null);
        public static CommitResult Failure(string error) => new(null, error);
    }
}
