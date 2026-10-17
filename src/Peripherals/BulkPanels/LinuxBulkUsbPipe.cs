using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Bulk pipe pair over usbfs: claims the interface that owns the write endpoint on the
/// <c>/dev/bus/usb/BBB/DDD</c> node and moves transfers with USBDEVFS_BULK. No libusb
/// dependency; the service runs as root, so the node opens without a udev rule and a
/// kernel driver bound to the interface is detached.
/// </summary>
public sealed unsafe partial class LinuxBulkUsbPipe : IBulkUsbPipe
{
    // usbfs reads a 0 timeout as "wait forever", so neither may be 0.
    private const uint WriteTimeoutMs = 5000;

    // libusb's per-URB ceiling for synchronous usbfs transfers; a multiple of every bulk packet size.
    private const int MaxChunk = 16384;

    // A multiple of the bulk packet size at every USB speed; usbfs fails a read shorter than the packet the device sends.
    private const int ReadGranule = 512;

    // linux/usbdevice_fs.h, 64-bit struct layouts.
    private const nuint UsbdevfsBulk = 0xC0185502;
    private const nuint UsbdevfsDisconnectClaim = 0x8108551B;
    private const nuint UsbdevfsReleaseInterface = 0x80045510;
    private const nuint UsbdevfsClearHalt = 0x80045515;
    private const uint DisconnectClaimExceptDriver = 0x02;
    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;
    private const int ETIMEDOUT = 110;
    private const int ENODEV = 19;
    private const int ESHUTDOWN = 108;

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

    private readonly string _devicePath;
    private readonly uint _interface;
    private readonly byte _writePipeId;
    private readonly byte _readPipeId;
    private readonly object _writeLock = new();
    // Reads hold their own lock, as on Windows, so a read parked on a quiet IN pipe never
    // holds up frame writes.
    private readonly object _readLock = new();
    // Surplus of a read longer than the caller's buffer; _readLock-confined.
    private byte[] _leftover = Array.Empty<byte>();
    private int _leftoverOffset;
    private int _leftoverCount;
    private int _fd;
    private volatile bool _disposed;
    private volatile bool _dead;

    public int ProductId { get; }

    /// <summary>A replug or firmware reboot removes the node, and a transfer on the old fd fails ENODEV.</summary>
    public bool IsGone => !_disposed && (_dead || !File.Exists(_devicePath));

    private LinuxBulkUsbPipe(string devicePath, uint iface, byte writePipeId, byte readPipeId, int productId, int fd)
    {
        _devicePath = devicePath;
        _interface = iface;
        _writePipeId = writePipeId;
        _readPipeId = readPipeId;
        ProductId = productId;
        _fd = fd;
    }

    /// <summary>Opens and claims the interface, or returns null with the reason.</summary>
    internal static LinuxBulkUsbPipe? TryOpen(string devicePath, uint iface, byte writePipeId, byte readPipeId, int productId, out string? failure)
    {
        var fd = open(devicePath, O_RDWR | O_CLOEXEC);
        if (fd < 0)
        {
            failure = $"open {devicePath} failed: errno {Marshal.GetLastPInvokeError()}";
            return null;
        }
        // Detaches a kernel driver bound to the interface, but not another process's usbfs claim (that stays EBUSY).
        var claim = new DisconnectClaim { Interface = iface, Flags = DisconnectClaimExceptDriver };
        "usbfs"u8.CopyTo(new Span<byte>(claim.Driver, 256));
        if (ioctl(fd, UsbdevfsDisconnectClaim, (nint)(&claim)) < 0)
        {
            failure = $"claiming interface {iface} of {devicePath} failed: errno {Marshal.GetLastPInvokeError()}";
            close(fd);
            return null;
        }
        // Clears a halt a previous owner may have left, as the WinUSB pipe does on open.
        var ep = (uint)writePipeId;
        ioctl(fd, UsbdevfsClearHalt, (nint)(&ep));
        if (readPipeId != 0)
        {
            ep = readPipeId;
            ioctl(fd, UsbdevfsClearHalt, (nint)(&ep));
        }
        failure = null;
        return new LinuxBulkUsbPipe(devicePath, iface, writePipeId, readPipeId, productId, fd);
    }

    public bool Write(ReadOnlySpan<byte> data) => Write(_writePipeId, data);

