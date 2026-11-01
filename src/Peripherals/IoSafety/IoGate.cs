using System;
using System.Diagnostics;
using System.Threading;

namespace Nexus.Service.Peripherals.IoSafety;

/// <summary>
/// Counts the I/O calls in flight on a native handle so teardown can cancel them and wait for each to finish
/// before the handle, event or buffer behind it is freed. Closing a handle under a pending overlapped call
/// leaves the kernel completing into memory the caller has already released.
/// </summary>
public sealed class IoGate
{
    private int _inFlight;
    private volatile bool _closing;

    public bool Closing => _closing;

    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>False once teardown started; the caller must not touch the handle. Every true needs one <see cref="Exit"/>.</summary>
    public bool TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (_closing)
        {
            Interlocked.Decrement(ref _inFlight);
            return false;
        }
        return true;
    }

    public void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <summary>
    /// Stops new entries, runs <paramref name="cancelPending"/> once so blocked calls return, then waits up to
    /// <paramref name="timeoutMs"/> for the in-flight count to reach zero. False means a call is still inside the
    /// handle: the caller must leak it rather than free it.
    /// </summary>
    public bool CloseAndDrain(Action cancelPending, int timeoutMs)
    {
        _closing = true;
        Thread.MemoryBarrier();
        try
        {
            cancelPending();
        }
        catch
        {
            // Best effort: the wait below decides whether the handle is safe to free.
        }
        var sw = Stopwatch.StartNew();
        var spin = new SpinWait();
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                return false;
            }
            if (spin.NextSpinWillYield)
            {
                Thread.Sleep(1);
            }
            else
            {
                spin.SpinOnce();
            }
        }
        return true;
    }
}
