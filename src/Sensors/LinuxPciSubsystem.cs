using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Nexus.Service.Sensors;

/// <summary>Reads a PCI function's subsystem ids from sysfs, the way the kernel exposes them without root.</summary>
internal static class LinuxPciSubsystem
{
    internal const string DevicesRoot = "/sys/bus/pci/devices";
    internal const string DmiRoot = "/sys/class/dmi/id";

    /// <summary>
    /// A bus address as sysfs names it: four hex digits of domain, then bus:slot.function. nvidia-smi pads the
    /// domain to eight digits and lspci omits it when it is zero; both map onto the sysfs form.
    /// </summary>
    internal static string? NormalizeBusId(string? busId)
    {
        var id = busId?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id))
            return null;
        var colons = id.Split(':').Length - 1;
        if (colons == 1)
            return "0000:" + id;
        if (colons != 2)
            return null;
        var domain = id[..id.IndexOf(':')];
        return domain.Length > 4 ? domain[^4..] + id[id.IndexOf(':')..] : id;
    }

    /// <summary>The subsystem vendor and device ids of one function; null when sysfs has no such file or it does not parse.</summary>
    internal static (int Vendor, int Device)? Read(string devicesRoot, string? busId)
    {
        var normalized = NormalizeBusId(busId);
        if (normalized is null)
            return null;
        var dir = Path.Combine(devicesRoot, normalized);
        var vendor = ReadHex(Path.Combine(dir, "subsystem_vendor"));
        var device = ReadHex(Path.Combine(dir, "subsystem_device"));
        return vendor is { } v && device is { } d ? (v, d) : null;
    }

    /// <summary>One row of <c>nvidia-smi --query-gpu=name,pci.bus_id --format=csv,noheader</c>; the bus id is the last field because a name has no comma but a missing bus id must not eat it.</summary>
    internal static (string Name, string? BusId) ParseSmiRow(string row)
    {
        var line = row.Trim();
        var comma = line.LastIndexOf(',');
        if (comma < 0)
            return (line, null);
        var tail = line[(comma + 1)..].Trim();
        return NormalizeBusId(tail) is null ? (line, null) : (line[..comma].Trim(), tail);
    }

    /// <summary>Dedicated memory a listed AMD card must report on top of being on the allow list; narrows the gate, proves nothing alone.</summary>
    internal const long DiscreteVramMinBytes = 1024L * 1024 * 1024;

    private const int NvidiaVendorId = 0x10DE;
    private const int AmdVendorId = 0x1002;
    private const int IntelVendorId = 0x8086;
    private const string IntegratedIntelFunction = "0000:00:02.0";
    private const string HostBridge = "0000:00:00.0";
    private const string SmbusClass = "0x0c05";

    /// <summary>
    /// Discrete Radeon device ids, from the PCI ID Repository (https://pci-ids.ucw.cz/v2.2/pci.ids) vendor 1002 entries whose
    /// names are consumer Radeon cards: Navi 10, 14, 21, 22, 23, 24, 31, 32, 33, 44 and 48 RX parts, Polaris Ellesmere, Baffin
    /// and Lexa RX parts, Vega 10 RX Vega 56/64 and Vega 20 Radeon VII. Some ids are shared with laptop and Pro parts; the
    /// chassis gate covers laptops. Mobile-only, USB, "Radeon Graphics" and APU entries are left out. Anything not listed gets no maker.
    /// </summary>
    private static readonly HashSet<int> AmdDiscreteDeviceIds = new()
    {
        0x731f, 0x7340, 0x73a5, 0x73af, 0x73bf, 0x73df, 0x73ef, 0x73ff, 0x7424, 0x743f, 0x744c, 0x747e, 0x7480, 0x7499, 0x749f,
        0x7550, 0x7590, 0x67df, 0x67ef, 0x67ff, 0x6fdf, 0x6987, 0x699f, 0x687f, 0x66af,
    };

    /// <summary>Vendors whose subsystem on the host bridge names the chipset or CPU, not the motherboard maker.</summary>
    private static readonly HashSet<int> SiliconVendorIds = new() { 0, 0x1022, 0x8086, AmdVendorId, NvidiaVendorId };

    private static readonly string[] IntelDiscreteCodenames = { "DG1", "DG2", "Alchemist", "Battlemage" };
    private static readonly string[] MobileMarkers = { "Laptop", "Max-Q", "Mobile" };

    /// <summary>
    /// True only when every gate agrees the function is a discrete card whose subsystem vendor is a board partner and not the
    /// motherboard maker. A portable chassis never qualifies. Board vendor: the first SMBus controller whose subsystem vendor is
    /// not silicon, else the host bridge's unless that names silicon; unreadable means no maker. The GPU's subsystem vendor must differ from it. Intel needs a dGPU codename and is
    /// never 00:02.0; AMD must be on the allow list with dedicated memory at the floor; NVIDIA must not be a laptop part.
    /// Missing beats wrong, so a same-vendor card and board reports no maker.
    /// </summary>
    internal static bool IsDiscrete(string devicesRoot, string? busId, string name, string dmiRoot = DmiRoot)
    {
        var normalized = NormalizeBusId(busId);
        if (normalized is null || PortableChassis.IsPortable(ReadChassisType(dmiRoot)))
            return false;
        var dir = Path.Combine(devicesRoot, normalized);
        var subsystemVendor = ReadHex(Path.Combine(dir, "subsystem_vendor"));
        var boardVendor = ReadBoardVendor(devicesRoot);
        if (subsystemVendor is null || boardVendor is null || subsystemVendor == boardVendor)
            return false;
        return ReadHex(Path.Combine(dir, "vendor")) switch
        {
            NvidiaVendorId => !MobileMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)),
            AmdVendorId => ReadHex(Path.Combine(dir, "device")) is { } id && AmdDiscreteDeviceIds.Contains(id)
                && ReadLong(Path.Combine(dir, "mem_info_vram_total")) is { } vram && vram >= DiscreteVramMinBytes,
            IntelVendorId => normalized != IntegratedIntelFunction
                && IntelDiscreteCodenames.Any(c => name.Contains(c, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    /// <summary>The motherboard maker's subsystem vendor: the first SMBus controller's, else the host bridge's when it is not a silicon vendor.</summary>
    private static int? ReadBoardVendor(string devicesRoot)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(devicesRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (ReadText(Path.Combine(dir, "class"))?.StartsWith(SmbusClass, StringComparison.OrdinalIgnoreCase) == true
                    && ReadHex(Path.Combine(dir, "subsystem_vendor")) is { } smbus
                    && !SiliconVendorIds.Contains(smbus))
                {
                    return smbus;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ReadHex(Path.Combine(devicesRoot, HostBridge, "subsystem_vendor")) is { } host && !SiliconVendorIds.Contains(host) ? host : null;
    }

    /// <summary>The SMBIOS chassis type the kernel exposes without root; null when the file is absent or does not parse.</summary>
    internal static int? ReadChassisType(string dmiRoot) =>
        int.TryParse(ReadText(Path.Combine(dmiRoot, "chassis_type")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type) ? type : null;

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The VGA and 3D controllers of <c>lspci -mm</c> output as (name, slot).</summary>
    internal static List<(string Name, string Slot)> ParseLspciGpus(string lspciMm)
    {
        var found = new List<(string, string)>();
        foreach (var line in lspciMm.Split('\n'))
        {
            if (!line.Contains("\"VGA compatible controller\"") && !line.Contains("\"3D controller\""))
                continue;
            var parts = line.Split('"');
            if (parts.Length >= 6)
                found.Add(($"{parts[3]} {parts[5]}".Trim(), parts[0].Trim()));
        }
        return found;
    }

    private static long? ReadLong(string path)
    {
        try
        {
            return long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ReadHex(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text[2..];
            return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
