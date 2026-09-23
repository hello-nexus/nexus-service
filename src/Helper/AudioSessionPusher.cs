#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side per-app audio bridge: owns the Core Audio session enumerator in
/// the user's session and pushes <c>audioMixer.snapshot</c> on change.
///
/// Polls rather than subscribes because IAudioSessionNotification /
/// IAudioSessionEvents are inbound COM interfaces, which under AOT would mean
/// hand-building a native vtable for a managed object.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioSessionPusher : IDisposable
{
    // Fast enough for a level meter to look continuous.
    private const int StreamIntervalMs = 100;
    // Idle only has to notice an app appearing or its level changing elsewhere.
    private const int IdleIntervalMs = 1000;

    private readonly HelperOutbound _outbound;
    private readonly WindowsAudioSessionEnumerator _enumerator = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Task _loop;
    /// <summary>Null forces the next pass to push; see SetStreaming.</summary>
    private List<AudioSessionDto>? _last = new();
    private int _streaming;

    public AudioSessionPusher(HelperOutbound outbound)
    {
        _outbound = outbound;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Service asked for (or released) meter-rate sampling.</summary>
    public void SetStreaming(bool enabled)
    {
        Interlocked.Exchange(ref _streaming, enabled ? 1 : 0);
        // Drop the diff baseline so the next pass pushes unconditionally. The
        // helper outlives a service restart, and the service clears its snapshot
        // on disconnect - without this the pusher sees no change and the mixer
        // stays empty until a level happens to move.
        if (enabled) _last = null;
        Wake();
    }

    /// <summary>Writes a strip's level, then re-samples so the push confirms it.</summary>
    public void Apply(string id, double? volume, bool? muted)
    {
        _enumerator.Apply(id, volume, muted);
        Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a pass is already pending */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.Audio))
            {
                try { await _wake.WaitAsync(IdleIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var streaming = Volatile.Read(ref _streaming) == 1;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var sessions = _enumerator.Snapshot();
                sw.Stop();
                if (sw.ElapsedMilliseconds >= HelperPollerDiagnostics.SlowPassMs
                    && HelperPollerDiagnostics.TryFormatSlowPass(
                        HelperPollerDiagnostics.Audio, sw.Elapsed.TotalMilliseconds, sessions.Count, out var slow))
                {
                    Nexus.Service.Platform.HelperLog.Write(slow);
                }
                // Peaks move every sample; carrying them while nobody is
                // watching would turn the idle pass into a push every second.
                if (!streaming)
                {
                    foreach (var s in sessions) s.Peak = 0;
                }
                if (Differs(_last, sessions))
                {
                    _last = sessions;
                    await _outbound.SendAsync(
                        AudioMixerCommands.SnapshotType,
                        new AudioMixerSnapshotPayload { Sessions = sessions },
                        AppJsonContext.Default.AudioMixerSnapshotPayload,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[audio-mixer-pusher] pass failed: {ex.Message}");
            }

            try
            {
                await _wake.WaitAsync(streaming ? StreamIntervalMs : IdleIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Compared at the precision the UI renders (whole percent), so
    /// float dithering never becomes a push.</summary>
    private static bool Differs(List<AudioSessionDto>? a, List<AudioSessionDto> b)
    {
        if (a is null || a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (!string.Equals(x.Id, y.Id, StringComparison.Ordinal)) return true;
            if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal)) return true;
            if (x.Muted != y.Muted || x.Active != y.Active) return true;
            if (x.OnDefault != y.OnDefault) return true;
            if (!x.DeviceIds.SequenceEqual(y.DeviceIds)) return true;
            if (Pct(x.Volume) != Pct(y.Volume)) return true;
            if (Pct(x.Peak) != Pct(y.Peak)) return true;
        }
        return false;
    }

    private static int Pct(double value) => (int)Math.Round(value * 100);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(1000); } catch { }
        _enumerator.Dispose();
        _cts.Dispose();
        _wake.Dispose();
    }
}
#endif
