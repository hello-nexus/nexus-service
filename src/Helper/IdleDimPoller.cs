#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper;

/// <summary>
/// Reports whether the session has been idle past a threshold, for idle dim.
/// Like <see cref="LockInputPoller"/> it reads only <c>GetLastInputInfo</c>, the
/// session's last-input tick: no hook, no raw input, no device, no key
/// identity. It exists only while armed, polls once a second, and sends the
/// state when it changes (plus once on arm, so a service that restarted or a
/// helper that reconnected agrees with it).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IdleDimPoller : IDisposable
{
    private const int PollPeriodMs = 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly HelperOutbound _outbound;
    private readonly object _gate = new();
    private JitteredPeriodicTimer? _timer;
    private int _thresholdSeconds;
    private bool? _reported;

    public IdleDimPoller(HelperOutbound outbound) => _outbound = outbound;

    /// <summary>Arm with a threshold in seconds, or 0 to stop. Re-arming reports the current state at once.</summary>
    public void SetThreshold(int thresholdSeconds)
    {
        lock (_gate)
        {
            _thresholdSeconds = thresholdSeconds;
            _reported = null;
            if (thresholdSeconds <= 0)
            {
                _timer?.Dispose();
                _timer = null;
                return;
            }
            _timer ??= new JitteredPeriodicTimer(PollPeriodMs, jitterMs: 0, Poll);
        }
        Poll();
    }

    private void Poll()
    {
        try
        {
            bool idle;
            lock (_gate)
            {
                if (_thresholdSeconds <= 0) return;
                var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                // A failed call must not read as "idle for ever".
                if (!GetLastInputInfo(ref lii)) return;
                // Unchecked on purpose: both ticks wrap together every 49 days.
                var idleMs = unchecked((uint)Environment.TickCount - lii.dwTime);
                idle = idleMs / 1000 >= (uint)_thresholdSeconds;
                if (_reported == idle) return;
                _reported = idle;
            }
            _ = _outbound.SendAsync(
                type: IdleDimCommands.IdleStateType,
                payload: new IdleStatePayload { Idle = idle },
                payloadType: AppJsonContext.Default.IdleStatePayload);
        }
        catch { /* best-effort; the next poll is a second away */ }
    }

    public void Dispose() => SetThreshold(0);
}
#endif
