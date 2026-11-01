using System;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Peripherals.IoSafety;

/// <summary>
/// Open and close rules for <see cref="SerialPort"/> on Windows. Its stream runs a comm-event loop on a
/// long-running task that holds the port handle through a weak reference to the stream. Two paths end that
/// loop on a handle already closed, so it throws ObjectDisposedException in <c>GetOverlappedResult</c> with no one
/// awaiting the task: the stream is finalized without Dispose (the finalizer closes the handle and never waits
/// for the loop), or Dispose fails on a removed USB device (the call to drop DTR is refused, Dispose throws
/// before it reaches the wait for the loop, and the handle closes anyway).
/// </summary>
internal static class SerialPortSafety
{
    // The stream type is internal to System.IO.Ports, so the accessor names it by string.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_waitForComEventTask")]
    private static extern ref Task LoopTaskOf([UnsafeAccessorType("System.IO.Ports.SerialStream, System.IO.Ports")] object stream);

    /// <summary>Opens the port and clears stale bytes. A failure after the open closes the port here: an open port that nobody holds again is finalized, which is the first path above.</summary>
    internal static void OpenAndClear(SerialPort port)
    {
        port.Open();
        try
        {
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch
        {
            Close(port);
            throw;
        }
    }

    /// <summary>Closes and disposes the port, reading any fault the event loop raises while the handle goes away. Never throws.</summary>
    internal static void Close(SerialPort port)
    {
        if (OperatingSystem.IsWindows())
        {
            ObserveEventLoop(port);
        }
        try { port.Close(); } catch { /* removed device: Dispose throws, the handle is closed regardless */ }
        try { port.Dispose(); } catch { }
    }

    private static void ObserveEventLoop(SerialPort port)
    {
        try
        {
            if (port.IsOpen)
            {
                var loop = LoopTaskOf(port.BaseStream);
                _ = loop.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        catch
        {
            // Layout changed or the port already closed: ErrorNoise still filters this row.
        }
    }
}
