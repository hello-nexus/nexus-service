using System;
using System.Collections.Generic;
#if WINDOWS
using System.IO;
using System.Runtime.InteropServices;
#endif

namespace Nexus.Service.Peripherals.LianLiWireless;

public interface ISlv3LcdDiscovery
{
    IReadOnlyList<Slv3LcdPortInfo> Discover();
}

public sealed class Slv3LcdPortInfo
{
    public required string PortName { get; init; }
    public string Serial { get; init; } = "";
    public int ProductId { get; init; }
}

/// <summary>One open SL-LCD Wireless screen: a WinUSB bulk pipe pair, EP 0x01 OUT / EP 0x81 IN.</summary>
public interface ISlv3LcdTransport : IDisposable
{
    bool IsOpen { get; }
    string PortName { get; }

    /// <summary>Pushes a 400x400 JPEG as one fixed-size bulk write. False on a transport failure.</summary>
    bool PushImage(byte[] jpeg);

    /// <summary>Header-only write, no JPEG payload.</summary>
    bool SetBrightness(byte value);

    /// <summary>Header-only write; value is 0..3 for the four panel rotations.</summary>
    bool SetRotation(byte value);

    /// <summary>Queries GetPosIndex(201); on success, <paramref name="groupIndex"/> is the fan-position group from ack byte 8.</summary>
    bool TryGetPosition(out byte groupIndex);
}

#if WINDOWS
/// <summary>
/// WinUSB-backed transport for one SL-LCD Wireless screen. Reuses
/// <see cref="Slv3WinUsbInterop"/>, the same WinUSB P/Invoke surface as the
/// RF dongles, but the bulk OUT pipe here rejects a NULL-overlapped write with
/// ERROR_BAD_COMMAND, so writes go through a real OVERLAPPED + GetOverlappedResult
/// (the RF interrupt pipes tolerate NULL). A background thread drains the IN
/// endpoint continuously, matching L-Connect's DataReceived reader.
/// </summary>
public sealed class Slv3LcdTransport : ISlv3LcdTransport
{
    // A 102400-byte bulk write is a single WinUsb_WritePipe call; this bounds
    // a stalled write instead of blocking indefinitely.
    private const uint WriteTimeoutMs = 3000;
    // The ack read is optional (plan section 4.1); a short timeout reads a
    // non-acking screen as "no ack" rather than blocking the caller.
    private const int AckReadTimeoutMs = 500;
    private const uint WaitObject0 = 0;

    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _fileHandle;
    private readonly IntPtr _winUsbHandle;
    private readonly IntPtr _writeEvent;
    private readonly object _ioLock = new();
    private bool _disposed;

