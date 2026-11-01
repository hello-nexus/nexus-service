using System;
using System.Threading;

namespace Nexus.Service.Peripherals.IoSafety;

/// <summary>
/// Counts the I/O calls in flight on a native handle so teardown can cancel them and free the handle, event or
/// buffer only after the last one has left. Closing a handle under a pending overlapped call leaves the kernel
/// completing into memory the caller has already released. Teardown never blocks: whichever of
/// <see cref="Close"/> or the last <see cref="Exit"/> sees the gate closing and empty runs the release, once.
/// </summary>
public sealed class IoGate
{
    private int _inFlight;
    private int _released;
    private volatile bool _closing;
    private Action? _release;

    public bool Closing => _closing;

    public bool Released => Volatile.Read(ref _released) != 0;

    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>False once teardown started; the caller must not touch the handle. Every true needs one <see cref="Exit"/>.</summary>
    public bool TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (_closing)
        {
            Exit();
            return false;
        }
        return true;
    }

    public void Exit()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && _closing)
        {
            Release();
        }
    }

    /// <summary>
    /// Stops new entries and runs <paramref name="cancelPending"/> once so blocked calls return. <paramref name="release"/>
    /// runs when no call is inside the handle: now if none is, otherwise on the last exit. It never runs while a call is in flight.
    /// </summary>
    public void Close(Action cancelPending, Action release)
    {
        _release = release;
        _closing = true;
        Thread.MemoryBarrier();
        try
        {
            cancelPending();
        }
        catch
        {
            // Best effort: the release below is gated on the in-flight count, not on the cancel.
        }
        if (Volatile.Read(ref _inFlight) == 0)
        {
            Release();
        }
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }
        try
        {
            _release?.Invoke();
        }
        catch
        {
            // Closing a native handle must never throw into an I/O thread.
        }
    }
}
