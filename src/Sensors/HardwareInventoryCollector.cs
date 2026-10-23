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
internal sealed record HardwareInventory(IReadOnlyList<FleetEventRamModule> RamModules, IReadOnlyList<FleetEventDrive> Drives)
{
    public static readonly HardwareInventory Empty = new(Array.Empty<FleetEventRamModule>(), Array.Empty<FleetEventDrive>());
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
        return new HardwareInventory(Cap(ram, MaxRamModules), Cap(drives, MaxDrives));
    }

    // Stable order so an enumeration that comes back shuffled does not look like a hardware change to the specs hash.
    private static List<T> Cap<T>(List<T> items, int max) => items.Take(max).ToList();

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
            return HardwareInventoryParsers.ParseMacMemory(ShellExecutor.Run("/usr/sbin/system_profiler", 10_000, "SPMemoryDataType"));
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
            var drives = HardwareInventoryParsers.ParseMacDrives(ShellExecutor.Run("/usr/sbin/system_profiler", 10_000, "SPNVMeDataType"), "nvme");
            drives.AddRange(HardwareInventoryParsers.ParseMacDrives(ShellExecutor.Run("/usr/sbin/system_profiler", 10_000, "SPSerialATADataType"), "sata"));
            return drives;
        }
        if (OperatingSystem.IsLinux())
            return ReadLinuxDrives();
        return new List<FleetEventDrive>();
    }

    private const string LinuxDmiTable = "/sys/firmware/dmi/tables/DMI";

    // The kernel exposes the raw SMBIOS structure table, readable by root only; an unreadable table leaves the list empty rather than shelling out to dmidecode.
    private static List<FleetEventRamModule> ReadLinuxRam()
    {
        var table = File.ReadAllBytes(LinuxDmiTable);
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
