using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Lighting.Engine;

/// <summary>
/// Absolute-deadline frame ticker on a high-resolution waitable timer. The
/// default timer queue rounds each wait up to a whole system tick, so a period
/// that is not a tick multiple alternates between two tick counts and some
/// frames are held visibly longer than others.
/// </summary>
internal sealed partial class HighResolutionTicker : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly TimerHandle _timer;
    private readonly CancellationToken _ct;
    private WaitHandle[]? _wake;
    private long _deadline;

    private HighResolutionTicker(SafeWaitHandle handle, CancellationToken ct)
    {
        _timer = new TimerHandle(handle);
        _ct = ct;
    }

    /// <summary>Null when the OS has no high-resolution timers (before Windows 10 1803).</summary>
    [SupportedOSPlatform("windows")]
    public static HighResolutionTicker? TryCreate(CancellationToken ct)
    {
        var handle = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        return handle == 0 ? null : new HighResolutionTicker(new SafeWaitHandle(handle, ownsHandle: true), ct);
    }

    /// <summary>Blocks until the next deadline on a fixed grid; false once cancelled or disposed.</summary>
    public bool WaitForNextTick(int periodMs)
    {
        if (_ct.IsCancellationRequested) return false;
        var now = Stopwatch.GetTimestamp();
        var period = periodMs * Stopwatch.Frequency / 1000;
        // A deadline a whole period behind restarts the grid instead of bursting to catch up.
        _deadline = _deadline == 0 || now - _deadline > period ? now + period : _deadline + period;
        // Negative = relative, in 100 ns units.
        var due = -((_deadline - now) * 10_000_000 / Stopwatch.Frequency);
        try
        {
            if (due < 0)
            {
                if (SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, 0, 0, false))
                {
                    WaitHandle.WaitAny(_wake ??= new[] { _timer, _ct.WaitHandle });
                }
                else
                {
                    _ct.WaitHandle.WaitOne(periodMs);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Engine Dispose tears down the token source without joining the loop.
            return false;
        }
        return !_ct.IsCancellationRequested;
    }

    public void Dispose() => _timer.Dispose();

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        SafeWaitHandle timer, ref long dueTime, int period, nint completion, nint arg, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
