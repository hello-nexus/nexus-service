#if WINDOWS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
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
/// interface GUIDs the registry records for the device's interfaces plus the generic USB
/// device interface class, matches VID/PID out of the device path, then lets
/// <c>WinUsb_Initialize</c> decide: it succeeds only where WinUSB is actually bound.
/// </summary>
public sealed unsafe class WindowsBulkUsbPipe : IBulkUsbPipe
{
    private const uint TransferTimeoutMs = 5000;
    private const uint WaitObject0 = 0;

    private readonly SafeFileHandle _fileHandle;
    private readonly IntPtr _winUsbHandle;
    private readonly IntPtr _ioEvent;
    private readonly IntPtr _readEvent;
    private readonly byte _writePipeId;
    private readonly byte _readPipeId;
    private readonly object _ioLock = new();
    // Reads wait on their own event and lock, so a read parked on a quiet IN pipe never
    // holds up frame writes; WinUSB takes overlapped transfers on different pipes at once.
    private readonly object _readLock = new();
    private bool _disposed;

    public int ProductId { get; }

    public WindowsBulkUsbPipe(string devicePath, byte writePipeId, byte readPipeId, int productId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        ProductId = productId;
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
        _readEvent = Slv3WinUsbInterop.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        if (_readEvent == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Slv3WinUsbInterop.CloseHandle(_ioEvent);
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
        lock (_ioLock)
        {
            // Re-checked inside the lock: writing through a handle Dispose already freed is
            // an access violation, not an exception.
            fixed (byte* p = data)
            {
                return !_disposed && TransferLocked(pipeId, _ioEvent, (IntPtr)p, data.Length, TransferTimeoutMs, write: true) == data.Length;
            }
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_disposed || _readPipeId == 0 || buffer.Length == 0)
        {
            return -1;
        }
        lock (_readLock)
        {
            if (_disposed)
            {
                return -1;
            }
            fixed (byte* p = buffer)
            {
                return TransferLocked(_readPipeId, _readEvent, (IntPtr)p, buffer.Length, (uint)Math.Max(1, timeoutMs), write: false);
            }
        }
    }

    /// <summary>Bytes transferred, 0 on timeout, or -1 on failure. <paramref name="buffer"/>
    /// must stay pinned until this returns; every path waits out the transfer first.</summary>
    private int TransferLocked(byte pipeId, IntPtr ioEvent, IntPtr buffer, int length, uint timeoutMs, bool write)
    {
        var ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        try
        {
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = ioEvent }, ovPtr, false);
            Slv3WinUsbInterop.ResetEvent(ioEvent);
            var ok = write
                ? Slv3WinUsbInterop.WinUsb_WritePipe(
                    _winUsbHandle, pipeId, buffer, (uint)length, out var transferred, ovPtr)
                : Slv3WinUsbInterop.WinUsb_ReadPipe(
                    _winUsbHandle, pipeId, buffer, (uint)length, out transferred, ovPtr);

            if (!ok && Marshal.GetLastWin32Error() == (int)Slv3WinUsbInterop.ERROR_IO_PENDING)
            {
                if (Slv3WinUsbInterop.WaitForSingleObject(ioEvent, timeoutMs) != WaitObject0)
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
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        lock (_readLock)
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
            if (_readEvent != IntPtr.Zero)
            {
                Slv3WinUsbInterop.CloseHandle(_readEvent);
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
        var failures = new List<string>();
        foreach (var path in FindDevicePaths(vendorId, productId))
        {
            try
            {
                return new WindowsBulkUsbPipe(path, writePipeId, readPipeId, productId);
            }
            catch (IOException ex)
            {
                failures.Add(ex.Message);
            }
        }
        if (failures.Count > 0)
        {
            // Overwhelmingly the normal case for a cooler still owned by its vendor driver:
            // it enumerates, and WinUSB is not bound, so it can never be opened here.
            ServiceLog.Info(
                $"[bulk-panel] {vendorId:X4}:{productId:X4} is present but not WinUSB-bound: {string.Join("; ", failures)}");
        }
        return null;
    }

    /// <summary>
    /// WinUSB interfaces of a composite device first: the generic interface class names
    /// only the composite parent there, which WinUsb_Initialize always refuses.
    /// </summary>
    private static List<string> FindDevicePaths(int vendorId, int productId)
    {
        var paths = new List<string>();
        foreach (var guid in WinUsbInterfaceGuids(vendorId, productId))
        {
            AddDevicePaths(guid, vendorId, productId, paths);
        }
        AddDevicePaths(UsbDeviceInterface, vendorId, productId, paths);
        return paths;
    }

    /// <summary>
    /// The interface GUIDs WinUSB publishes for each interface of the device. Only the
    /// device's registry key records them; they come from its MS OS descriptors or its INF.
    /// </summary>
    private static List<Guid> WinUsbInterfaceGuids(int vendorId, int productId)
    {
        var guids = new List<Guid>();
        var prefix = $"VID_{vendorId:X4}&PID_{productId:X4}&MI_";
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null)
            {
                return guids;
            }
            foreach (var name in usb.GetSubKeyNames())
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                using var iface = usb.OpenSubKey(name);
                foreach (var instance in iface?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var parameters = iface!.OpenSubKey(instance + @"\Device Parameters");
                    var values = parameters?.GetValue("DeviceInterfaceGUIDs") switch
                    {
                        string[] many => many,
                        string one => new[] { one },
                        _ => parameters?.GetValue("DeviceInterfaceGUID") is string single
                            ? new[] { single }
                            : Array.Empty<string>(),
                    };
                    foreach (var value in values)
                    {
                        if (Guid.TryParse(value, out var guid) && !guids.Contains(guid))
                        {
                            guids.Add(guid);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return guids;
    }

    private static void AddDevicePaths(Guid interfaceGuid, int vendorId, int productId, List<string> paths)
    {
        var guid = interfaceGuid;
        var devInfo = Slv3WinUsbInterop.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
            Slv3WinUsbInterop.DIGCF_PRESENT | Slv3WinUsbInterop.DIGCF_DEVICEINTERFACE);
        if (devInfo == (IntPtr)(-1))
        {
            return;
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
                if (!string.IsNullOrEmpty(path) && Matches(path, vendorId, productId) && !paths.Contains(path))
                {
                    paths.Add(path);
                }
            }
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
