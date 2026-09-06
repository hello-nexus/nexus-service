using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Media;
using Nexus.Service.Platform;

namespace Nexus.Service.Media;

/// <summary>
/// Converts uploaded files to optimized frame sequences for the lighting engine.
/// All formats (static images, animated GIFs, videos) go through ffmpeg. A
/// single ffmpeg pass produces raw RGB24 frames at the canvas resolution (160x90)
/// written consecutively to frames.bin; a second pass captures the first frame as
/// thumb.jpg for UI previews. Static images collapse to a 1-frame output; animated
/// content is capped at 30 fps / 300 frames.
/// </summary>
public static class MediaImporter
{
    public const int CanvasWidth = 160;
    public const int CanvasHeight = 90;
    public const int MaxFps = 30;
    public const int MaxFrames = 300;
    public const long MaxFileSize = 100 * 1024 * 1024;
    public const int ThumbWidth = 480;
    public const int ThumbHeight = 270;

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif" };
    private static readonly string[] AnimatedExtensions = { ".gif", ".mp4", ".webm", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".mpg", ".mpeg" };

    /// <summary>
    /// Stores the raw upload and extracts a 1280-wide preview JPEG.
    /// Returns StageResult with stageId on success, or Error on unsupported/unreadable input.
    /// </summary>
    public static async Task<StageResult> StageAsync(
        MediaLibrary library,
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

        library.SweepStaging(maxAgeMinutes: 30);

        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var stageId = SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");
        var stagingDir = library.GetStagingDir();
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

        var previewPath = library.GetStagePreviewPath(stageId);
        try
        {
            await RunFfmpeg(
                "-y", "-i", stagedPath,
                "-frames:v", "1",
                "-vf", "scale=1280:1280:force_original_aspect_ratio=decrease",
                "-q:v", "4",
                previewPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-stage] ffmpeg preview failed: {ex.Message}");
        }

        if (!File.Exists(previewPath) || new FileInfo(previewPath).Length == 0)
        {
            try { File.Delete(stagedPath); }
            catch { }
            try { File.Delete(previewPath); }
            catch { }
            return StageResult.Failure("Unsupported or unreadable media");
        }

        return StageResult.Success(stageId);
    }

