using System;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Peripherals.LianLiFlex;

#if WINDOWS
/// <summary>
/// One Flex screen: the same encrypted-header bulk protocol as the other fan screens, but frames
/// go out at their natural length (no 102 400-byte padding), nothing is sent for a frame rate,
/// and acks are never waited on (the SL-INF screen drops or delays them). Opening checks the
/// screen generation and clears any overlay layers a previous owner left on the display.
/// </summary>
public sealed class FlexLcdTransport : ISlv3LcdTransport, IFlexScreenVersion
{
    private const uint WriteTimeoutMs = 3000;
    private const int FlexLayer3Size = 360;

    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _file;
    private readonly IntPtr _usb;
    private readonly IntPtr _writeEvent;
    private readonly object _io = new();
    private readonly System.Threading.Thread _drain;
    private volatile bool _stop;
    private bool _disposed;

    public FlexLcdTransport(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        PortName = devicePath;
        _file = Slv3WinUsbInterop.CreateFileW(
            devicePath,
            Slv3WinUsbInterop.GENERIC_READ | Slv3WinUsbInterop.GENERIC_WRITE,
            Slv3WinUsbInterop.FILE_SHARE_READ | Slv3WinUsbInterop.FILE_SHARE_WRITE,
            IntPtr.Zero, Slv3WinUsbInterop.OPEN_EXISTING, Slv3WinUsbInterop.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (_file.IsInvalid)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new System.IO.IOException($"CreateFileW failed for {devicePath}: {err}");
        }
        if (!Slv3WinUsbInterop.WinUsb_Initialize(_file, out _usb))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new System.IO.IOException($"WinUsb_Initialize failed for {devicePath}: {err}");
        }
        _writeEvent = Slv3WinUsbInterop.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        var wt = WriteTimeoutMs;
        var rt = 200u;
        Slv3WinUsbInterop.WinUsb_SetPipePolicy(_usb, Slv3LcdProtocol.WritePipeId, Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref wt);
        Slv3WinUsbInterop.WinUsb_SetPipePolicy(_usb, Slv3LcdProtocol.ReadPipeId, Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref rt);
        Slv3WinUsbInterop.WinUsb_ResetPipe(_usb, Slv3LcdProtocol.WritePipeId);
        Slv3WinUsbInterop.WinUsb_ResetPipe(_usb, Slv3LcdProtocol.ReadPipeId);
        _drain = new System.Threading.Thread(DrainLoop) { IsBackground = true, Name = "flex-lcd-drain" };
        _drain.Start();

        WriteHeader(Slv3LcdProtocol.CmdType.CheckNewLcd, 0);
        var clear = FlexPng.TransparentSquare(Slv3LcdProtocol.PanelWidth);
        WritePayload(Slv3LcdProtocol.CmdType.PushPng, clear, 0, 0);
        WritePayload(Slv3LcdProtocol.CmdType.PushLayer3, FlexPng.TransparentSquare(FlexLayer3Size), 0, 0);
    }

    public bool IsOpen => !_disposed && !_file.IsInvalid && !_file.IsClosed;

    public string PortName { get; }

    private const int ErrorSemTimeout = 121;
    private const int MaxConsecutiveReadFailures = 40;

    // Acks are never waited on; the pipe is only kept from backing up. A timeout is normal, other failures back off and end the loop when they persist.
    private void DrainLoop()
    {
        var buf = new byte[1024];
        var failures = 0;
        while (!_stop)
        {
            if (Slv3WinUsbInterop.WinUsb_ReadPipe(_usb, Slv3LcdProtocol.ReadPipeId, buf, (uint)buf.Length, out _, IntPtr.Zero))
            {
                failures = 0;
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

    private static uint Stamp() => (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool PushImage(byte[] jpeg)
    {
        var header = Slv3LcdProtocol.BuildFlexHeader(Slv3LcdProtocol.CmdType.PushJpg, Stamp(), length: jpeg.Length);
        return Write(Slv3LcdProtocol.BuildUnpaddedBuffer(header, jpeg));
    }

    public bool SetBrightness(byte value) => WriteHeader(Slv3LcdProtocol.CmdType.BrigthSet, value);

    public bool SetRotation(byte value) => WriteHeader(Slv3LcdProtocol.CmdType.Rotate, value);

    /// <summary>
    /// The screen's USB product string, which ends in its firmware version. Read from the device
    /// descriptors, not a screen command: the TL screen's command pipe stalls on a version query.
    /// </summary>
    public string? ReadVersion()
    {
        lock (_io)
        {
            if (_disposed)
            {
                return null;
            }
            var device = new byte[18];
            if (!Slv3WinUsbInterop.WinUsb_GetDescriptor(_usb, 1, 0, 0, device, (uint)device.Length, out var got) || got < 18 || device[15] == 0)
            {
                return null;
            }
            var str = new byte[128];
            if (!Slv3WinUsbInterop.WinUsb_GetDescriptor(_usb, 3, device[15], 0x0409, str, (uint)str.Length, out var n) || n < 4)
            {
                return null;
            }
            return System.Text.Encoding.Unicode.GetString(str, 2, (int)n - 2).Trim();
        }
    }

    public bool TryGetPosition(out byte groupIndex)
    {
        groupIndex = 0;
        return false;
    }

    private bool WriteHeader(Slv3LcdProtocol.CmdType cmd, byte arg) =>
        Write(Slv3LcdProtocol.BuildFlexHeader(cmd, Stamp(), arg));

    private bool WritePayload(Slv3LcdProtocol.CmdType cmd, byte[] payload, int x, int y)
    {
        var header = Slv3LcdProtocol.BuildFlexHeader(cmd, Stamp(), length: payload.Length, x: x, y: y);
        return Write(Slv3LcdProtocol.BuildUnpaddedBuffer(header, payload));
    }

    private bool Write(byte[] buffer)
    {
        lock (_io)
        {
            if (_disposed)
            {
                return false;
            }
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            var ov = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<System.Threading.NativeOverlapped>());
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(new System.Threading.NativeOverlapped { EventHandle = _writeEvent }, ov, false);
                Slv3WinUsbInterop.ResetEvent(_writeEvent);
                var ok = Slv3WinUsbInterop.WinUsb_WritePipe(
                    _usb, Slv3LcdProtocol.WritePipeId, pin.AddrOfPinnedObject(), (uint)buffer.Length, out var sent, ov);
                if (!ok && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == (int)Slv3WinUsbInterop.ERROR_IO_PENDING)
                {
                    if (Slv3WinUsbInterop.WaitForSingleObject(_writeEvent, WriteTimeoutMs) != 0)
                    {
                        Slv3WinUsbInterop.WinUsb_AbortPipe(_usb, Slv3LcdProtocol.WritePipeId);
                    }
                    ok = Slv3WinUsbInterop.WinUsb_GetOverlappedResult(_usb, ov, out sent, wait: true);
                }
                return ok && sent == buffer.Length;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(ov);
                pin.Free();
            }
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
        Slv3WinUsbInterop.WinUsb_AbortPipe(_usb, Slv3LcdProtocol.ReadPipeId);
        _drain.Join();
        lock (_io)
        {
            
            Slv3WinUsbInterop.WinUsb_Free(_usb);
            _file.Dispose();
            if (_writeEvent != IntPtr.Zero)
            {
                Slv3WinUsbInterop.CloseHandle(_writeEvent);
            }
        }
    }
}
#endif
