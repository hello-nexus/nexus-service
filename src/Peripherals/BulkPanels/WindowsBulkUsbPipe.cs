#if WINDOWS
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// WinUSB bulk pipe pair, generalised from the Kraken's LCD transport: the pipe ids come
/// from the driver rather than being fixed, and a read pipe is supported for the panels
/// that answer.
///
/// The Kraken finds its interface by a model-specific GUID published in the cooler's own
/// MS OS descriptors. None of these panels publishes one we know, so this enumerates the
/// generic USB device interface class and matches VID/PID out of the device path, then
/// lets <c>WinUsb_Initialize</c> decide: it succeeds only where WinUSB is actually bound.
/// </summary>
public sealed class WindowsBulkUsbPipe : IBulkUsbPipe
{
    private const uint TransferTimeoutMs = 5000;
    private const uint WaitObject0 = 0;

    private readonly SafeFileHandle _fileHandle;
    private readonly IntPtr _winUsbHandle;
    private readonly IntPtr _ioEvent;
    private readonly byte _writePipeId;
    private readonly byte _readPipeId;
    private readonly object _ioLock = new();
    private bool _disposed;

    public WindowsBulkUsbPipe(string devicePath, byte writePipeId, byte readPipeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        _writePipeId = writePipeId;
        _readPipeId = readPipeId;

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

        _ioEvent = Slv3WinUsbInterop.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        if (_ioEvent == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            _fileHandle.Dispose();
            throw new IOException($"CreateEventW failed for {devicePath}: {err}");
        }

        var timeout = TransferTimeoutMs;
        Slv3WinUsbInterop.WinUsb_SetPipePolicy(
            _winUsbHandle, _writePipeId, Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);
        // Clears a halt a previous owner (the vendor app) may have left behind; a stalled
        // bulk pipe rejects every transfer with ERROR_BAD_COMMAND until it is reset.
        Slv3WinUsbInterop.WinUsb_ResetPipe(_winUsbHandle, _writePipeId);
        if (_readPipeId != 0)
        {
            Slv3WinUsbInterop.WinUsb_SetPipePolicy(
                _winUsbHandle, _readPipeId, Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);
            Slv3WinUsbInterop.WinUsb_ResetPipe(_winUsbHandle, _readPipeId);
        }
    }

    public bool Write(ReadOnlySpan<byte> data) => Write(_writePipeId, data);

    public bool Write(byte pipeId, ReadOnlySpan<byte> data)
    {
        if (_disposed || data.Length == 0)
        {
            return false;
        }
        var buffer = data.ToArray();
        lock (_ioLock)
        {
            // Re-checked inside the lock: writing through a handle Dispose already freed is
            // an access violation, not an exception.
            return !_disposed && TransferLocked(pipeId, buffer, buffer.Length, TransferTimeoutMs, write: true) == buffer.Length;
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_disposed || _readPipeId == 0 || buffer.Length == 0)
        {
            return -1;
        }
        var scratch = new byte[buffer.Length];
        lock (_ioLock)
        {
            if (_disposed)
            {
                return -1;
            }
            var read = TransferLocked(_readPipeId, scratch, scratch.Length, (uint)Math.Max(1, timeoutMs), write: false);
            if (read > 0)
            {
                scratch.AsSpan(0, read).CopyTo(buffer);
            }
            return read;
        }
    }

    /// <summary>Bytes transferred, 0 on timeout, or -1 on failure.</summary>
    private int TransferLocked(byte pipeId, byte[] buffer, int length, uint timeoutMs, bool write)
    {
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        try
        {
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _ioEvent }, ovPtr, false);
            Slv3WinUsbInterop.ResetEvent(_ioEvent);
            var ok = write
                ? Slv3WinUsbInterop.WinUsb_WritePipe(
                    _winUsbHandle, pipeId, pin.AddrOfPinnedObject(), (uint)length, out var transferred, ovPtr)
                : Slv3WinUsbInterop.WinUsb_ReadPipe(
                    _winUsbHandle, pipeId, pin.AddrOfPinnedObject(), (uint)length, out transferred, ovPtr);