    /// <summary>
    /// Bakes the staged raw file into a committed library item (frames.bin + thumb.jpg + meta.json),
    /// then deletes the staging files for stageId.
    /// </summary>
    public static async Task<CommitResult> CommitAsync(
        MediaLibrary library,
        string stageId,
        string? crop,
        string? originalName = null)
    {
        var stagedPath = library.FindStagedRaw(stageId);
        if (stagedPath is null || !File.Exists(stagedPath))
        {
            return CommitResult.Failure("Stage not found or expired");
        }

        if (FfmpegResolver.Path is null)
        {
            return CommitResult.Failure("Media conversion requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        // Prefer the caller's original upload name so the library shows the real
        // filename, not the random stage id; the staged file's extension stays
        // authoritative for format detection.
        var importName = string.IsNullOrWhiteSpace(originalName)
            ? Path.GetFileName(stagedPath)
            : Path.GetFileNameWithoutExtension(originalName) + Path.GetExtension(stagedPath);
        var result = await ImportAsync(library, stagedPath, importName, crop);
        library.DeleteStage(stageId);

        if (!result.Ok)
        {
            return CommitResult.Failure(result.Error ?? "Import failed");
        }

        return CommitResult.Success(result.Item!);
    }

    public static async Task<ImportResult> ImportAsync(MediaLibrary library, string sourcePath, string originalName, string? crop = null)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var id = SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");

        bool isImage = Array.Exists(ImageExtensions, e => e == ext);
        bool isAnimated = Array.Exists(AnimatedExtensions, e => e == ext);
        if (!isImage && !isAnimated)
        {
            return ImportResult.Failure("Unsupported file format");
        }

        if (FfmpegResolver.Path is null)
        {
            return ImportResult.Failure("Media conversion requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        var dir = library.GetItemDir(id);
        try
        {
            Directory.CreateDirectory(dir);
            var framesPath = library.GetFramesBinPath(id);
            var thumbPath = library.GetThumbPath(id);

            // Optional pre-crop (normalized to the source) selected in the
            // cropper; the cropped region is already 16:9, so decrease+pad below
            // lands exactly on the canvas with no bars.
            var cropPrefix = CropRect.TryParse(crop, out var cropRect) ? cropRect.ToFfmpegCrop() + "," : "";
            var scaleFilter = $"{cropPrefix}scale={CanvasWidth}:{CanvasHeight}:force_original_aspect_ratio=decrease,pad={CanvasWidth}:{CanvasHeight}:-1:-1:color=black";
            var thumbScaleFilter = $"{cropPrefix}scale={ThumbWidth}:{ThumbHeight}:force_original_aspect_ratio=decrease,pad={ThumbWidth}:{ThumbHeight}:-1:-1:color=black";

            if (isImage)
            {
                await RunFfmpeg("-y", "-i", sourcePath, "-vf", scaleFilter, "-frames:v", "1",
                    "-f", "rawvideo", "-pix_fmt", "rgb24", framesPath);
            }
            else
            {
                await RunFfmpeg("-y", "-i", sourcePath, "-vf", scaleFilter, "-r", MaxFps.ToString(),
                    "-frames:v", MaxFrames.ToString(), "-f", "rawvideo", "-pix_fmt", "rgb24", framesPath);
            }

            if (!File.Exists(framesPath) || new FileInfo(framesPath).Length == 0)
            {
                library.DeleteItem(id);
                return ImportResult.Failure("ffmpeg produced no frames");
            }

            var frameByteCount = CanvasWidth * CanvasHeight * 3L;
            var totalBytes = new FileInfo(framesPath).Length;
            var frameCount = (int)(totalBytes / frameByteCount);
            if (frameCount == 0)
            {
                library.DeleteItem(id);
                return ImportResult.Failure("Frame decode failed");
            }

            await RunFfmpeg("-y", "-i", sourcePath, "-vf", thumbScaleFilter, "-frames:v", "1",
                "-q:v", "5", thumbPath);

            var item = new MediaItem
            {
                Id = id,
                Name = originalName,
                Type = frameCount > 1 ? "animated" : "static",
                Frames = frameCount,
                Fps = frameCount > 1 ? MaxFps : 0,
                Width = CanvasWidth,
                Height = CanvasHeight,
                ImportedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            library.SaveMeta(item);
            return ImportResult.Success(item);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-import] failed: {ex.Message}");
            library.DeleteItem(id);
            return ImportResult.Failure(ex.Message);
        }
    }

    // Hard cap on any single ffmpeg run. A pathological input that wedges the
    // decoder must not park an import forever.
    private const int FfmpegTimeoutSeconds = 120;

    internal static Task RunFfmpeg(params string[] args) =>
        RunFfmpeg(FfmpegTimeoutSeconds, CancellationToken.None, args);

    /// <summary>
    /// Runs ffmpeg with an explicit budget and an external cancellation token.
    /// Callers sitting on a live HTTP request use this: the import default of
    /// two minutes is far too long to hold a request open, and passing
    /// RequestAborted means an abandoned fetch kills the child process instead
    /// of leaving it to burn a core.
    /// </summary>
    internal static async Task RunFfmpeg(int timeoutSeconds, CancellationToken ct, string[] args)
    {
        var ffmpegPath = FfmpegResolver.Path
            ?? throw new InvalidOperationException("ffmpeg not found");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg failed to start");

        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch { }
            // A caller-cancelled run is not a timeout - let the caller's own
            // token surface so it is not logged as an ffmpeg fault.
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"ffmpeg timed out after {timeoutSeconds}s");
        }
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 400 ? stderr[^400..] : stderr;
            throw new InvalidOperationException($"ffmpeg exit {proc.ExitCode}: {tail.Trim()}");
        }
    }

    // IDs become directory names, file names, ffmpeg arguments, and URL path
    // segments, so they must stay ASCII and path-safe whatever language the
    // source filename is in. Anything outside [A-Za-z0-9_-] - non-ASCII letters
    // (kanji, accented Latin) included, which char.IsLetterOrDigit would keep -
    // collapses to '_'. The result is pure ASCII, so the 64-char cap can never
    // split a surrogate pair.
    internal static string SanitizeId(string id)
    {
        var sb = new System.Text.StringBuilder(Math.Min(id.Length, 64));
        foreach (var ch in id)
        {
            if (sb.Length >= 64)
            {
                break;
            }
            bool ascii = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
                or (>= '0' and <= '9') or '-' or '_';
            sb.Append(ascii ? ch : '_');
        }
        return sb.ToString();
    }

    public readonly record struct ImportResult(MediaItem? Item, string? Error)
    {
        public bool Ok => Item is not null;
        public static ImportResult Success(MediaItem item) => new(item, null);
        public static ImportResult Failure(string error) => new(null, error);
    }

    public readonly record struct StageResult(string? StageId, string? Error)
    {
        public bool Ok => StageId is not null;
        public static StageResult Success(string stageId) => new(stageId, null);
        public static StageResult Failure(string error) => new(null, error);
    }

    public readonly record struct CommitResult(MediaItem? Item, string? Error)
    {
        public bool Ok => Item is not null;
        public static CommitResult Success(MediaItem item) => new(item, null);
        public static CommitResult Failure(string error) => new(null, error);
    }
}
