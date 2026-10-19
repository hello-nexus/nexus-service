using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;

namespace Nexus.Service.Sensors;

/// <summary>
/// Pure byte/string parsers behind <see cref="WindowsSpecsNative"/>. No P/Invoke
/// and no OS dependency, so each one is unit-tested against captured fixtures
/// on any platform.
/// </summary>
internal static class WindowsSpecsParsers
{
    // ACPI SDT header (36 bytes), then the Microsoft Software Licensing
    // Structure: version, reserved, data type, data reserved, data length, data.
    private const int MsdmSignatureOffset = 0;
    private const int MsdmDataLengthOffset = 52;
    private const int MsdmDataOffset = 56;

    /// <summary>The OA3 product key stored in the firmware MSDM table, or "" when the table is absent or malformed.</summary>
    public static string ParseMsdmKey(byte[]? table)
    {
        if (table is null || table.Length <= MsdmDataOffset) return "";
        if (table[MsdmSignatureOffset] != (byte)'M' || table[MsdmSignatureOffset + 1] != (byte)'S'
            || table[MsdmSignatureOffset + 2] != (byte)'D' || table[MsdmSignatureOffset + 3] != (byte)'M')
        {
            return "";
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(MsdmDataLengthOffset, 4));
        var available = table.Length - MsdmDataOffset;
        var length = (int)Math.Min(declared, (uint)available);
        if (length <= 0) return "";
        return Encoding.ASCII.GetString(table, MsdmDataOffset, length).TrimEnd('\0').Trim();
    }

