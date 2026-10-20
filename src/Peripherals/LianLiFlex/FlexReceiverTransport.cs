using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>One open receiver: 64-byte bulk frames out, replies read back in arrival order.</summary>
public interface IFlexReceiverTransport : IDisposable
{
    bool IsOpen { get; }

    /// <summary>Writes one 64-byte frame; false on a transport failure.</summary>
    bool Write(byte[] frame);

    /// <summary>Takes the next reply, or null when none arrives within the timeout.</summary>
    byte[]? Read(int timeoutMs);

    /// <summary>Drops replies that arrived before now, so the next read answers the next write.</summary>
    void DiscardReplies();
}

#if WINDOWS
/// <summary>
/// WinUSB receiver pipe pair (EP 0x01 OUT, EP 0x81 IN, 64-byte bulk). Writes go through a real
/// OVERLAPPED like the screen transport; a background thread drains the IN pipe so streamed frames,
/// which elicit status replies, never back it up.
/// </summary>
public sealed class FlexReceiverTransport : IFlexReceiverTransport
{
    private const uint WriteTimeoutMs = 2000;
    private const uint ReadTimeoutMs = 100;

    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _file;
    private readonly IntPtr _usb;
    private readonly IntPtr _writeEvent;
    private readonly object _writeLock = new();
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _replies = new(boundedCapacity: 64);
    private readonly System.Threading.Thread _drain;
    private volatile bool _stop;
    private bool _disposed;

    public FlexReceiverTransport(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        _file = LianLiWireless.Slv3WinUsbInterop.CreateFileW(
            devicePath,
            LianLiWireless.Slv3WinUsbInterop.GENERIC_READ | LianLiWireless.Slv3WinUsbInterop.GENERIC_WRITE,
            LianLiWireless.Slv3WinUsbInterop.FILE_SHARE_READ | LianLiWireless.Slv3WinUsbInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            LianLiWireless.Slv3WinUsbInterop.OPEN_EXISTING,
            LianLiWireless.Slv3WinUsbInterop.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (_file.IsInvalid)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new System.IO.IOException($"CreateFileW failed for {devicePath}: {err}");
        }
        if (!LianLiWireless.Slv3WinUsbInterop.WinUsb_Initialize(_file, out _usb))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new System.IO.IOException($"WinUsb_Initialize failed for {devicePath}: {err}");
        }
        _writeEvent = LianLiWireless.Slv3WinUsbInterop.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        var wt = WriteTimeoutMs;
        var rt = ReadTimeoutMs;
        LianLiWireless.Slv3WinUsbInterop.WinUsb_SetPipePolicy(_usb, FlexProtocol.WritePipeId, LianLiWireless.Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref wt);
        LianLiWireless.Slv3WinUsbInterop.WinUsb_SetPipePolicy(_usb, FlexProtocol.ReadPipeId, LianLiWireless.Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref rt);
        LianLiWireless.Slv3WinUsbInterop.WinUsb_ResetPipe(_usb, FlexProtocol.WritePipeId);
        LianLiWireless.Slv3WinUsbInterop.WinUsb_ResetPipe(_usb, FlexProtocol.ReadPipeId);
        _drain = new System.Threading.Thread(DrainLoop) { IsBackground = true, Name = "flex-rx-drain" };
        _drain.Start();
    }

    public bool IsOpen => !_disposed && !_file.IsInvalid && !_file.IsClosed;

    private const int ErrorSemTimeout = 121;
    private const int MaxConsecutiveReadFailures = 40;

    // A read timeout is normal (nothing to say); any other failure backs off and ends the loop when it persists, so a vanished device does not spin a core.
    private void DrainLoop()
    {
        var buf = new byte[FlexProtocol.FrameSize];
        var failures = 0;
        while (!_stop)
        {
            if (LianLiWireless.Slv3WinUsbInterop.WinUsb_ReadPipe(_usb, FlexProtocol.ReadPipeId, buf, (uint)buf.Length, out var n, IntPtr.Zero))
            {
                failures = 0;
                if (n > 0)
                {
                    var frame = buf[..(int)n];
                    while (!_stop && !_replies.TryAdd(frame) && _replies.TryTake(out _))
                    {
                    }
                }
            }
            else if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != ErrorSemTimeout)
            {
                if (++failures >= MaxConsecutiveReadFailures)
                {
                    break;
                }
                System.Threading.Thread.Sleep(25);
            }
        }
    }

    public bool Write(byte[] frame)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return false;
            }
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(frame, System.Runtime.InteropServices.GCHandleType.Pinned);
            var ov = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<System.Threading.NativeOverlapped>());
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(new System.Threading.NativeOverlapped { EventHandle = _writeEvent }, ov, false);
                LianLiWireless.Slv3WinUsbInterop.ResetEvent(_writeEvent);
                var ok = LianLiWireless.Slv3WinUsbInterop.WinUsb_WritePipe(
                    _usb, FlexProtocol.WritePipeId, pin.AddrOfPinnedObject(), (uint)frame.Length, out var sent, ov);
                if (!ok && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == (int)LianLiWireless.Slv3WinUsbInterop.ERROR_IO_PENDING)
                {
                    if (LianLiWireless.Slv3WinUsbInterop.WaitForSingleObject(_writeEvent, WriteTimeoutMs) != 0)
                    {
                        LianLiWireless.Slv3WinUsbInterop.WinUsb_AbortPipe(_usb, FlexProtocol.WritePipeId);
                    }
                    ok = LianLiWireless.Slv3WinUsbInterop.WinUsb_GetOverlappedResult(_usb, ov, out sent, wait: true);
                }
                return ok && sent == frame.Length;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(ov);
                pin.Free();
            }
        }
    }

    public byte[]? Read(int timeoutMs) => !_disposed && _replies.TryTake(out var f, timeoutMs) ? f : null;

    public void DiscardReplies()
    {
        while (_replies.TryTake(out _))
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stop = true;
        LianLiWireless.Slv3WinUsbInterop.WinUsb_AbortPipe(_usb, FlexProtocol.ReadPipeId);
        _drain.Join();
        lock (_writeLock)
        {
            _replies.Dispose();
            LianLiWireless.Slv3WinUsbInterop.WinUsb_Free(_usb);
            _file.Dispose();
            if (_writeEvent != IntPtr.Zero)
            {
                LianLiWireless.Slv3WinUsbInterop.CloseHandle(_writeEvent);
            }
        }
    }
}
#endif
