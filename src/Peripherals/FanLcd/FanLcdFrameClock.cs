using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// Waits until a <see cref="Stopwatch"/> timestamp on a high-resolution waitable timer, without
/// holding a thread. Task.Delay rounds every wait up to the ~15.6 ms system tick, which holds a
/// clip near 30 frames a second and makes its frames uneven; this keeps 60 fps frames 16-17 ms
/// apart. One clock per screen loop: a wait re-arms the clock's single timer.
/// </summary>
internal sealed partial class FanLcdFrameClock : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly TimerHandle? _timer;

    public FanLcdFrameClock()
    {
        if (OperatingSystem.IsWindows())
        {
            _timer = TryCreateTimer();
        }
    }

    [SupportedOSPlatform("windows")]
    private static TimerHandle? TryCreateTimer()
    {
        // Null before Windows 10 1803, which has no high-resolution timers; waits then use Task.Delay.
        var handle = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        return handle == 0 ? null : new TimerHandle(new SafeWaitHandle(handle, ownsHandle: true));
    }

    /// <summary>Returns at <paramref name="deadline"/> (a Stopwatch timestamp), at once if it has passed.</summary>
    public async Task WaitUntilAsync(long deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return;
        }
        // Negative = relative, in 100 ns units.
        var due = -(remaining * 10_000_000 / Stopwatch.Frequency);
        if (_timer is null || !OperatingSystem.IsWindows() || !SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, 0, 0, false))
        {
            await Task.Delay(TimeSpan.FromTicks(-due), ct).ConfigureAwait(false);
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            _timer, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), done, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            using (ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), done))
            {
                await done.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    public void Dispose() => _timer?.Dispose();

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
