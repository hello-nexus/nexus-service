using System;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// usbfs-backed transport for one SLV3 dongle on Linux: claims interface 0 of the
/// <c>/dev/bus/usb/BBB/DDD</c> node and moves 64-byte packets over bulk EP 0x01 OUT /
/// EP 0x81 IN with USBDEVFS_BULK. No libusb dependency; the service runs as root,
/// so the node opens without a udev rule.
/// </summary>
public sealed unsafe partial class LinuxSlv3Transport : ISlv3Transport
{
    // Same budgets as the WinUSB transport's pipe policies. usbfs reads a 0 timeout
    // as "wait forever", so none of these may be 0.
    private const uint WriteTimeoutMs = 500;
    private const uint TxReadTimeoutMs = 500;
    private const uint RxReadTimeoutMs = 100;
    private const uint DrainTimeoutMs = 5;
    private const int MaxDrainPackets = 16;

    // linux/usbdevice_fs.h, 64-bit struct layouts.
    private const nuint UsbdevfsBulk = 0xC0185502;
    private const nuint UsbdevfsDisconnectClaim = 0x8108551B;
    private const nuint UsbdevfsReleaseInterface = 0x80045510;
    private const uint DisconnectClaimExceptDriver = 0x02;
    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;
    private const int ETIMEDOUT = 110;
    private const uint DongleInterface = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BulkTransfer
    {
        public uint Endpoint;
        public uint Length;
        public uint TimeoutMs;
        public byte* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisconnectClaim
    {
        public uint Interface;
        public uint Flags;
        public fixed byte Driver[256];
    }

    private readonly object _ioLock = new();
    private int _fd;
    private bool _disposed;
    private bool _dead;

    public LinuxSlv3Transport(string devicePath, Slv3DongleRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        Role = role;
        PortName = devicePath;

        _fd = open(devicePath, O_RDWR | O_CLOEXEC);
        if (_fd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Slv3OpenException($"open failed for {devicePath}: errno {err}", err);
        }
        // Detaches a kernel driver bound to the interface, but not another process's usbfs claim (that stays EBUSY).
        var claim = new DisconnectClaim { Interface = DongleInterface, Flags = DisconnectClaimExceptDriver };
        "usbfs"u8.CopyTo(new Span<byte>(claim.Driver, 256));
        if (ioctl(_fd, UsbdevfsDisconnectClaim, (nint)(&claim)) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            close(_fd);
            _fd = -1;
            throw new Slv3OpenException($"claiming interface 0 of {devicePath} failed: errno {err}", err);
        }
    }

    public bool IsOpen => !_disposed && !_dead && _fd >= 0;
    public Slv3DongleRole Role { get; }
    public string PortName { get; }

    public bool LastWriteTimedOut { get; private set; }

    public bool RfSend(ReadOnlySpan<byte> frame)
    {
        if (_disposed)
        {
            return false;
        }
        var buffer = frame.ToArray();
        lock (_ioLock)
        {
            LastWriteTimedOut = false;
            if (_fd < 0)
            {
                return false;
            }
            if (Role == Slv3DongleRole.Rx)
            {
                // WinUsb_FlushPipe equivalent: a packet a previous poll left on the
                // pipe would otherwise head the next reply and fail its echo check.
                var stale = new byte[Slv3Protocol.UsbPacketSize];
                for (var i = 0; i < MaxDrainPackets && Transfer(Slv3Protocol.ReadPipeId, stale, DrainTimeoutMs, out _) > 0; i++)
                {
                }
            }
            if (Transfer(Slv3Protocol.WritePipeId, buffer, WriteTimeoutMs, out var err) == buffer.Length)
            {
                return true;
            }
            // Same policy as the WinUSB transport: the RX reset re-enumerates the TX
            // (the old node then fails ENODEV), so anything but a timeout closes this
            // TX handle and the hub reopens it.
            LastWriteTimedOut = err == ETIMEDOUT;
            if (Role == Slv3DongleRole.Tx && err != ETIMEDOUT && !_dead)
            {
                _dead = true;
                ServiceLog.Warn($"[lianli-wireless] TX write failed (errno {err}), reopening it");
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
        var timeoutMs = Role == Slv3DongleRole.Rx ? RxReadTimeoutMs : TxReadTimeoutMs;
        var result = new byte[expectedLen];
        var offset = 0;
        lock (_ioLock)
        {
            while (_fd >= 0 && offset < expectedLen)
            {
                var chunk = new byte[Slv3Protocol.UsbPacketSize];
                var read = Transfer(Slv3Protocol.ReadPipeId, chunk, timeoutMs, out _);
                if (read <= 0)
                {
                    break;
                }
                if (offset == 0 && chunk[0] == 0)
                {
                    var noData = Math.Min(read, expectedLen);
                    Array.Copy(chunk, result, noData);
                    offset = noData;
                    break;
                }
                var copyLen = Math.Min(read, expectedLen - offset);
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
            if (_fd >= 0)
            {
                var iface = DongleInterface;
                ioctl(_fd, UsbdevfsReleaseInterface, (nint)(&iface));
                close(_fd);
                _fd = -1;
            }
        }
    }

    // Returns the bytes moved, or -1 with the errno in <paramref name="err"/>.
    private int Transfer(byte endpoint, byte[] buffer, uint timeoutMs, out int err)
    {
        fixed (byte* data = buffer)
        {
            var xfer = new BulkTransfer
            {
                Endpoint = endpoint,
                Length = (uint)buffer.Length,
                TimeoutMs = timeoutMs,
                Data = data,
            };
            var n = ioctl(_fd, UsbdevfsBulk, (nint)(&xfer));
            err = n < 0 ? Marshal.GetLastPInvokeError() : 0;
            return n;
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static partial int ioctl(int fd, nuint request, nint arg);
}
