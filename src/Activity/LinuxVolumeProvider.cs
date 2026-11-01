using System;
using System.Globalization;
using System.Linq;
using Nexus.Service.Models.Activity;
using Nexus.Service.Platform;

namespace Nexus.Service.Activity;

/// <summary>
/// Linux master-volume provider. Drives the default audio sink through
/// <c>wpctl</c> (PipeWire / WirePlumber - the default on modern desktops and on
/// Bazzite) and falls back to <c>pactl</c> (PulseAudio, also provided by the
/// pipewire-pulse shim). Subprocess-based via <see cref="ShellExecutor"/> - no
/// native deps, AOT-safe - and chosen over the PulseAudio D-Bus API, which
/// needs the frequently-unloaded <c>module-dbus-protocol</c>. The working
/// backend is probed once and cached.
/// </summary>
public sealed class LinuxVolumeProvider : IVolumeProvider
{
    private const string WpctlSink = "@DEFAULT_AUDIO_SINK@";
    private const string PactlSink = "@DEFAULT_SINK@";

    private enum Backend { Unknown, Wpctl, Pactl, None }

    private readonly object _gate = new();
    private Backend _backend = Backend.Unknown;

    public VolumeState GetState()
    {
        if (!OperatingSystem.IsLinux())
            return Unsupported();

        return ResolveBackend() switch
        {
            Backend.Wpctl => WpctlState(),
            Backend.Pactl => PactlState(),
            _ => Unsupported(),
        };
    }

    public void SetVolume(double volume)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var v = Math.Clamp(volume, 0, 1);
        switch (ResolveBackend())
        {
            case Backend.Wpctl:
                RunC("wpctl", "set-volume", WpctlSink, v.ToString("0.###", CultureInfo.InvariantCulture));
                break;
            case Backend.Pactl:
                RunC("pactl", "set-sink-volume", PactlSink, $"{(int)Math.Round(v * 100)}%");
                break;
        }
    }

    public void SetMuted(bool muted)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var flag = muted ? "1" : "0";
        switch (ResolveBackend())
        {
            case Backend.Wpctl:
                RunC("wpctl", "set-mute", WpctlSink, flag);
                break;
            case Backend.Pactl:
                RunC("pactl", "set-sink-mute", PactlSink, flag);
                break;
        }
    }

    private Backend ResolveBackend()
    {
        lock (_gate)
        {
            if (_backend != Backend.Unknown)
                return _backend;
            if (!string.IsNullOrWhiteSpace(RunC("wpctl", "get-volume", WpctlSink)))
                _backend = Backend.Wpctl;
            else if (!string.IsNullOrWhiteSpace(RunC("pactl", "get-sink-volume", PactlSink)))
                _backend = Backend.Pactl;
            else
                return Backend.None; // don't cache - the audio stack may come up after us
            return _backend;
        }
    }

    // Spawn the audio CLI under LC_ALL=C so output (volume scalar, "[MUTED]",
    // "Mute: yes/no") is locale-stable and parseable. `env` is universally present.
    private static string RunC(string tool, params string[] args)
    {
        var argv = new string[args.Length + 2];
        argv[0] = "LC_ALL=C";
        argv[1] = tool;
        Array.Copy(args, 0, argv, 2, args.Length);
        return ShellExecutor.RunAudio("env", argv);
    }

    private static VolumeState WpctlState() => ParseWpctl(RunC("wpctl", "get-volume", WpctlSink));

    private static VolumeState PactlState()
        => ParsePactl(RunC("pactl", "get-sink-volume", PactlSink), RunC("pactl", "get-sink-mute", PactlSink));

    // wpctl get-volume prints: "Volume: 0.65" or "Volume: 0.65 [MUTED]".
    internal static VolumeState ParseWpctl(string output)
    {
        var idx = output.IndexOf("Volume:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return Unsupported();
        var rest = output[(idx + "Volume:".Length)..].Trim();
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var vol))
            return Unsupported();
        return new VolumeState
        {
            Supported = true,
            Volume = Math.Clamp(vol, 0, 1),
            Muted = rest.Contains("[MUTED]", StringComparison.OrdinalIgnoreCase),
        };
    }

    // pactl get-sink-volume prints "Volume: front-left: 42598 / 65% / ...";
    // get-sink-mute prints "Mute: yes" / "Mute: no".
    internal static VolumeState ParsePactl(string volOut, string muteOut)
    {
        var pct = ParseFirstPercent(volOut);
        if (pct is null)
            return Unsupported();
        return new VolumeState
        {
            Supported = true,
            Volume = Math.Clamp(pct.Value / 100.0, 0, 1),
            Muted = muteOut.Contains("yes", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static int? ParseFirstPercent(string text)
    {
        var slash = text.IndexOf('%');
        if (slash < 0)
            return null;
        var start = slash - 1;
        while (start >= 0 && (char.IsDigit(text[start]) || text[start] == '-'))
            start--;
        var span = text.Substring(start + 1, slash - start - 1);
        return int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static VolumeState Unsupported() => new() { Supported = false, Volume = 0, Muted = false };
}
