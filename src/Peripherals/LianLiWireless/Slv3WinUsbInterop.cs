#if WINDOWS
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// P/Invoke surface for WinUSB + SetupAPI, shared by the SLV3 dongle discovery and
/// transport. The dongles bind the generic WinUSB driver (no vendor driver, no
/// LibUsbDotNet); direct DllImport bindings only, so this stays AOT-safe.
/// </summary>
internal static class Slv3WinUsbInterop
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const uint DIGCF_PRESENT = 0x02;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    /// <summary>WinUsb_SetPipePolicy policy id for the per-pipe I/O timeout (DWORD milliseconds).</summary>
    public const uint PIPE_TRANSFER_TIMEOUT = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_WritePipe(
        IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength,
        out uint lengthTransferred, IntPtr overlapped);

    // Pointer overload so the LCD push can pass a pinned buffer + a real
    // OVERLAPPED pointer (the byte[] overload marshals overlapped as IntPtr.Zero).
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_WritePipe(
        IntPtr interfaceHandle, byte pipeId, IntPtr buffer, uint bufferLength,
        out uint lengthTransferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_ReadPipe(
        IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength,
        out uint lengthTransferred, IntPtr overlapped);

    // Pointer overload, matching the write side: an overlapped read needs a pinned buffer
    // and a real OVERLAPPED pointer, which the byte[] overload cannot express.
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_ReadPipe(
        IntPtr interfaceHandle, byte pipeId, IntPtr buffer, uint bufferLength,
        out uint lengthTransferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_SetPipePolicy(
        IntPtr interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_ResetPipe(IntPtr interfaceHandle, byte pipeId);

    // Discards data the pipe has already buffered from the device. L-Connect
    // flushes its reader before every GetDev so a poll never starts on the
    // tail of the previous reply.
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_FlushPipe(IntPtr interfaceHandle, byte pipeId);

    // Cancels any pending transfer on a pipe so a blocked ReadPipe/WritePipe
    // returns instead of pinning the handle open through Dispose.
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_AbortPipe(IntPtr interfaceHandle, byte pipeId);

    // Overlapped I/O: LibUsbDotNet (what L-Connect uses) drives bulk transfers
    // through a real OVERLAPPED; a NULL overlapped works for the RF interrupt
    // pipes but the LCD bulk OUT pipe returns ERROR_BAD_COMMAND without one.
    public const uint ERROR_IO_PENDING = 997;

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_GetOverlappedResult(
        IntPtr interfaceHandle, IntPtr overlapped, out uint lengthTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr devInfo, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detailData,
        uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);
}
#endif