    public bool Write(byte pipeId, ReadOnlySpan<byte> data)
    {
        if (_disposed || data.Length == 0)
        {
            return false;
        }
        var buffer = data.ToArray();
        lock (_writeLock)
        {
            if (_disposed || _fd < 0)
            {
                return false;
            }
            for (var offset = 0; offset < buffer.Length; offset += MaxChunk)
            {
                var length = Math.Min(MaxChunk, buffer.Length - offset);
                if (Transfer(pipeId, buffer, offset, length, WriteTimeoutMs, out _) != length)
                {
                    return false;
                }
            }
            return true;
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
            if (_disposed || _fd < 0)
            {
                return -1;
            }
            if (_leftoverCount > 0)
            {
                var n = Math.Min(buffer.Length, _leftoverCount);
                _leftover.AsSpan(_leftoverOffset, n).CopyTo(buffer);
                _leftoverOffset += n;
                _leftoverCount -= n;
                return n;
            }
            var request = (buffer.Length + ReadGranule - 1) / ReadGranule * ReadGranule;
            var received = new byte[request];
            var read = Transfer(_readPipeId, received, 0, request, (uint)Math.Max(1, timeoutMs), out var err);
            if (read < 0)
            {
                // A timed-out read is a quiet panel, not a broken one.
                return err == ETIMEDOUT ? 0 : -1;
            }
            if (read <= buffer.Length)
            {
                received.AsSpan(0, read).CopyTo(buffer);
                return read;
            }
            received.AsSpan(0, buffer.Length).CopyTo(buffer);
            _leftover = received.AsSpan(buffer.Length, read - buffer.Length).ToArray();
            _leftoverOffset = 0;
            _leftoverCount = _leftover.Length;
            return buffer.Length;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_readLock)
        {
            lock (_writeLock)
            {
                if (_fd >= 0)
                {
                    var iface = _interface;
                    ioctl(_fd, UsbdevfsReleaseInterface, (nint)(&iface));
                    close(_fd);
                    _fd = -1;
                }
            }
        }
    }

    // Returns the bytes moved, or -1 with the errno in <paramref name="err"/>.
    private int Transfer(byte endpoint, byte[] buffer, int offset, int length, uint timeoutMs, out int err)
    {
        fixed (byte* data = &buffer[offset])
        {
            var xfer = new BulkTransfer
            {
                Endpoint = endpoint,
                Length = (uint)length,
                TimeoutMs = timeoutMs,
                Data = data,
            };
            var n = ioctl(_fd, UsbdevfsBulk, (nint)(&xfer));
            err = n < 0 ? Marshal.GetLastPInvokeError() : 0;
            if (err is ENODEV or ESHUTDOWN)
            {
                _dead = true;
            }
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

public sealed class LinuxBulkUsbPipeFactory : IBulkUsbPipeFactory
{
    private const string SysfsRoot = "/sys/bus/usb/devices";

    public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId)
    {
        var failures = new List<string>();
        foreach (var (path, iface) in FindInterfaces(SysfsRoot, vendorId, productId, writePipeId))
        {
            var pipe = LinuxBulkUsbPipe.TryOpen(path, iface, writePipeId, readPipeId, productId, out var failure);
            if (pipe is not null)
            {
                return pipe;
            }
            failures.Add(failure!);
        }
        if (failures.Count > 0)
        {
            ServiceLog.Info($"[bulk-panel] {vendorId:X4}:{productId:X4} is present but could not be opened: {string.Join("; ", failures)}");
        }
        return null;
    }

    /// <summary>
    /// Each matching device's usbfs node with the interface that exposes <paramref name="writePipeId"/>,
    /// read from the <c>ep_XX</c> entries sysfs lists under the active altsetting. Exposed for unit tests.
    /// </summary>
    internal static IReadOnlyList<(string Path, uint Interface)> FindInterfaces(string root, int vendorId, int productId, byte writePipeId)
    {
        var result = new List<(string, uint)>();
        if (!Directory.Exists(root))
        {
            return result;
        }
        var endpoint = $"ep_{writePipeId:x2}";
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            // "N-M.P" is a device; "usbN" root hubs and "N-M:C.I" interfaces have no panel identity.
            var name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith("usb", StringComparison.Ordinal) || name.Contains(':')
                || ReadHex(System.IO.Path.Combine(dir, "idVendor")) != vendorId
                || ReadHex(System.IO.Path.Combine(dir, "idProduct")) != productId
                || !TryReadInt(System.IO.Path.Combine(dir, "busnum"), out var bus)
                || !TryReadInt(System.IO.Path.Combine(dir, "devnum"), out var dev))
            {
                continue;
            }
            foreach (var iface in Directory.EnumerateDirectories(dir, name + ":*"))
            {
                if (Directory.Exists(System.IO.Path.Combine(iface, endpoint))
                    && TryReadHexInt(System.IO.Path.Combine(iface, "bInterfaceNumber"), out var number))
                {
                    result.Add(($"/dev/bus/usb/{bus:D3}/{dev:D3}", (uint)number));
                }
            }
        }
        return result;
    }

    private static int ReadHex(string path) => TryReadHexInt(path, out var value) ? value : -1;

    private static bool TryReadHexInt(string path, out int value) =>
        int.TryParse(TryRead(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static bool TryReadInt(string path, out int value) =>
        int.TryParse(TryRead(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }
}
