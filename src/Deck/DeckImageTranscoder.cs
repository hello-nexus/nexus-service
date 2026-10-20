using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;

namespace Nexus.Service.Deck;

/// <summary>
/// Crops and shrinks an animated or still source into a deck key icon: crop to the
/// cropper's rect, then fit inside a square of MaxEdge pixels (never upscaled). A GIF stays a
/// transparent-capable animated GIF (the PanelBgImporter palettegen route), any other video
/// becomes the same H.264 MP4 that importer emits, and a still becomes a PNG.
/// </summary>
public static class DeckImageTranscoder
{
    public const int MaxEdge = 256;
    public const int TimeoutSeconds = 60;
    private const int MaxSeconds = 30;

    public enum Kind { Gif, Video, Still }

    public static string ExtensionFor(Kind kind) => kind switch
    {
        Kind.Gif => ".gif",
        Kind.Video => ".mp4",
        _ => ".png",
    };

    /// <summary>The output kind for a source file extension (a staged file or an upload), or null when unsupported.</summary>
    public static Kind? KindForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".gif" => Kind.Gif,
        ".mp4" or ".webm" or ".mov" or ".mkv" or ".avi" or ".wmv" or ".m4v" or ".mpg" or ".mpeg" => Kind.Video,
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".tif" or ".tiff" => Kind.Still,
        _ => null,
    };

    /// <summary>Error text when no transcode slot frees up within the wait; routes answer it as a 503.</summary>
    public const string BusyMessage = "Busy, try again";

    /// <summary>Bounds concurrent crop transcodes, process-wide; separate from the poster gate.</summary>
    public static readonly SemaphoreSlim Gate = new(2);

    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(10);

    /// <summary>GIF frame rates tried in order: a source's own rate can blow the size cap, so one lower-rate retry follows.</summary>
    private static readonly int[] GifFps = [20, 12];

    /// <summary>
    /// Runs the transcode off any caller lock, behind the transcode gate and with a timeout.
    /// Returns the output bytes, or an error message (including an output over the cap or BusyMessage).
    /// gate, tempDir and gateWait exist so tests can drive busy and cancellation deterministically.
    /// </summary>
    public static async Task<(byte[]? Bytes, string? Error)> RunAsync(
        string sourcePath, Kind kind, string? cropRaw, CancellationToken ct,
        long maxOutputBytes = DeckImageStore.MaxAnimatedBytes,
        SemaphoreSlim? gate = null, string? tempDir = null, TimeSpan? gateWait = null)
    {
        var output = Path.Combine(tempDir ?? Path.GetTempPath(), $"nexus-deck-icon-{Guid.NewGuid():N}{ExtensionFor(kind)}");
        gate ??= Gate;
        try
        {
            if (!await gate.WaitAsync(gateWait ?? GateWait, ct))
            {
                return (null, BusyMessage);
            }
            try
            {
                var rates = kind == Kind.Gif ? GifFps : [0];
                var tooLarge = false;
                foreach (var fps in rates)
                {
                    try { File.Delete(output); }
                    catch { }
                    await MediaImporter.RunFfmpeg(TimeoutSeconds, ct, BuildArgs(sourcePath, kind, cropRaw, output, fps));
                    if (!File.Exists(output) || new FileInfo(output).Length == 0)
                    {
                        return (null, "Unsupported or unreadable media");
                    }
                    tooLarge = new FileInfo(output).Length > maxOutputBytes;
                    if (!tooLarge)
                    {
                        return (await File.ReadAllBytesAsync(output, ct), null);
                    }
                }
                return (null, tooLarge ? "Result too large" : "Unsupported or unreadable media");
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[deck-image] transcode failed: {ex.Message}");
            return (null, "Unsupported or unreadable media");
        }
        finally
        {
            try { File.Delete(output); }
            catch { }
        }
    }

    internal static string[] BuildArgs(string sourcePath, Kind kind, string? cropRaw, string outputPath, int gifFps = 0)
    {
        var crop = CropRect.TryParse(cropRaw, out var rect) ? rect.ToFfmpegCrop() + "," : "";
        var fit = $"scale='min({MaxEdge},iw)':'min({MaxEdge},ih)':force_original_aspect_ratio=decrease:flags=lanczos";
        var args = new List<string> { "-y", "-i", sourcePath };
        switch (kind)
        {
            case Kind.Gif:
                args.AddRange(["-vf", $"{crop}{(gifFps > 0 ? $"fps={gifFps}," : "")}{fit},split[s0][s1];[s0]palettegen=reserve_transparent=1[p];[s1][p]paletteuse=alpha_threshold=128", "-loop", "0", "-t", MaxSeconds.ToString()]);
                break;
            case Kind.Video:
                args.AddRange(["-vf", $"{crop}{fit},scale=trunc(iw/2)*2:trunc(ih/2)*2", "-r", "30",
                    "-c:v", "libx264", "-profile:v", "main", "-preset", "veryfast", "-crf", "23",
                    "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart", "-t", MaxSeconds.ToString()]);
                break;
            default:
                args.AddRange(["-vf", $"{crop}{fit}", "-frames:v", "1"]);
                break;
        }
        args.Add(outputPath);
        return [.. args];
    }
}
