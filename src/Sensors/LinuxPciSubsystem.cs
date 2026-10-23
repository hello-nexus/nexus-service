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

    /// <summary>A floor, not proof of a discrete card: a UMA carve-out can exceed it, so it only narrows the AMD gate.</summary>
    internal const long DiscreteVramMinBytes = 1024L * 1024 * 1024;

    private const int NvidiaVendorId = 0x10DE;
    private const int AmdVendorId = 0x1002;
    private const int IntelVendorId = 0x8086;
    private const string IntegratedIntelFunction = "0000:00:02.0";
    private const string HostBridge = "0000:00:00.0";
    private const string SmbusClass = "0x0c05";

    /// <summary>
    /// AMD device ids that are APUs, from the PCI ID Repository (https://pci-ids.ucw.cz/v2.2/pci.ids) vendor 1002 entries:
    /// Raphael, Granite Ridge, Phoenix1, Phoenix2, Cezanne, Rembrandt, Renoir, Lucienne, Picasso/Raven 2, Barcelo,
    /// Mendocino, Strix, Strix Halo, Krackan, VanGogh, Sephiroth. A second gate behind the board-vendor rule.
    /// </summary>
    private static readonly HashSet<int> AmdApuDeviceIds = new()
    {
        0x164e, 0x13c0, 0x15bf, 0x15c8, 0x1638, 0x1681, 0x1636, 0x164c, 0x15d8, 0x15e7, 0x1506, 0x150e, 0x1586, 0x1114, 0x163f, 0x1435,
    };

    private static readonly string[] IntelDiscreteCodenames = { "DG1", "DG2", "Alchemist", "Battlemage" };

    /// <summary>
    /// True only when every gate agrees the function is a discrete card whose subsystem vendor is a board partner and not the
    /// motherboard maker. The board-vendor rule: the GPU's subsystem vendor must differ from the one on a bus 0 chipset
    /// function (host bridge, else the SMBus controller); with neither readable there is no maker. Intel needs a dGPU codename
    /// and is never 00:02.0; AMD must not be a listed APU and needs dedicated memory at the floor; NVIDIA has no integrated
    /// parts here. Missing beats wrong, so same-vendor card and board, and laptops, report no maker.
    /// </summary>
    internal static bool IsDiscrete(string devicesRoot, string? busId, string name)
    {
        var normalized = NormalizeBusId(busId);
        if (normalized is null)
            return false;
        var dir = Path.Combine(devicesRoot, normalized);
        var subsystemVendor = ReadHex(Path.Combine(dir, "subsystem_vendor"));
        var boardVendor = ReadBoardVendor(devicesRoot);
        if (subsystemVendor is null || boardVendor is null || subsystemVendor == boardVendor)
            return false;
        return ReadHex(Path.Combine(dir, "vendor")) switch
        {
            NvidiaVendorId => true,
            AmdVendorId => ReadHex(Path.Combine(dir, "device")) is { } id && !AmdApuDeviceIds.Contains(id)
                && ReadLong(Path.Combine(dir, "mem_info_vram_total")) is { } vram && vram >= DiscreteVramMinBytes,
            IntelVendorId => normalized != IntegratedIntelFunction
                && IntelDiscreteCodenames.Any(c => name.Contains(c, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    /// <summary>The motherboard maker's subsystem vendor: the host bridge's, else the first SMBus controller's.</summary>
    private static int? ReadBoardVendor(string devicesRoot)
    {
        if (ReadHex(Path.Combine(devicesRoot, HostBridge, "subsystem_vendor")) is { } host)
            return host;
        try
        {
            foreach (var dir in Directory.GetDirectories(devicesRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (ReadText(Path.Combine(dir, "class"))?.StartsWith(SmbusClass, StringComparison.OrdinalIgnoreCase) == true
                    && ReadHex(Path.Combine(dir, "subsystem_vendor")) is { } smbus)
                {
                    return smbus;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

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
