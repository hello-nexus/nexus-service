using System;
using System.Collections.Generic;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Per-application audio levels: one strip per process, collapsing every
/// session across every active render endpoint (see
/// <see cref="AudioSessionDto.DeviceIds"/> and <see cref="AudioSessionDto.OnDefault"/>).
/// Separate from <see cref="IVolumeProvider"/> (an endpoint's own master
/// level) and <see cref="IAudioDeviceProvider"/> (which endpoint is default).
/// </summary>
public interface IAudioSessionProvider
{
    /// <summary>False where per-app levels cannot be read at all (macOS has no
    /// API for it) or where the reader is not up yet (no user-session helper).</summary>
    bool Supported { get; }

    IReadOnlyList<AudioSessionDto> GetSessions();

    /// <summary>Applies to every session the strip covers. <paramref name="volume"/> is 0-1.</summary>
    void SetVolume(string id, double volume);

    void SetMuted(string id, bool muted);

    /// <summary>Raise the sampling rate to something a level meter can animate.
    /// Ref-counted by the caller, not here: <see cref="StopStreaming"/> drops
    /// straight back to the idle rate.</summary>
    void StartStreaming();

    void StopStreaming();

    /// <summary>A strip appeared, vanished, or changed level/peak.</summary>
    event Action? SessionsChanged;
}

/// <summary>Platforms with no per-app level control. Reports unsupported and
/// swallows writes so callers need no per-OS branch.</summary>
public sealed class StubAudioSessionProvider : IAudioSessionProvider
{
    private static readonly IReadOnlyList<AudioSessionDto> None = Array.Empty<AudioSessionDto>();

    public bool Supported => false;
    public IReadOnlyList<AudioSessionDto> GetSessions() => None;
    public void SetVolume(string id, double volume) { }
    public void SetMuted(string id, bool muted) { }
    public void StartStreaming() { }
    public void StopStreaming() { }

#pragma warning disable CS0067 // never raised: this provider has no state to change
    public event Action? SessionsChanged;
#pragma warning restore CS0067
}
