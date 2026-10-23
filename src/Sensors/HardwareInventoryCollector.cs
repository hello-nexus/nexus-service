using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Platform;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Sensors;

/// <summary>RAM modules and physical drives as read from the platform; either list is empty when the platform cannot supply it.</summary>
internal sealed record HardwareInventory(IReadOnlyList<FleetEventRamModule> RamModules, IReadOnlyList<FleetEventDrive> Drives, int RamModuleTotal = -1)
{
    public static readonly HardwareInventory Empty = new(Array.Empty<FleetEventRamModule>(), Array.Empty<FleetEventDrive>());

    /// <summary>Populated module count before the cap on <see cref="RamModules"/>.</summary>
    public int PopulatedModules => RamModuleTotal >= 0 ? RamModuleTotal : RamModules.Count;
}

/// <summary>
/// One read of RAM module and physical drive identity per process, shared by the fleet specs event and the account-device
/// spec strings so both describe the same hardware. Windows uses the in-process SMBIOS table and storage IOCTLs, macOS uses
/// system_profiler, Linux uses sysfs and the DMI table. Serial numbers, volume labels and mount paths are never read.
/// </summary>
internal static class HardwareInventoryCollector
{
    /// <summary>Bounds the specs event; nexus-api enforces the same cap.</summary>
    internal const int MaxRamModules = 16;
    internal const int MaxDrives = 16;

    private static readonly Lazy<HardwareInventory> Shared = new(Collect, LazyThreadSafetyMode.ExecutionAndPublication);

    public static HardwareInventory GetCached() => Shared.Value;

    internal static HardwareInventory Collect()
    {
        var ram = Safe(ReadRam);
        var drives = Safe(ReadDrives);
        return new HardwareInventory(
            ram.OrderBy(RamKey, StringComparer.Ordinal).Take(MaxRamModules).ToList(),
            drives.OrderBy(DriveKey, StringComparer.Ordinal).Take(MaxDrives).ToList(),
            ram.Count);
    }

    // Sorted before the cap so which entries survive it does not depend on enumeration order.
    private static string RamKey(FleetEventRamModule m) =>
        string.Join('|', m.PartNumber, m.Manufacturer, m.CapacityBytes, m.ConfiguredSpeedMts, m.ManufacturerId, m.Generation, m.RatedSpeedMts);

    private static string DriveKey(FleetEventDrive d) => string.Join('|', d.Model, d.Vendor, d.CapacityBytes, d.Bus, d.Media);

    private static List<T> Safe<T>(Func<List<T>> read)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hardware-inventory] {ex.GetType().Name}: {ex.Message}");
            return new List<T>();
        }
    }

    private static List<FleetEventRamModule> ReadRam()
    {
        if (OperatingSystem.IsWindows())
            return WindowsSpecsNative.ReadRamModules();
        if (OperatingSystem.IsMacOS())
            return HardwareInventoryParsers.ParseMacMemory(MacProfilerCache.Get("SPMemoryDataType"));
        if (OperatingSystem.IsLinux())
            return ReadLinuxRam();
        return new List<FleetEventRamModule>();
    }

    private static List<FleetEventDrive> ReadDrives()
    {
        if (OperatingSystem.IsWindows())
            return WindowsSpecsNative.ReadDriveDetails();
        if (OperatingSystem.IsMacOS())
        {
            var drives = HardwareInventoryParsers.ParseMacDrives(MacProfilerCache.Get("SPNVMeDataType"), "nvme");
            drives.AddRange(HardwareInventoryParsers.ParseMacDrives(MacProfilerCache.Get("SPSerialATADataType"), "sata"));
            return drives;
        }
        if (OperatingSystem.IsLinux())
            return ReadLinuxDrives();
        return new List<FleetEventDrive>();
    }

    private const string LinuxDmiTable = "/sys/firmware/dmi/tables/DMI";

    // The kernel exposes the raw SMBIOS structure table, readable by root only. A non-root service gets an empty list, silently: it is the expected case, not a fault, and dmidecode is deliberately not shelled out to.
    private static List<FleetEventRamModule> ReadLinuxRam()
    {
        byte[] table;
        try { table = File.ReadAllBytes(LinuxDmiTable); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return new List<FleetEventRamModule>();
        }
        // SmbiosParser expects the Windows RawSMBIOSData framing: 8 header bytes, table length at offset 4.
        var framed = new byte[8 + table.Length];
        BitConverter.TryWriteBytes(framed.AsSpan(4, 4), (uint)table.Length);
        table.CopyTo(framed, 8);
        return SmbiosParser.ParseModules(framed)
            .Select(m => HardwareInventoryParsers.FromSmbios(m.Info, m.MemoryType))
            .ToList();
    }

    private static List<FleetEventDrive> ReadLinuxDrives()
    {
        var drives = new List<FleetEventDrive>();
        foreach (var block in Directory.GetDirectories("/sys/block").OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(block);
            var resolved = new DirectoryInfo(block).ResolveLinkTarget(true)?.FullName ?? block;
            var device = Path.Combine(block, "device");
            var size = long.TryParse(ReadText(Path.Combine(block, "size")), out var sectors) ? sectors : 0;
            var drive = HardwareInventoryParsers.FromLinux(
                name, resolved,
                ReadText(Path.Combine(device, "vendor")), ReadText(Path.Combine(device, "model")),
                size, ReadText(Path.Combine(block, "queue", "rotational")), ReadText(Path.Combine(block, "removable")));
            if (drive is not null)
                drives.Add(drive);
        }
        return drives;
    }

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return null; }
    }
}
