using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform;

/// <summary>
/// Tracks ffmpeg subprocesses spawned by this service (screen mirror, beats)
/// so they can be cleaned up if the service crashes without disposing them.
///
/// On startup, CleanupOrphans() reads stale PIDs from the tracker file and
/// kills any that are still running ffmpeg. Only PIDs we recorded are touched
/// - the user's unrelated ffmpeg work is never affected.
/// </summary>
public static class FfmpegTracker
{
    private static readonly string PidFilePath = Path.Combine(
        GetConfigDir(), "ffmpeg-pids.txt");

    private static readonly object Lock = new();

    /// <summary>Record a spawned ffmpeg PID so it can be cleaned up on crash recovery.</summary>
    public static void Track(int pid)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
                File.AppendAllText(PidFilePath, pid + Environment.NewLine);
            }
        }
        catch { /* non-critical */ }
    }

    /// <summary>Remove a PID when the process is cleanly disposed.</summary>
    public static void Untrack(int pid)
    {
        try
        {
            lock (Lock)
            {
                if (!File.Exists(PidFilePath))
                {
                    return;
                }

                var lines = File.ReadAllLines(PidFilePath);
                var remaining = new System.Collections.Generic.List<string>();
                foreach (var line in lines)
                {
                    if (int.TryParse(line.Trim(), out var p) && p != pid)
                    {
                        remaining.Add(line.Trim());
                    }
                }
                if (remaining.Count > 0)
                {
                    File.WriteAllLines(PidFilePath, remaining);
                }
                else
                {
                    File.Delete(PidFilePath);
                }
            }
        }
        catch { /* non-critical */ }
    }

    /// <summary>Kill any tracked ffmpeg PIDs from a previous crash, then clear the file.</summary>
    public static void CleanupOrphans()
    {
        try
        {
            lock (Lock)
            {
                if (!File.Exists(PidFilePath))
                {
                    return;
                }

                var lines = File.ReadAllLines(PidFilePath);
                foreach (var line in lines)
                {
                    if (!int.TryParse(line.Trim(), out var pid))
                    {
                        continue;
                    }

                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        // Verify it's actually ffmpeg (PID could have been reused)
                        if (proc.ProcessName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                        {
                            proc.Kill();
                            Console.WriteLine($"[nexus-service] killed orphan ffmpeg (pid {pid})");
                        }
                        proc.Dispose();
                    }
                    catch { /* process already gone or access denied - fine */ }
                }

                File.Delete(PidFilePath);
            }
        }
        catch { /* non-critical */ }
    }

    private static string GetConfigDir()
    {
        if (Persistence.NexusDataPaths.SystemDaemonRoot is { } daemonRoot)
            return daemonRoot;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Nexus");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(xdg, "Nexus");
    }
}
