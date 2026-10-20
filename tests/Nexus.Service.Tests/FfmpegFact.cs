using System.Diagnostics;
using Nexus.Service.Platform;

namespace Nexus.Service.Tests;

/// <summary>A <see cref="FactAttribute"/> skipped unless an ffmpeg that can encode h264 resolves on this box, so the extraction path is asserted where it can run and never passes vacuously.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(Probe);

    public FfmpegFactAttribute()
    {
        Skip = SkipReason.Value;
    }

    private static string? Probe()
    {
        if (FfmpegResolver.Path is not { } path)
        {
            return "ffmpeg is not available on this machine.";
        }
        try
        {
            var psi = new ProcessStartInfo(path, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);
            return output.Contains("libx264", StringComparison.Ordinal) ? null : "ffmpeg here has no libx264 encoder to build a test clip.";
        }
        catch (Exception ex)
        {
            return "ffmpeg could not be probed: " + ex.Message;
        }
    }
}