    // One OUT write at a time (all writes hold _ioLock), so a single reusable
    // event + overlapped buffer suffices.
    private bool WriteOverlappedLocked(byte[] buffer)
    {
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        var ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf<System.Threading.NativeOverlapped>());
        try
        {
            Marshal.StructureToPtr(new System.Threading.NativeOverlapped { EventHandle = _writeEvent }, ovPtr, false);
            Slv3WinUsbInterop.ResetEvent(_writeEvent);
            var ok = Slv3WinUsbInterop.WinUsb_WritePipe(
                _winUsbHandle, Slv3LcdProtocol.WritePipeId, pin.AddrOfPinnedObject(), (uint)buffer.Length, out var transferred, ovPtr);
            if (!ok && Marshal.GetLastWin32Error() == (int)Slv3WinUsbInterop.ERROR_IO_PENDING)
            {
                // On timeout, abort the transfer first so the wait:true reap
                // returns promptly instead of re-blocking past WriteTimeoutMs.
                if (Slv3WinUsbInterop.WaitForSingleObject(_writeEvent, WriteTimeoutMs) != WaitObject0)
                {
                    Slv3WinUsbInterop.WinUsb_AbortPipe(_winUsbHandle, Slv3LcdProtocol.WritePipeId);
                }
                ok = Slv3WinUsbInterop.WinUsb_GetOverlappedResult(_winUsbHandle, ovPtr, out transferred, wait: true);
            }
            return ok && transferred == buffer.Length;
        }
        finally
        {
            Marshal.FreeHGlobal(ovPtr);
            pin.Free();
        }
    }

    public Slv3LcdTransport(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        PortName = devicePath;

        _fileHandle = Slv3WinUsbInterop.CreateFileW(
            devicePath,
            Slv3WinUsbInterop.GENERIC_READ | Slv3WinUsbInterop.GENERIC_WRITE,
            Slv3WinUsbInterop.FILE_SHARE_READ | Slv3WinUsbInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Slv3WinUsbInterop.OPEN_EXISTING,
            Slv3WinUsbInterop.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (_fileHandle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            _fileHandle.Dispose();
            throw new IOException($"CreateFileW failed for {devicePath}: {err}");
        }

        if (!Slv3WinUsbInterop.WinUsb_Initialize(_fileHandle, out _winUsbHandle))
        {
            var err = Marshal.GetLastWin32Error();
            _fileHandle.Dispose();
            throw new IOException($"WinUsb_Initialize failed for {devicePath}: {err}");
        }

        _writeEvent = Slv3WinUsbInterop.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        if (_writeEvent == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            _fileHandle.Dispose();
            throw new IOException($"CreateEventW failed for {devicePath}: {err}");
        }

        var writeTimeout = WriteTimeoutMs;
        var readTimeout = (uint)AckReadTimeoutMs;
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, Slv3LcdProtocol.WritePipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref writeTimeout))
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless-lcd] SetPipePolicy (write timeout) failed for {devicePath}: {Marshal.GetLastWin32Error()}");
        }
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, Slv3LcdProtocol.ReadPipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref readTimeout))
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless-lcd] SetPipePolicy (read timeout) failed for {devicePath}: {Marshal.GetLastWin32Error()}");
        }

        // Clear any halt left on the pipes from L-Connect's prior session; a
        // stalled bulk OUT pipe rejects WinUsb_WritePipe with ERROR_BAD_COMMAND.
        Slv3WinUsbInterop.WinUsb_ResetPipe(_winUsbHandle, Slv3LcdProtocol.WritePipeId);
        Slv3WinUsbInterop.WinUsb_ResetPipe(_winUsbHandle, Slv3LcdProtocol.ReadPipeId);

        // The panel pushes back-pressure/acks on the IN endpoint; if the host does
        // not continuously drain it the pipe backs up and the OUT writes stall
        // (ERROR_BAD_COMMAND). L-Connect's InitDev runs a continuous DataReceived
        // reader for exactly this - mirror it with a background drain thread.
        _draining = true;
        _drainThread = new System.Threading.Thread(DrainLoop) { IsBackground = true, Name = "slv3-lcd-drain" };
        _drainThread.Start();

        // L-Connect's InitDev issues SetFrameRate(120) on open; the panel stays on
        // its boot logo and ignores pushed JPEGs until it receives this.
        WriteArgCommand(Slv3LcdProtocol.CmdType.SetFrameRate, 120);
    }

    private volatile bool _draining;
    private readonly System.Threading.Thread? _drainThread;
    // Sole reader of the IN pipe: TryGetPosition consumes acks from here rather
    // than issuing its own read, which would race the drain for the same frame.
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _rxQueue = new(boundedCapacity: 32);

    // After a read that fails outright, the drain waits this long before the next one,
    // doubling up to the cap, so a pipe in a failing state cannot spin the thread.
    internal const int DrainRetryFirstMs = 10;
    internal const int DrainRetryMaxMs = 1000;

    internal static int NextDrainRetryMs(int previousMs) =>
        previousMs <= 0 ? DrainRetryFirstMs : Math.Min(DrainRetryMaxMs, previousMs * 2);

    private void DrainLoop()
    {
        // The handle is opened FILE_FLAG_OVERLAPPED, and the IN pipe, like the OUT pipe,
        // refuses a NULL-overlapped transfer at once: read through a real OVERLAPPED and
        // wait for it (the pipe's PIPE_TRANSFER_TIMEOUT bounds the wait), as the writes do.
        var buf = new byte[10240];
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        var ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf<System.Threading.NativeOverlapped>());
        var readEvent = Slv3WinUsbInterop.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        if (readEvent == IntPtr.Zero)
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless-lcd] CreateEventW failed for the IN pipe of {PortName}: {Marshal.GetLastWin32Error()}; acks will not be read");
        }
        var retryMs = 0;
        try
        {
            while (_draining && !_disposed && readEvent != IntPtr.Zero)
            {
                Marshal.StructureToPtr(new System.Threading.NativeOverlapped { EventHandle = readEvent }, ovPtr, false);
                Slv3WinUsbInterop.ResetEvent(readEvent);
                // Different pipe (0x81) than writes (0x01) - WinUSB serializes per
                // pipe, so this reads concurrently with a push without _ioLock.
                var ok = Slv3WinUsbInterop.WinUsb_ReadPipe(
                    _winUsbHandle, Slv3LcdProtocol.ReadPipeId, pin.AddrOfPinnedObject(), (uint)buf.Length, out var n, ovPtr);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error == (int)Slv3WinUsbInterop.ERROR_IO_PENDING)
                {
                    ok = Slv3WinUsbInterop.WinUsb_GetOverlappedResult(_winUsbHandle, ovPtr, out n, wait: true);
                    error = ok ? 0 : Marshal.GetLastWin32Error();
                }

                if (ok && n > 0)
                {
                    retryMs = 0;
                    var frame = buf[..(int)n];
                    while (!_rxQueue.TryAdd(frame) && _rxQueue.TryTake(out _))
                    {
                        // Drop the oldest ack to make room for the newest.
                    }
                }
                else if (ok || (error != Slv3WinUsbInterop.ERROR_SEM_TIMEOUT && error != Slv3WinUsbInterop.ERROR_OPERATION_ABORTED))
                {
                    // A timeout already waited and an abort means Dispose; an empty read or any
                    // other error comes back at once, so back off instead of retrying in a tight loop.
                    retryMs = NextDrainRetryMs(retryMs);
                    System.Threading.Thread.Sleep(retryMs);
                }
            }
        }
        finally
        {
            if (readEvent != IntPtr.Zero)
            {
                Slv3WinUsbInterop.CloseHandle(readEvent);
            }
            Marshal.FreeHGlobal(ovPtr);
            pin.Free();
        }
    }

    public bool IsOpen => !_disposed && !_fileHandle.IsInvalid && !_fileHandle.IsClosed;
    public string PortName { get; }

    public bool PushImage(byte[] jpeg)
    {
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var buffer = Slv3LcdProtocol.BuildPushJpgBuffer(timestamp, jpeg);
        lock (_ioLock)
        {
            // The hub can drop this transport from another thread; Dispose frees the handles under this lock.
            if (_disposed)
            {
                return false;
            }
            var ok = WriteOverlappedLocked(buffer);
            if (ok && !_loggedFirstPush)
            {
                _loggedFirstPush = true;
                Nexus.Service.Platform.ServiceLog.Info(
                    $"[lianli-wireless-lcd] PushImage OK ({buffer.Length} bytes)");
            }
            return ok;
        }
    }

    private bool _loggedFirstPush;

    public bool SetBrightness(byte value) => WriteArgCommand(Slv3LcdProtocol.CmdType.BrigthSet, value);

    public bool SetRotation(byte value) => WriteArgCommand(Slv3LcdProtocol.CmdType.Rotate, value);

    public bool TryGetPosition(out byte groupIndex)
    {
        groupIndex = 0;
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = Slv3LcdProtocol.BuildArgCommandHeader(Slv3LcdProtocol.CmdType.GetPosIndex, timestamp, 0);
        lock (_ioLock)
        {
            if (_disposed)
            {
                return false;
            }
            // Discard acks queued before this query so the next frame is ours.
            while (_rxQueue.TryTake(out _))
            {
            }
            if (!WriteOverlappedLocked(header))
            {
                return false;
            }
            if (!_rxQueue.TryTake(out var ack, AckReadTimeoutMs) || ack.Length < 9)
            {
                return false;
            }
            groupIndex = ack[8];
            return true;
        }
    }

    private bool WriteArgCommand(Slv3LcdProtocol.CmdType cmd, byte value)
    {
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = Slv3LcdProtocol.BuildArgCommandHeader(cmd, timestamp, value);
        lock (_ioLock)
        {
            return !_disposed && WriteOverlappedLocked(header);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Stop the drain thread before freeing the handle it reads from. Abort
        // the IN pipe so a blocked ReadPipe returns even if its timeout policy
        // never took, then join before WinUsb_Free (no read in flight = no UAF).
        _draining = false;
        Slv3WinUsbInterop.WinUsb_AbortPipe(_winUsbHandle, Slv3LcdProtocol.ReadPipeId);
        _drainThread?.Join();
        lock (_ioLock)
        {
            _rxQueue.Dispose();
            if (_winUsbHandle != IntPtr.Zero)
            {
                Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            }
            _fileHandle.Dispose();
            if (_writeEvent != IntPtr.Zero)
            {
                Slv3WinUsbInterop.CloseHandle(_writeEvent);
            }
        }
    }
}
#else
/// <summary>Inert: the LCD screens are Windows-only for v1.</summary>
public sealed class Slv3LcdTransport : ISlv3LcdTransport
{
    public Slv3LcdTransport(string devicePath)
    {
        PortName = devicePath;
    }

    public bool IsOpen => false;
    public string PortName { get; }
    public bool PushImage(byte[] jpeg) => false;
    public bool SetBrightness(byte value) => false;
    public bool SetRotation(byte value) => false;

    public bool TryGetPosition(out byte groupIndex)
    {
        groupIndex = 0;
        return false;
    }

    public void Dispose()
    {
    }
}
#endif
