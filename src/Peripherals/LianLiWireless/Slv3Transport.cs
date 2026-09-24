using System;
using System.Collections.Generic;
using System.IO;
#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>Which physical dongle a discovered device-interface path belongs to.</summary>
public enum Slv3DongleRole
{
    Tx,
    Rx,
}

/// <summary>
/// A dongle open that failed at the OS layer. Carries the Win32 error so the hub
/// can tell "another app holds the WinUSB interface" from any other failure.
/// </summary>
public sealed class Slv3OpenException : IOException
{
    // What CreateFileW returns when the device will not open for us. Measured
    // against L-Connect holding the dongles on the Y70: ERROR_ACCESS_DENIED.
    // Neither code proves a competing app (permissions and a wedged device raise
    // them too), so 'busy' is the likeliest cause, not a proven one.
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    public Slv3OpenException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }

    public bool IsInUseByAnotherApp => ErrorCode is ErrorAccessDenied or ErrorSharingViolation;
}

public interface ISlv3Discovery
{
    IReadOnlyList<Slv3PortInfo> Discover();
}

public sealed class Slv3PortInfo
{
    public required string PortName { get; init; }
    public string Serial { get; init; } = "";
    public Slv3DongleRole Role { get; init; }
}

/// <summary>One open SLV3 dongle (TX or RX): a WinUSB pipe pair, EP 0x01 OUT / EP 0x81 IN.</summary>
public interface ISlv3Transport : IDisposable
{
    bool IsOpen { get; }
    Slv3DongleRole Role { get; }
    string PortName { get; }

    /// <summary>Writes one 64-byte USB frame. False on a transport failure.</summary>
    bool RfSend(ReadOnlySpan<byte> frame);

    /// <summary>
    /// Reassembles a reply from 64-byte interrupt reads up to <paramref name="expectedLen"/>
    /// bytes (the decompiled RfRead/ReadAll loop): stops early on a zero-length or short
    /// read. A first frame opening with 0 is the firmware's no-data signal and is returned
    /// on its own, so a caller can tell "nothing to report" from no reply at all.
    /// Returns only the bytes actually received.
    /// </summary>
    byte[] RfRead(int expectedLen);
}

#if WINDOWS
/// <summary>
/// WinUSB-backed transport for one SLV3 dongle. CreateFileW requires
/// FILE_FLAG_OVERLAPPED for WinUsb_Initialize to accept the handle; passing
/// IntPtr.Zero as the OVERLAPPED parameter to WinUsb_ReadPipe/WritePipe then
/// makes each call synchronous (WinUSB manages the overlapped I/O internally).
/// </summary>
public sealed class Slv3Transport : ISlv3Transport
{
    // Bounds a stalled read so RfRead's reassembly loop treats a timeout as
    // "no more data" instead of blocking indefinitely.
    private const uint TxPipeTimeoutMs = 500;
    // A GetDev reply is on the pipe within a millisecond, in 64-byte packets
    // padded out to the page length; the RX sometimes sends one padding packet
    // fewer, and the read of the missing one then runs to this timeout, so it
    // is L-Connect's short one (Y70 USBPcap 2026-09-04).
    private const uint RxPipeTimeoutMs = 100;

    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _fileHandle;
    private readonly IntPtr _winUsbHandle;
    private const int ErrorSemTimeout = 121;

    private readonly object _ioLock = new();
    private bool _disposed;
    private bool _dead;

    public Slv3Transport(string devicePath, Slv3DongleRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        Role = role;
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
            throw new Slv3OpenException($"CreateFileW failed for {devicePath}: {err}", err);
        }

        if (!Slv3WinUsbInterop.WinUsb_Initialize(_fileHandle, out _winUsbHandle))
        {
            var err = Marshal.GetLastWin32Error();
            _fileHandle.Dispose();
            throw new Slv3OpenException($"WinUsb_Initialize failed for {devicePath}: {err}", err);
        }

