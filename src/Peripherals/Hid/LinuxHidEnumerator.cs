using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Linux hidraw enumeration + open via sysfs + libc open(). The counterpart to
/// <see cref="WindowsHidEnumerator"/>. Walks <c>/sys/class/hidraw</c>, reads each
/// node's <c>device/uevent</c> (VID/PID/serial) and <c>device/report_descriptor</c>
/// (usage page + usage), and opens the matching <c>/dev/hidrawN</c> for vendor
/// protocol I/O. AOT-safe (sysfs reads + a single libc <c>[LibraryImport]</c>).
/// </summary>
public sealed partial class LinuxHidEnumerator : IHidEnumerator
{
    private const string HidrawClass = "/sys/class/hidraw";
    private const int O_RDWR = 2;
    private const int O_CLOEXEC = 0x80000;

    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
    {
        var result = new List<HidDeviceInfo>();
        foreach (var info in EnumerateAll())
        {
            if (info.VendorId != vendorId || info.ProductId != productId) continue;
            result.Add(info);
        }
        return result;
    }

    public IReadOnlyList<HidDeviceInfo> FindAll() => new List<HidDeviceInfo>(EnumerateAll());

    private static IEnumerable<HidDeviceInfo> EnumerateAll()
    {
        if (!Directory.Exists(HidrawClass)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(HidrawClass))
        {
            var name = System.IO.Path.GetFileName(dir); // e.g. "hidraw2"
            var info = ReadInfo(name);
            if (info is null) continue;
            yield return info;
        }
    }

    public IHidDevice? Open(string path, bool forInput = false)
    {
        // forInput is a no-op here: LinuxHidDevice.Read already bounds the read
        // with poll(timeoutMs), so a blocking fd is correct for input too.
        var fd = open(path, O_RDWR | O_CLOEXEC);
        if (fd < 0) return null;

        // Re-read the sysfs metadata for this node so the device carries its
        // usage page / serial (Find already matched on these, but Open only
        // gets the /dev path).
        var name = System.IO.Path.GetFileName(path); // "hidraw2"
        var info = ReadInfo(name);
        return new LinuxHidDevice(fd, path,
            info?.VendorId ?? 0, info?.ProductId ?? 0, info?.Serial,
            info?.UsagePage ?? 0, info?.Usage ?? 0);
    }

    /// <summary>Read VID/PID/serial + top-level usage for one hidraw node, or null if not a valid HID node.</summary>
    private static HidDeviceInfo? ReadInfo(string name)
    {
        try
        {
            var deviceDir = System.IO.Path.Combine(HidrawClass, name, "device");
            var ueventPath = System.IO.Path.Combine(deviceDir, "uevent");
            if (!File.Exists(ueventPath)) return null;

            int vid = 0, pid = 0;
            string? serial = null;
            foreach (var line in File.ReadAllLines(ueventPath))
            {
                if (line.StartsWith("HID_ID=", StringComparison.Ordinal))
                {
                    // HID_ID=<bus>:<8-hex vendor>:<8-hex product>
                    var parts = line.AsSpan(7).ToString().Split(':');
                    if (parts.Length == 3
                        && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                        && uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p))
                    {
                        vid = (int)(v & 0xFFFF);
                        pid = (int)(p & 0xFFFF);
                    }
                }
                else if (line.StartsWith("HID_UNIQ=", StringComparison.Ordinal))
                {
                    var u = line.Substring(9).Trim();
                    if (!string.IsNullOrEmpty(u)) serial = u;
                }
            }
            if (vid == 0) return null;

            var (usagePage, usage) = (0, 0);
            var (inLen, outLen, featLen) = (0, 0, 0);
            var descPath = System.IO.Path.Combine(deviceDir, "report_descriptor");
            if (File.Exists(descPath))
            {
                var desc = ReadSysfsBytes(descPath);
                if (desc.Length > 0)
                {
                    (usagePage, usage) = ParseTopUsage(desc);
                    (inLen, outLen, featLen) = HidReportDescriptorSizes.Compute(desc);
                }
            }

            return new HidDeviceInfo
            {
                VendorId = vid,
                ProductId = pid,
                Path = "/dev/" + name,
                Serial = serial,
                UsagePage = usagePage,
                Usage = usage,
                InputReportByteLength = inLen,
                OutputReportByteLength = outLen,
                FeatureReportByteLength = featLen,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a HID report descriptor for the top-level (application collection)
    /// Usage Page + Usage - the first Usage Page / Usage items before the first
    /// Collection. Enough to pick the keeb's vendor collection (0xFF11 / 0xF0).
    /// </summary>
    internal static (int UsagePage, int Usage) ParseTopUsage(byte[] desc)
    {
        int usagePage = 0, usage = 0;
        var haveUsage = false;
        var i = 0;
        while (i < desc.Length)
        {
            var prefix = desc[i];
            if (prefix == 0xFE) // long item: [0xFE][dataSize][tag][data...]
            {
                if (i + 1 >= desc.Length) break;
                i += 3 + desc[i + 1];
                continue;
            }
            var dataLen = (prefix & 0x03) == 3 ? 4 : (prefix & 0x03);
            var bType = (prefix >> 2) & 0x03; // 0 Main, 1 Global, 2 Local
            var bTag = (prefix >> 4) & 0x0F;
            if (i + 1 + dataLen > desc.Length) break;

            long data = 0;
            for (var k = 0; k < dataLen; k++) data |= (long)desc[i + 1 + k] << (8 * k);

            if (bType == 1 && bTag == 0x0)
            {
                usagePage = (int)data; // Usage Page (Global)
            }
            else if (bType == 2 && bTag == 0x0 && !haveUsage)
            {
                usage = (int)data; // Usage (Local)
                haveUsage = true;
            }
            else if (bType == 0 && bTag == 0xA)
            {
                break; // Collection (Main) - stop at the application collection
            }

            i += 1 + dataLen;
        }
        return (usagePage, usage);
    }

    /// <summary>
    /// Read a sysfs binary attribute. sysfs files report a size of 0, so the
    /// length-based <see cref="File.ReadAllBytes"/> throws EndOfStreamException;
    /// stream to EOF instead. Returns an empty array on any failure.
    /// </summary>
    private static byte[] ReadSysfsBytes(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string pathname, int flags);
}
