using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;

namespace Nexus.Service.Activity;

/// <summary>
/// Service-side per-app audio provider. The Core Audio session walk runs in the
/// user-session helper (Session 0 enumerates its own empty session set, not the
/// user's), which pushes <c>audioMixer.snapshot</c> envelopes; this caches the
/// latest and forwards writes back through <see cref="AudioMixerCommands"/>.
/// Same shape as <see cref="WindowsMediaProvider"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioSessionProvider : IAudioSessionProvider, IDisposable
{
    private readonly HelperRegistry _helper;
    private readonly object _lock = new();
    private List<AudioSessionDto> _snapshot = new();
    private int _streaming;

    public WindowsAudioSessionProvider(HelperRegistry helper)
    {
        _helper = helper;
        _helper.InboundEnvelope += OnEnvelope;
        _helper.Connected += OnConnected;
        _helper.Disconnected += OnDisconnected;
    }

    /// <summary>No helper means no reader, which the UI reports as unavailable
    /// rather than as nothing playing.</summary>
    public bool Supported => _helper.IsAnyConnected;

    public event Action? SessionsChanged;

    public IReadOnlyList<AudioSessionDto> GetSessions()
    {
        lock (_lock) { return _snapshot; }
    }

    public void SetVolume(string id, double volume)
    {
        if (id.Length == 0) return;
        _ = AudioMixerCommands.SetAsync(_helper, id, Math.Clamp(volume, 0, 1), null);
    }

    public void SetMuted(string id, bool muted)
    {
        if (id.Length == 0) return;
        _ = AudioMixerCommands.SetAsync(_helper, id, null, muted);
    }

    public void StartStreaming()
    {
        Volatile.Write(ref _streaming, 1);
        _ = AudioMixerCommands.StreamAsync(_helper, true);
    }

    public void StopStreaming()
    {
        Volatile.Write(ref _streaming, 0);
        _ = AudioMixerCommands.StreamAsync(_helper, false);
    }

    // Re-assert the rate on every connect: a helper that crashed mid-stream
    // comes back at its idle rate, and the command also makes the helper push a
    // fresh snapshot, which it otherwise sends only on change (a pass that ran
    // before this pipe was up was dropped).
    private void OnConnected(HelperConnection conn) => _ = ReassertStreamingAsync();

    private async Task ReassertStreamingAsync()
    {
        var sent = Volatile.Read(ref _streaming) == 1;
        await AudioMixerCommands.StreamAsync(_helper, sent).ConfigureAwait(false);
        // A subscribe or release that raced this send may have reached the pipe
        // first; the last command the helper sees must carry the current rate.
        var now = Volatile.Read(ref _streaming) == 1;
        if (now != sent) await AudioMixerCommands.StreamAsync(_helper, now).ConfigureAwait(false);
    }

    // Without this the last strips keep answering after the helper dies, so the
    // mixer shows levels for apps that may have closed with it.
    private void OnDisconnected(HelperConnection conn)
    {
        lock (_lock) { _snapshot = new List<AudioSessionDto>(); }
        SessionsChanged?.Invoke();
    }

    /// <summary>Test-only seam: drives the real disconnect handler.</summary>
    internal void DisconnectForTest() => OnDisconnected(null!);

    private void OnEnvelope(HelperConnection conn, HelperEnvelope env)
    {
        if (env.Type != AudioMixerCommands.SnapshotType || env.Payload is null) return;
        try
        {
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.AudioMixerSnapshotPayload);
            if (p is null) return;
            lock (_lock) { _snapshot = p.Sessions; }
            SessionsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio-mixer] snapshot decode failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _helper.InboundEnvelope -= OnEnvelope;
        _helper.Connected -= OnConnected;
        _helper.Disconnected -= OnDisconnected;
    }
}
