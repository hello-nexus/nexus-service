using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Panel;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Turns any animation ffmpeg reads into the GIF a Kraken pump animates itself: cover-cropped
/// to the panel like a still image, transparency flattened to black, rotated host-side
/// because the panel does not re-orient what it is sent.
/// </summary>
public static class KrakenGif
{
    /// <summary>Largest upload accepted before conversion.</summary>
    public const long MaxSourceBytes = 32L * 1024 * 1024;

    /// <summary>Largest converted GIF sent to the pump; stays under its image store's capacity.</summary>
    public const int MaxWireBytes = 20 * 1024 * 1024;

    // A longer clip or a faster rate only grows the file the pump has to hold.
    private const string MaxSeconds = "20";
    private const string MaxFps = "25";
    private const int TimeoutSeconds = 60;

    /// <summary>Fits <paramref name="sourcePath"/> to the panel, unrotated.</summary>
    public static async Task<byte[]> FitAsync(string sourcePath, int width, int height, CancellationToken ct)
    {
        var fit = $"[0:v]scale={width}:{height}:force_original_aspect_ratio=increase:flags=lanczos,crop={width}:{height}";
        // Dropping alpha keeps whatever RGB sat under it, so a transparent source is matted
        // onto black. The matte runs at the canvas rate, so only sources that need it pay.
        var graph = await PanelBgImporter.HasTransparencyAsync(sourcePath).ConfigureAwait(false)
            ? $"{fit}[fg];color=black:s={width}x{height}:r={MaxFps}[bg];[bg][fg]overlay=shortest=1"
            : fit;
        return await RunAsync(sourcePath, graph, ct).ConfigureAwait(false);
    }

    /// <summary>Rotates a fitted GIF clockwise by <paramref name="quarterTurns"/>; 0 returns it as is.</summary>
    public static async Task<byte[]> RotateAsync(byte[] gif, int quarterTurns, CancellationToken ct)
    {
        var transpose = (quarterTurns & 0x03) switch
        {
            1 => "transpose=clock",
            2 => "transpose=clock,transpose=clock",
            3 => "transpose=cclock",
            _ => null,
        };
        if (transpose == null)
        {
            return gif;
        }
        var input = Path.Combine(Path.GetTempPath(), $"nexus-kraken-{Guid.NewGuid():N}.gif");
        try
        {
            await File.WriteAllBytesAsync(input, gif, ct).ConfigureAwait(false);
            return await RunAsync(input, $"[0:v]{transpose}", ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(input);
        }
    }

    // graph: a filter_complex chain from [0:v] ending in one unlabelled video output.
    private static async Task<byte[]> RunAsync(string inputPath, string graph, CancellationToken ct)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"nexus-kraken-{Guid.NewGuid():N}");
        var palette = stem + ".png";
        var output = stem + ".gif";
        try
        {
            // Two passes: a one-pass split would buffer every frame until palettegen ends.
            // -fpsmax drops frames from a fast source and never pads a slow one.
            await MediaImporter.RunFfmpeg(TimeoutSeconds, ct, new[]
            {
                "-y", "-v", "error", "-i", inputPath, "-t", MaxSeconds, "-fpsmax", MaxFps,
                "-filter_complex", $"{graph}[x];[x]palettegen=reserve_transparent=0", palette,
            }).ConfigureAwait(false);
            await MediaImporter.RunFfmpeg(TimeoutSeconds, ct, new[]
            {
                "-y", "-v", "error", "-i", inputPath, "-i", palette, "-t", MaxSeconds, "-fpsmax", MaxFps,
                "-filter_complex", $"{graph}[x];[x][1:v]paletteuse",
                "-loop", "0", output,
            }).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(output, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(palette);
            TryDelete(output);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
