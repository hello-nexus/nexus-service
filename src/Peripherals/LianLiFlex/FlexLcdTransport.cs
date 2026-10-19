using System;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Peripherals.LianLiFlex;

#if WINDOWS
/// <summary>
/// One Flex screen: the same encrypted-header bulk protocol as the other fan screens, but frames
/// go out at their natural length (no 102 400-byte padding), nothing is sent for a frame rate,
/// and acks are never waited on (the SL-INF screen drops or delays them). Opening checks the
/// screen generation and clears any overlay layers a previous owner left on the glass.
/// </summary>
public sealed class FlexLcdTransport : ISlv3LcdTransport
{
    private const uint WriteTimeoutMs = 3000;

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
    }

    public bool IsOpen => !_disposed && !_file.IsInvalid && !_file.IsClosed;

    public string PortName { get; }

    private void DrainLoop()
    {
        var buf = new byte[1024];
        while (!_stop)
        {
            Slv3WinUsbInterop.WinUsb_ReadPipe(_usb, Slv3LcdProtocol.ReadPipeId, buf, (uint)buf.Length, out _, IntPtr.Zero);
        }
    }

    private static uint Stamp() => (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool PushImage(byte[] jpeg)
    {
        var header = Slv3LcdProtocol.EncryptHeader(Slv3LcdProtocol.BuildLengthHeader(Slv3LcdProtocol.CmdType.PushJpg, Stamp(), jpeg.Length));
        return Write(Slv3LcdProtocol.BuildUnpaddedBuffer(header, jpeg));
    }

    public bool SetBrightness(byte value) => WriteHeader(Slv3LcdProtocol.CmdType.BrigthSet, value);

    public bool SetRotation(byte value) => WriteHeader(Slv3LcdProtocol.CmdType.Rotate, value);

    public bool TryGetPosition(out byte groupIndex)
    {
        groupIndex = 0;
        return false;
    }

    private bool WriteHeader(Slv3LcdProtocol.CmdType cmd, byte arg) =>
        Write(Slv3LcdProtocol.BuildArgCommandHeader(cmd, Stamp(), arg));

    private bool WritePayload(Slv3LcdProtocol.CmdType cmd, byte[] payload, int x, int y)
    {
        var plain = cmd == Slv3LcdProtocol.CmdType.PushLayer3
            ? Slv3LcdProtocol.BuildLayerHeader(cmd, Stamp(), payload.Length, x, y)
            : Slv3LcdProtocol.BuildLengthHeader(cmd, Stamp(), payload.Length);
        return Write(Slv3LcdProtocol.BuildUnpaddedBuffer(Slv3LcdProtocol.EncryptHeader(plain), payload));
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