    /// <summary>
    /// Win32_OperatingSystem.Caption without its "Microsoft " prefix. The
    /// registry ProductName still says "Windows 10" on Windows 11, so a build at
    /// or past the first Windows 11 release maps the name forward.
    /// </summary>
    public static string ComposeOsCaption(string productName, int build)
    {
        var name = (productName ?? "").Trim();
        if (name.Length == 0) return "";
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            name = "Windows 11" + name["Windows 10".Length..];
        }
        return name;
    }

    /// <summary>Get-NetAdapter's LinkSpeed string for a bits-per-second rate.</summary>
    public static string FormatLinkSpeed(ulong bitsPerSecond)
    {
        if (bitsPerSecond >= 1_000_000_000UL) return Scaled(bitsPerSecond, 1_000_000_000.0, "Gbps");
        if (bitsPerSecond >= 1_000_000UL) return Scaled(bitsPerSecond, 1_000_000.0, "Mbps");
        if (bitsPerSecond >= 1_000UL) return Scaled(bitsPerSecond, 1_000.0, "Kbps");
        return $"{bitsPerSecond} bps";
    }

    private static string Scaled(ulong value, double unit, string label)
        => $"{(value / unit).ToString("0.#", CultureInfo.InvariantCulture)} {label}";

    /// <summary>The numeric STORAGE_BUS_TYPE as the name Get-PhysicalDisk reports.</summary>
    public static string BusTypeName(uint busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File Backed Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };

    /// <summary>Fields of a STORAGE_DEVICE_DESCRIPTOR relevant to specs.</summary>
    internal readonly record struct StorageDescriptor(string Vendor, string Product, uint BusType, bool Removable);

    /// <summary>Parses the buffer returned for IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceProperty.</summary>
    public static StorageDescriptor? ParseStorageDescriptor(byte[]? buffer)
    {
        // Version, Size, DeviceType, DeviceTypeModifier, RemovableMedia, CommandQueueing,
        // VendorIdOffset, ProductIdOffset, ProductRevisionOffset, SerialNumberOffset, BusType.
        if (buffer is null || buffer.Length < 32) return null;
        var removable = buffer[10] != 0;
        var vendorOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12, 4));
        var productOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16, 4));
        var busType = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(28, 4));
        return new StorageDescriptor(
            AsciiAt(buffer, vendorOffset),
            AsciiAt(buffer, productOffset),
            busType,
            removable);
    }

    private static string AsciiAt(byte[] buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length) return "";
        var end = (int)offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, (int)offset, end - (int)offset).Trim();
    }

    /// <summary>
    /// The friendly name a physical disk reports: the product string, prefixed
    /// with the vendor unless the vendor is only the transport tag.
    /// </summary>
    public static string DiskFriendlyName(StorageDescriptor d)
    {
        var vendor = d.Vendor;
        if (vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase)
            || vendor.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
        {
            vendor = "";
        }
        if (vendor.Length == 0) return d.Product;
        if (d.Product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) return d.Product;
        return d.Product.Length == 0 ? vendor : $"{vendor} {d.Product}";
    }

    /// <summary>
    /// The monitor name from an EDID base block: the 0xFC display-descriptor
    /// text, the same string WmiMonitorID reports as UserFriendlyName. Empty
    /// when the EDID carries none.
    /// </summary>
    public static string EdidMonitorName(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return "";
        // Four 18-byte descriptors start at offset 54.
        for (var i = 0; i < 4; i++)
        {
            var o = 54 + (i * 18);
            if (edid[o] != 0 || edid[o + 1] != 0 || edid[o + 2] != 0 || edid[o + 3] != 0xFC) continue;
            var text = Encoding.ASCII.GetString(edid, o + 5, 13);
            var end = text.IndexOf('\n');
            if (end >= 0) text = text[..end];
            return text.Trim();
        }
        return "";
    }

    /// <summary>The three-letter PnP manufacturer id from an EDID base block, "" when too short.</summary>
    public static string EdidManufacturer(byte[]? edid)
    {
        if (edid is null || edid.Length < 10) return "";
        var word = (edid[8] << 8) | edid[9];
        var chars = new[]
        {
            (char)('A' + ((word >> 10) & 0x1F) - 1),
            (char)('A' + ((word >> 5) & 0x1F) - 1),
            (char)('A' + (word & 0x1F) - 1),
        };
        return new string(chars);
    }

    /// <summary>The first <c>USB\*\serial</c> id; the case-insensitive suffix match keeps a serial from matching another devnode.</summary>
    public static string? PickUsbInstanceId(IEnumerable<string> instanceIds, string serial)
    {
        var suffix = "\\" + serial;
        foreach (var id in instanceIds)
        {
            if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                && id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && id.Length >= "USB\\".Length + suffix.Length)
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>Splits a double-NUL terminated multi-string buffer.</summary>
    public static List<string> SplitMultiSz(char[] buffer)
    {
        var items = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i == start) break;
            items.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        return items;
    }

    /// <summary>
    /// The id the display driver stack uses for a monitor inside a
    /// GraphicsDrivers\Configuration key name: manufacturer, product code, serial
    /// text, week, year and the EDID checksum byte.
    /// </summary>
    public static string EdidConfigId(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return "";
        var product = edid[10] | (edid[11] << 8);
        var serial = "";
        for (var i = 0; i < 4; i++)
        {
            var o = 54 + (i * 18);
            if (edid[o] != 0 || edid[o + 1] != 0 || edid[o + 2] != 0 || edid[o + 3] != 0xFF) continue;
            var text = Encoding.ASCII.GetString(edid, o + 5, 13);
            var end = text.IndexOf('\n');
            serial = (end >= 0 ? text[..end] : text).Trim();
            break;
        }
        return $"{EdidManufacturer(edid)}{product:X4}{serial}_{edid[16]:X2}_{1990 + edid[17]:X4}_{edid[127]:X2}";
    }

    /// <summary>
    /// The saved display-configuration key whose monitor set is exactly the
    /// present monitors (key names read <c>id+id^hash</c>), or null.
    /// </summary>
    public static string? SelectDisplayConfig(IEnumerable<string> keyNames, IReadOnlyCollection<string> presentIds)
    {
        if (presentIds.Count == 0) return null;
        // A multiset: identical monitors without a serial descriptor share one id.
        var wanted = presentIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in keyNames)
        {
            var caret = name.IndexOf('^');
            var ids = (caret >= 0 ? name[..caret] : name).Split('+')
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase)) return name;
        }
        return null;
    }
}
