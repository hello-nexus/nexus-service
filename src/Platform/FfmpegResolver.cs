using System;
using System.Diagnostics;
using System.IO;

namespace Nexus.Service.Platform;

/// <summary>
/// Single source of truth for locating the ffmpeg binary at runtime. The
/// service ships ffmpeg.exe bundled next to the AOT output on Windows, and
/// falls back to the Homebrew default on macOS. All ffmpeg callers should go
/// through this instead of hardcoding a path -- that way when the bundle
/// location changes, one edit fixes every feature (media import, screen
/// mirror, etc.).
/// </summary>
public static class FfmpegResolver
{
    private static readonly FfmpegPathCache Cache = new(Locate, () => Environment.TickCount64);

    public static string? Path => Cache.Path;

    private static string? Locate()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                "ffmpeg.exe",
            }
            : new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg"),
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg",
                "ffmpeg",
            };
        foreach (var path in candidates)
        {
            if (Probe(path))
            {
                return path;
            }
        }
        return null;
    }

    private static bool Probe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Remembers a found path for the process lifetime and a miss for a few minutes, so a missing ffmpeg never costs a process-spawning probe per call.</summary>
internal sealed class FfmpegPathCache(Func<string?> locate, Func<long> tickMs)
{
    private const long MissCacheMs = 5 * 60 * 1000;
    private string? _path;
    private long _missUntil;

    public string? Path
    {
        get
        {
            if (_path is not null)
            {
                return _path;
            }
            var now = tickMs();
            if (now < _missUntil)
            {
                return null;
            }
            _path = locate();
            if (_path is null)
            {
                _missUntil = now + MissCacheMs;
            }
            return _path;
        }
    }
}
