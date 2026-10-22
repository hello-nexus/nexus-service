using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nexus.Service.Peripherals.LinuxUsb;

public sealed class LinuxUsbDeviceNode
{
    public required string SysName { get; init; }

    public required string DevNode { get; init; }
    public int VendorId { get; init; }
    public int ProductId { get; init; }

    public string Serial { get; init; } = "";
}

public static class LinuxUsbSysfs
{
    public const string SysfsRoot = "/sys/bus/usb/devices";

    public static IReadOnlyList<LinuxUsbDeviceNode> Find(string root, int vendorId, IReadOnlyCollection<int> productIds)
    {
        var result = new List<LinuxUsbDeviceNode>();
        if (!Directory.Exists(root))
        {
            return result;
        }
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("usb", StringComparison.Ordinal) || name.Contains(':'))
            {
                continue;
            }
            if (!TryReadHex(Path.Combine(dir, "idVendor"), out var vid) || vid != vendorId
                || !TryReadHex(Path.Combine(dir, "idProduct"), out var pid) || !Contains(productIds, pid)
                || !TryReadInt(Path.Combine(dir, "busnum"), out var bus)
                || !TryReadInt(Path.Combine(dir, "devnum"), out var dev))
            {
                continue;
            }
            result.Add(new LinuxUsbDeviceNode
            {
                SysName = name,
                DevNode = $"/dev/bus/usb/{bus:D3}/{dev:D3}",
                VendorId = vid,
                ProductId = pid,
                Serial = TryRead(Path.Combine(dir, "serial")),
            });
        }
        return result;
    }

    public static int? FindInterfaceWithEndpoint(string root, string sysName, byte endpoint)
    {
        var deviceDir = Path.Combine(root, sysName);
        if (!Directory.Exists(deviceDir))
        {
            return null;
        }
        var epName = $"ep_{endpoint:x2}";
        try
        {
            foreach (var iface in Directory.EnumerateDirectories(deviceDir, sysName + ":*"))
            {
                if (!Directory.Exists(Path.Combine(iface, epName)))
                {
                    continue;
                }
                if (TryReadHex(Path.Combine(iface, "bInterfaceNumber"), out var number))
                {
                    return number;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static bool Contains(IReadOnlyCollection<int> ids, int value)
    {
        foreach (var id in ids)
        {
            if (id == value)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryReadHex(string path, out int value) =>
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

public sealed class LinuxUsbfsException : IOException
{
    public const int EACCES = 13;
    public const int EBUSY = 16;

    public LinuxUsbfsException(string message, int errno)
        : base(message)
    {
        Errno = errno;
    }

    public int Errno { get; }

    public bool IsAccessDenied => Errno is EACCES or EBUSY;
}

public sealed unsafe partial class LinuxUsbfsInterface : IDisposable
{
    public const int MaxTransferBytes = 64 * 1024;

    private const nuint UsbdevfsBulk = 0xC0185502;
    private const nuint UsbdevfsReleaseInterface = 0x80045510;
    private const nuint UsbdevfsClearHalt = 0x80045515;
    private const nuint UsbdevfsDisconnectClaim = 0x8108551B;
    private const uint DisconnectClaimExceptDriver = 0x02;
    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;

    public const int ETIMEDOUT = 110;

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

    private readonly uint _interface;
    private int _fd;

    public LinuxUsbfsInterface(string devNode, int interfaceNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devNode);
        DevNode = devNode;
        _interface = (uint)interfaceNumber;

        _fd = open(devNode, O_RDWR | O_CLOEXEC);
        if (_fd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new LinuxUsbfsException($"open {devNode} failed: errno {err}", err);
        }
        var claim = new DisconnectClaim { Interface = _interface, Flags = DisconnectClaimExceptDriver };
        "usbfs"u8.CopyTo(new Span<byte>(claim.Driver, 256));
        if (ioctl(_fd, UsbdevfsDisconnectClaim, (nint)(&claim)) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            close(_fd);
            _fd = -1;

            throw new LinuxUsbfsException($"claiming interface {interfaceNumber} of {devNode} failed: errno {err}", err);
        }
    }

    public string DevNode { get; }

    public bool IsOpen => Volatile.Read(ref _fd) >= 0;

    public int ClearHalt(byte endpoint)
    {
        var fd = Volatile.Read(ref _fd);
        if (fd < 0)
        {
            return 19;
        }
        uint ep = endpoint;
        return ioctl(fd, UsbdevfsClearHalt, (nint)(&ep)) < 0 ? Marshal.GetLastPInvokeError() : 0;
    }

    public int Transfer(byte endpoint, Span<byte> buffer, uint timeoutMs, out int errno)
    {
        errno = 0;
        var fd = Volatile.Read(ref _fd);
        if (fd < 0)
        {
            errno = 19;
            return -1;
        }
        if (timeoutMs == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "usbfs treats 0 as no timeout");
        }
        if (buffer.Length > MaxTransferBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(buffer), $"at most {MaxTransferBytes} bytes per transfer");
        }
        fixed (byte* data = buffer)
        {
            var xfer = new BulkTransfer
            {
                Endpoint = endpoint,
                Length = (uint)buffer.Length,
                TimeoutMs = timeoutMs,
                Data = data,
            };
            var n = ioctl(fd, UsbdevfsBulk, (nint)(&xfer));
            if (n < 0)
            {
                errno = Marshal.GetLastPInvokeError();
            }
            return n;
        }
    }

    public bool WriteAll(byte endpoint, ReadOnlySpan<byte> data, uint timeoutMs, out int errno)
    {
        errno = 0;
        if ((endpoint & 0x80) != 0)
        {
            throw new ArgumentException("not an OUT endpoint", nameof(endpoint));
        }

        fixed (byte* p = data)
        {
            var all = new Span<byte>(p, data.Length);
            var offset = 0;
            do
            {
                var len = Math.Min(MaxTransferBytes, data.Length - offset);
                var n = Transfer(endpoint, all.Slice(offset, len), timeoutMs, out errno);
                if (n != len)
                {
                    return false;
                }
                offset += len;
            }
            while (offset < data.Length);
        }
        return true;
    }

    public void Dispose()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd < 0)
        {
            return;
        }
        var iface = _interface;
        ioctl(fd, UsbdevfsReleaseInterface, (nint)(&iface));
        close(fd);
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static partial int ioctl(int fd, nuint request, nint arg);
}
