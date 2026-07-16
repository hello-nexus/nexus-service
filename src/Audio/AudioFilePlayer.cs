using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Audio;

/// <summary>
/// Fire-and-forget native audio file playback for the deck "playAudio"
/// action. One sound plays at a time per platform - starting a new one
/// stops whichever is still playing. Never throws to the caller: a bad path,
/// missing player, or platform quirk is logged and swallowed so a key
/// dispatch can never crash on this.
/// </summary>
public sealed partial class AudioFilePlayer
{
    private const int DefaultVolumePercent = 100;
    private readonly object _lock = new();
    private Process? _externalProcess;

    public void Play(string? path, int? volumePercent)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        if (!File.Exists(path))
        {
            ServiceLog.Warn($"[audio-player] file not found: {path}");
            return;
        }

        var volume = Math.Clamp(volumePercent ?? DefaultVolumePercent, 0, 100);
        lock (_lock)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    PlayWindows(path, volume);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    PlayViaExternalProcess("afplay", BuildMacArgs(path, volume));
                }
                else if (OperatingSystem.IsLinux())
                {
                    PlayLinux(path);
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[audio-player] playback failed: {ex.Message}");
            }
        }
    }

    private static string[] BuildMacArgs(string path, int volumePercent) =>
        new[] { "-v", (volumePercent / 100.0).ToString(CultureInfo.InvariantCulture), path };

    /// <summary>Best-effort: paplay first (respects PulseAudio/PipeWire routing), then aplay (ALSA); a no-op when neither is on PATH.</summary>
    private void PlayLinux(string path)
    {
        var player = IsOnPath("paplay") ? "paplay" : IsOnPath("aplay") ? "aplay" : null;
        if (player is null)
        {
            ServiceLog.Warn("[audio-player] neither paplay nor aplay found on PATH - playback unavailable.");
            return;
        }
        PlayViaExternalProcess(player, new[] { path });
    }

    private static bool IsOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Caller must hold _lock. Kills whichever external player process is still running, then spawns the new one.</summary>
    private void PlayViaExternalProcess(string fileName, string[] args)
    {
        StopExternalProcess();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        _externalProcess = Process.Start(psi);
    }

    private void StopExternalProcess()
    {
        if (_externalProcess is null)
        {
            return;
        }
        try
        {
            if (!_externalProcess.HasExited)
            {
                _externalProcess.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the HasExited check and Kill - not an error.
        }
        finally
        {
            _externalProcess.Dispose();
            _externalProcess = null;
        }
    }

    /// <summary>
    /// winmm MCI: close any prior alias, open the new file (device type
    /// picked from the extension), apply volume, then play. Every step is
    /// fire-and-forget - a failed close/open/setaudio call is not fatal to
    /// the next step, matching MCI's own tolerant command-string design.
    /// </summary>
    private static void PlayWindows(string path, int volumePercent)
    {
        // The path is interpolated into the quoted MCI open-command token; a
        // double quote is the only char that could break out of it. It is also
        // an invalid Windows path char (so File.Exists already rejects it), but
        // guard explicitly so PlayWindows is safe regardless of the call path.
        if (path.Contains('"'))
        {
            return;
        }
        SendMci($"close {MciAlias}");
        var deviceType = string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)
            ? "waveaudio"
            : "mpegvideo";
        if (SendMci($"open \"{path}\" type {deviceType} alias {MciAlias}") != 0)
        {
            return;
        }
        var mciVolume = volumePercent * 10; // MCI volume range is 0-1000.
        SendMci($"setaudio {MciAlias} volume to {mciVolume}");
        SendMci($"play {MciAlias}");
    }

    private const string MciAlias = "nexusdeck";

    private static int SendMci(string command) => mciSendStringW(command, IntPtr.Zero, 0, IntPtr.Zero);

    // mciSendStringW is A/W-suffixed on winmm.dll; LibraryImport never probes
    // the suffix itself (unlike DllImport's ExactSpelling=false), so the
    // entry point must be named explicitly.
    [LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "mciSendStringW")]
    private static partial int mciSendStringW(string command, IntPtr returnValue, uint returnLength, IntPtr callback);
}