        var writeTimeout = TxPipeTimeoutMs;
        var readTimeout = role == Slv3DongleRole.Rx ? RxPipeTimeoutMs : TxPipeTimeoutMs;
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, Slv3Protocol.WritePipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref writeTimeout))
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless] SetPipePolicy (write timeout) failed for {devicePath}: {Marshal.GetLastWin32Error()}");
        }
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, Slv3Protocol.ReadPipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref readTimeout))
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless] SetPipePolicy (read timeout) failed for {devicePath}: {Marshal.GetLastWin32Error()}");
        }
    }

    public bool IsOpen => !_disposed && !_dead && !_fileHandle.IsInvalid && !_fileHandle.IsClosed;
    public Slv3DongleRole Role { get; }
    public string PortName { get; }

    public bool RfSend(ReadOnlySpan<byte> frame)
    {
        if (_disposed)
        {
            return false;
        }
        var buffer = frame.ToArray();
        lock (_ioLock)
        {
            if (Role == Slv3DongleRole.Rx)
            {
                // A GetDev reply arrives as 64-byte packets; whatever a previous
                // poll's read left buffered (the master's own record, a late
                // packet) would otherwise head the next reply and fail its
                // command-echo check. L-Connect flushes before every poll.
                Slv3WinUsbInterop.WinUsb_FlushPipe(_winUsbHandle, Slv3Protocol.ReadPipeId);
            }
            if (Slv3WinUsbInterop.WinUsb_WritePipe(
                _winUsbHandle, Slv3Protocol.WritePipeId, buffer, (uint)buffer.Length, out _, IntPtr.Zero))
            {
                return true;
            }
            // The RX reset (UsbResetAnother) re-enumerates the TX, after which
            // every write on the old TX handle fails; anything but a timeout
            // marks the link down so the worker reconnects. RX failures keep
            // the hub's own reset escalation.
            var err = Marshal.GetLastWin32Error();
            if (Role == Slv3DongleRole.Tx && err != ErrorSemTimeout && !_dead)
            {
                _dead = true;
                Nexus.Service.Platform.ServiceLog.Warn(
                    $"[lianli-wireless] TX write failed ({err}), marking the link down");
            }
            return false;
        }
    }

    public byte[] RfRead(int expectedLen)
    {
        if (_disposed || expectedLen <= 0)
        {
            return Array.Empty<byte>();
        }
        var result = new byte[expectedLen];
        var offset = 0;
        lock (_ioLock)
        {
            while (offset < expectedLen)
            {
                var chunk = new byte[Slv3Protocol.UsbPacketSize];
                var ok = Slv3WinUsbInterop.WinUsb_ReadPipe(
                    _winUsbHandle, Slv3Protocol.ReadPipeId, chunk, (uint)chunk.Length, out var read, IntPtr.Zero);
                if (!ok || read == 0)
                {
                    break;
                }
                if (offset == 0 && chunk[0] == 0)
                {
                    var noData = Math.Min((int)read, expectedLen);
                    Array.Copy(chunk, result, noData);
                    offset = noData;
                    break;
                }
                var copyLen = Math.Min((int)read, expectedLen - offset);
                Array.Copy(chunk, 0, result, offset, copyLen);
                offset += copyLen;
                if (read < Slv3Protocol.UsbPacketSize)
                {
                    break;
                }
            }
        }
        return offset == expectedLen ? result : result.AsSpan(0, offset).ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_ioLock)
        {
            if (_winUsbHandle != IntPtr.Zero)
            {
                Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            }
            _fileHandle.Dispose();
        }
    }
}
#else
/// <summary>
/// Inert: SLV3 is Windows-only for v1.
/// Discovery never returns a port on other platforms, so this is never opened.
/// </summary>
public sealed class Slv3Transport : ISlv3Transport
{
    public Slv3Transport(string devicePath, Slv3DongleRole role)
    {
        PortName = devicePath;
        Role = role;
    }

    public bool IsOpen => false;
    public Slv3DongleRole Role { get; }
    public string PortName { get; }
    public bool RfSend(ReadOnlySpan<byte> frame) => false;
    public byte[] RfRead(int expectedLen) => Array.Empty<byte>();
    public void Dispose()
    {
    }
}
#endif