            if (!ok && Marshal.GetLastWin32Error() == (int)Slv3WinUsbInterop.ERROR_IO_PENDING)
            {
                if (Slv3WinUsbInterop.WaitForSingleObject(_ioEvent, timeoutMs) != WaitObject0)
                {
                    Slv3WinUsbInterop.WinUsb_AbortPipe(_winUsbHandle, pipeId);
                    Slv3WinUsbInterop.WinUsb_GetOverlappedResult(_winUsbHandle, ovPtr, out _, wait: true);
                    // A timed-out read is a quiet panel, not a broken one.
                    return write ? -1 : 0;
                }
                ok = Slv3WinUsbInterop.WinUsb_GetOverlappedResult(_winUsbHandle, ovPtr, out transferred, wait: true);
            }
            return ok ? (int)transferred : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(ovPtr);
            pin.Free();
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_ioEvent != IntPtr.Zero)
            {
                Slv3WinUsbInterop.CloseHandle(_ioEvent);
            }
            if (_winUsbHandle != IntPtr.Zero)
            {
                Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            }
            _fileHandle.Dispose();
        }
    }
}

public sealed class WindowsBulkUsbPipeFactory : IBulkUsbPipeFactory
{
    /// <summary>GUID_DEVINTERFACE_USB_DEVICE - every USB device exposes it.</summary>
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId)
    {
        var path = FindDevicePath(vendorId, productId);
        if (path == null)
        {
            return null;
        }
        try
        {
            return new WindowsBulkUsbPipe(path, writePipeId, readPipeId);
        }
        catch (IOException ex)
        {
            // Overwhelmingly the normal case for a cooler still owned by its vendor driver:
            // it enumerates, and WinUSB is not bound, so it can never be opened here.
            ServiceLog.Info(
                $"[bulk-panel] {vendorId:X4}:{productId:X4} is present but not WinUSB-bound: {ex.Message}");
            return null;
        }
    }

    private static string? FindDevicePath(int vendorId, int productId)
    {
        var guid = UsbDeviceInterface;
        var devInfo = Slv3WinUsbInterop.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
            Slv3WinUsbInterop.DIGCF_PRESENT | Slv3WinUsbInterop.DIGCF_DEVICEINTERFACE);
        if (devInfo == (IntPtr)(-1))
        {
            return null;
        }
        try
        {
            var idx = 0u;
            var ifaceData = new Slv3WinUsbInterop.SP_DEVICE_INTERFACE_DATA();
            ifaceData.cbSize = Marshal.SizeOf(ifaceData);
            while (Slv3WinUsbInterop.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, idx, ref ifaceData))
            {
                idx++;
                var path = ReadDevicePath(devInfo, ref ifaceData);
                if (!string.IsNullOrEmpty(path) && Matches(path, vendorId, productId))
                {
                    return path;
                }
            }
            return null;
        }
        finally
        {
            Slv3WinUsbInterop.SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    private static bool Matches(string path, int vendorId, int productId)
    {
        var vidIdx = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var pidIdx = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vidIdx < 0 || pidIdx < 0 || vidIdx + 8 > path.Length || pidIdx + 8 > path.Length)
        {
            return false;
        }
        return int.TryParse(path.AsSpan(vidIdx + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid)
            && int.TryParse(path.AsSpan(pidIdx + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid)
            && vid == vendorId
            && pid == productId;
    }

    private static string ReadDevicePath(IntPtr devInfo, ref Slv3WinUsbInterop.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        Slv3WinUsbInterop.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0)
        {
            return "";
        }
        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!Slv3WinUsbInterop.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detail, required, out _, IntPtr.Zero))
            {
                return "";
            }
            return Marshal.PtrToStringUni(detail + 4) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }
}
#endif
