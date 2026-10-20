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

    /// <summary>
    /// Runs the transcode off any caller lock, behind the shared ffmpeg gate and with a timeout.
    /// Returns the output bytes, or an error message (including an output over the animated cap).
    /// </summary>
    public static async Task<(byte[]? Bytes, string? Error)> RunAsync(string sourcePath, Kind kind, string? cropRaw, CancellationToken ct, long maxOutputBytes = DeckImageStore.MaxAnimatedBytes)
    {
        var output = Path.Combine(Path.GetTempPath(), $"nexus-deck-icon-{Guid.NewGuid():N}{ExtensionFor(kind)}");
        try
        {
            await DeckImageStore.FfmpegGate.WaitAsync(ct);
            try
            {
                await MediaImporter.RunFfmpeg(TimeoutSeconds, ct, BuildArgs(sourcePath, kind, cropRaw, output));
            }
            finally
            {
                DeckImageStore.FfmpegGate.Release();
            }
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                return (null, "Unsupported or unreadable media");
            }
            if (new FileInfo(output).Length > maxOutputBytes)
            {
                return (null, "Result too large");
            }
            return (await File.ReadAllBytesAsync(output, ct), null);
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

    internal static string[] BuildArgs(string sourcePath, Kind kind, string? cropRaw, string outputPath)
    {
        var crop = CropRect.TryParse(cropRaw, out var rect) ? rect.ToFfmpegCrop() + "," : "";
        var fit = $"scale='min({MaxEdge},iw)':'min({MaxEdge},ih)':force_original_aspect_ratio=decrease:flags=lanczos";
        var args = new List<string> { "-y", "-i", sourcePath };
        switch (kind)
        {
            case Kind.Gif:
                args.AddRange(["-vf", $"{crop}{fit},split[s0][s1];[s0]palettegen=reserve_transparent=1[p];[s1][p]paletteuse=alpha_threshold=128", "-loop", "0", "-t", MaxSeconds.ToString()]);
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
