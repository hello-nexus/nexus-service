using System;
using System.IO;
using System.Text;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>A dedicated log for the Glacial LCD upload path, rotated across two files so the pair stays under a fixed size; the support bundle takes every *.log in the logs directory.</summary>
public static class GlacialLcdLog
{
    public const string FileName = "glacial-lcd.log";
    public const string RotatedFileName = "glacial-lcd.1.log";

    private static readonly object Gate = new();
    private static string? _dirOverride;
    private static long _maxFileBytes = 5L * 1024 * 1024;

    /// <summary>Test seam: logs into this directory with this per-file cap.</summary>
    internal static void UseDirectory(string? dir, long maxFileBytes = 5L * 1024 * 1024)
    {
        lock (Gate)
        {
            _dirOverride = dir;
            _maxFileBytes = maxFileBytes;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string dir = _dirOverride ?? ServiceLog.LogsDirectory;
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, FileName);
                var bytes = Encoding.UTF8.GetBytes($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {message}\n");
                var info = new FileInfo(path);
                if (info.Exists && info.Length + bytes.Length > _maxFileBytes)
                {
                    File.Move(path, Path.Combine(dir, RotatedFileName), overwrite: true);
                }
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception)
        {
        }
    }
}
