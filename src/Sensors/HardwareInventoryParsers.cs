using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Sensors;

/// <summary>
/// Pure normalizers behind <see cref="HardwareInventoryCollector"/>: raw platform readouts in,
/// the wire shapes out. No I/O, so each runs against captured fixtures on any platform. None
/// of them ever reads or returns a serial number, volume label or mount path.
/// </summary>
internal static partial class HardwareInventoryParsers
{
    private static readonly string[] Placeholders =
    {
        "unknown", "undefined", "not specified", "none", "n/a", "[empty]", "to be filled by o.e.m.", "0", "0000",
    };

    private static readonly string[] VirtualMarkers =
    {
        "virtual", "vbox", "qemu", "vmware", "parallels", "xen ", "msft", "hyper-v", "ramdisk",
    };

    /// <summary>The trimmed text, or null for blanks and the OEM placeholder strings boards write into unpopulated SPD fields.</summary>
    public static string? CleanText(string? value)
    {
        var t = (value ?? "").Trim();
        if (t.Length == 0)
            return null;
        if (Placeholders.Contains(t, StringComparer.OrdinalIgnoreCase))
            return null;
        if (PlaceholderIndexed().IsMatch(t))
            return null;
        return t;
    }

    [GeneratedRegex(@"^(manufacturer|partnum(ber)?|partno|array\d+_\w+)\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderIndexed();

    /// <summary>SMBIOS memory-type byte to the DDR generation name; null for types that say nothing useful.</summary>
    public static string? MemoryGeneration(int smbiosMemoryType) => smbiosMemoryType switch
    {
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => null,
    };

    /// <summary>Generation from a text type such as "DDR4" or "LPDDR5" (macOS system_profiler).</summary>
    public static string? MemoryGeneration(string? text)
    {
        var t = (text ?? "").Trim().ToUpperInvariant();
        return t is "DDR3" or "DDR4" or "DDR5" or "LPDDR3" or "LPDDR4" or "LPDDR4X" or "LPDDR5" or "LPDDR5X" ? t : null;
    }

    /// <summary>Vendor name and raw id for an SMBIOS manufacturer string, falling back to the Module Manufacturer ID field.</summary>
    public static (string? Name, string? Id) ResolveManufacturer(string? smbiosString, string? moduleManufacturerId)
    {
        var text = CleanText(smbiosString);
        if (text is not null && !JedecManufacturers.LooksLikeId(text))
            return (text, moduleManufacturerId);

        var id = text is not null ? NormalizeId(text) : moduleManufacturerId;
        var name = id is null ? null : JedecManufacturers.Decode(id);
        if (name is null && moduleManufacturerId is not null && moduleManufacturerId != id)
        {
            name = JedecManufacturers.Decode(moduleManufacturerId);
            id ??= moduleManufacturerId;
        }
        return (name, id);
    }

    private static string NormalizeId(string text)
    {
        var t = text.Trim();
        return (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t).ToUpperInvariant();
    }

    public static FleetEventRamModule FromSmbios(MemoryModuleInfo info, int? smbiosMemoryType)
    {
        var (name, id) = ResolveManufacturer(info.Manufacturer, info.ModuleManufacturerId);
        return new FleetEventRamModule
        {
            Manufacturer = name,
            ManufacturerId = id,
            PartNumber = CleanText(info.PartNumber),
            CapacityBytes = info.SizeBytes is > 0 ? info.SizeBytes : null,
            ConfiguredSpeedMts = info.ConfiguredSpeedMts,
            RatedSpeedMts = info.MaxSpeedMts,
            Generation = smbiosMemoryType is int t ? MemoryGeneration(t) : null,
        };
    }

    /// <summary>True for hypervisor and RAID-volume disks, which are not hardware a user owns.</summary>
    public static bool IsVirtualDrive(string? model, string? vendor)
    {
        var haystack = $"{vendor} {model}".ToLowerInvariant();
        if (VirtualMarkers.Any(haystack.Contains))
            return true;
        return haystack.Contains("raid") && haystack.Contains("volume");
    }

    private static string? TransportTagToNull(string? vendor)
    {
        var v = CleanText(vendor);
        if (v is null || v.Equals("ATA", StringComparison.OrdinalIgnoreCase) || v.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
            return null;
        return v;
    }

    /// <summary>Bus name for a Windows STORAGE_BUS_TYPE; null for buses that are not a physical drive transport.</summary>
    public static string? WindowsBus(uint busType) => busType switch
    {
        1 => "scsi",
        3 => "ata",
        7 => "usb",
        8 => "raid",
        10 => "sas",
        11 => "sata",
        17 => "nvme",
        _ => null,
    };

    /// <summary>A Windows physical drive, or null when it is virtual, pooled or otherwise not a physical disk.</summary>
    public static FleetEventDrive? FromWindows(string vendor, string product, uint busType, string mediaType, long size)
    {
        // 14 virtual, 15 file-backed virtual, 16 Storage Spaces pool.
        if (busType is 14 or 15 or 16)
            return null;
        var model = CleanText(product);
        var vend = TransportTagToNull(vendor);
        if (model is null && vend is null)
            return null;
        if (IsVirtualDrive(model, vendor))
            return null;

        var bus = WindowsBus(busType);
        var media = mediaType.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "ssd"
            : mediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "hdd"
            : bus == "nvme" ? "ssd" : null;
        return new FleetEventDrive
        {
            Model = model ?? vend,
            Vendor = model is null ? null : vend,
            CapacityBytes = size > 0 ? size : null,
            Bus = bus,
            Media = media,
        };
    }

    /// <summary>
    /// A Linux block device from its sysfs facts. <paramref name="resolvedDevicePath"/> is the symlink target of
    /// /sys/block/NAME and carries the transport (nvme, usb, ata). Null for loop, RAM, mapper, optical, removable and virtual devices.
    /// </summary>
    public static FleetEventDrive? FromLinux(string blockName, string resolvedDevicePath, string? vendor, string? model,
        long sizeSectors, string? rotational, string? removable)
    {
        if (blockName.StartsWith("loop", StringComparison.Ordinal) || blockName.StartsWith("ram", StringComparison.Ordinal)
            || blockName.StartsWith("dm-", StringComparison.Ordinal) || blockName.StartsWith("md", StringComparison.Ordinal)
            || blockName.StartsWith("zram", StringComparison.Ordinal) || blockName.StartsWith("sr", StringComparison.Ordinal)
            || blockName.StartsWith("fd", StringComparison.Ordinal) || blockName.StartsWith("nbd", StringComparison.Ordinal)
            || blockName.StartsWith("mmcblk", StringComparison.Ordinal))
        {
            return null;
        }
        if ((removable ?? "").Trim() == "1")
            return null;
        if (resolvedDevicePath.Contains("/virtual/", StringComparison.Ordinal) || resolvedDevicePath.Contains("/virtio", StringComparison.Ordinal))
            return null;

        var cleanModel = CleanText(model);
        var cleanVendor = TransportTagToNull(vendor);
        if (cleanModel is null && cleanVendor is null)
            return null;
        if (IsVirtualDrive(cleanModel, cleanVendor))
            return null;

        string? bus = null;
        if (blockName.StartsWith("nvme", StringComparison.Ordinal) || resolvedDevicePath.Contains("/nvme", StringComparison.Ordinal))
            bus = "nvme";
        else if (resolvedDevicePath.Contains("/usb", StringComparison.Ordinal))
            bus = "usb";
        else if (resolvedDevicePath.Contains("/ata", StringComparison.Ordinal))
            bus = "sata";

        var rot = (rotational ?? "").Trim();
        var media = rot == "1" ? "hdd" : rot == "0" ? "ssd" : null;
        return new FleetEventDrive
        {
            Model = cleanModel ?? cleanVendor,
            Vendor = cleanModel is null ? null : cleanVendor,
            CapacityBytes = sizeSectors > 0 ? sizeSectors * 512 : null,
            Bus = bus,
            Media = media,
        };
    }

    /// <summary>
    /// Physical drives from `system_profiler SPNVMeDataType` or `SPSerialATADataType` text; <paramref name="bus"/> names the transport of the section.
    /// A drive is a block whose direct children include a Model line. Field order inside a block varies, and a Volumes sub-block (labels, per-volume capacity)
    /// sits deeper, so only the direct children are read.
    /// </summary>
    public static List<FleetEventDrive> ParseMacDrives(string profile, string bus)
    {
        var lines = new List<(int Indent, string Key, string Value)>();
        foreach (var raw in profile.Split('\n'))
        {
            var text = raw.TrimEnd();
            var line = text.TrimStart();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            lines.Add((text.Length - line.Length, line[..colon], line[(colon + 1)..].Trim()));
        }

        var drives = new List<FleetEventDrive>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Value.Length != 0)
                continue;
            var end = i + 1;
            while (end < lines.Count && lines[end].Indent > lines[i].Indent)
                end++;
            if (end == i + 1)
                continue;
            var childIndent = lines[i + 1].Indent;
            var direct = lines.Skip(i + 1).Take(end - i - 1).Where(l => l.Indent == childIndent).ToList();
            var model = direct.FirstOrDefault(l => l.Key == "Model").Value;
            if (string.IsNullOrEmpty(model))
                continue;

            string Field(string key) => direct.FirstOrDefault(l => l.Key == key).Value ?? "";
            if (Field("Removable Media").Equals("Yes", StringComparison.OrdinalIgnoreCase)
                || Field("Detachable Drive").Equals("Yes", StringComparison.OrdinalIgnoreCase)
                || IsVirtualDrive(model, null))
            {
                continue;
            }

            var medium = Field("Medium Type");
            drives.Add(new FleetEventDrive
            {
                Model = CleanText(model),
                CapacityBytes = ParseBytesInParens(Field("Capacity")),
                Bus = bus,
                Media = medium.StartsWith("Solid", StringComparison.OrdinalIgnoreCase) ? "ssd"
                    : medium.StartsWith("Rotational", StringComparison.OrdinalIgnoreCase) ? "hdd"
                    : bus == "nvme" ? "ssd" : null,
            });
        }
        return drives.Where(d => d.Model is not null).ToList();
    }

    private static long? ParseBytesInParens(string value)
    {
        var open = value.IndexOf('(');
        var end = value.IndexOf(" bytes", StringComparison.Ordinal);
        if (open < 0 || end <= open)
            return null;
        var digits = value[(open + 1)..end].Replace(",", "");
        return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>
    /// DIMMs from Intel-Mac `system_profiler SPMemoryDataType`, one block per slot that reports a Size. Apple Silicon prints a
    /// single unified-memory block with no Size per slot and yields nothing. Serial numbers are not read.
    /// </summary>
    public static List<FleetEventRamModule> ParseMacMemory(string profile)
    {
        var modules = new List<FleetEventRamModule>();
        FleetEventRamModule? current = null;
        string? rawManufacturer = null;

        void Flush()
        {
            if (current is not null && current.CapacityBytes is not null)
            {
                var (name, id) = ResolveManufacturer(rawManufacturer, null);
                current.Manufacturer = name;
                current.ManufacturerId = id;
                modules.Add(current);
            }
            current = null;
            rawManufacturer = null;
        }

        foreach (var raw in profile.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                continue;
            // A slot header ("DIMM0/BANK 0:") has no value after its colon.
            if (trimmed.EndsWith(':') && raw.StartsWith("        ", StringComparison.Ordinal) && !raw.StartsWith("          ", StringComparison.Ordinal))
            {
                Flush();
                current = new FleetEventRamModule();
                continue;
            }
            if (current is null)
                continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = trimmed[..colon];
            var value = trimmed[(colon + 1)..].Trim();
            switch (key)
            {
                case "Size":
                    current.CapacityBytes = ParseSizeBytes(value);
                    break;
                case "Type":
                    current.Generation = MemoryGeneration(value);
                    break;
                case "Speed":
                    current.ConfiguredSpeedMts = ParseMts(value);
                    break;
                case "Manufacturer":
                    rawManufacturer = value;
                    break;
                case "Part Number":
                    current.PartNumber = CleanText(value);
                    break;
            }
        }
        Flush();
        return modules;
    }

    private static long? ParseSizeBytes(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return null;
        return parts[1].ToUpperInvariant() switch
        {
            "GB" => n * 1024L * 1024 * 1024,
            "MB" => n * 1024L * 1024,
            "TB" => n * 1024L * 1024 * 1024 * 1024,
            _ => null,
        };
    }

    private static int? ParseMts(string value)
    {
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }
}
