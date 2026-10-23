using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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

    /// <summary>Dedicated memory at or above this marks a discrete AMD adapter; an APU exposes only a small carve-out.</summary>
    internal const long DiscreteVramMinBytes = 1024L * 1024 * 1024;

    private const int NvidiaVendorId = 0x10DE;
    private const int AmdVendorId = 0x1002;
    private const int IntelVendorId = 0x8086;

    /// <summary>
    /// True only with positive proof the function is a discrete card: NVIDIA parts are always discrete, an AMD function needs
    /// amdgpu to report dedicated memory at or above the threshold, an Intel function needs an Arc name. Anything unreadable is
    /// not discrete, so an iGPU is never given its motherboard maker as a card maker.
    /// </summary>
    internal static bool IsDiscrete(string devicesRoot, string? busId, string name)
    {
        var normalized = NormalizeBusId(busId);
        if (normalized is null)
            return false;
        var dir = Path.Combine(devicesRoot, normalized);
        return ReadHex(Path.Combine(dir, "vendor")) switch
        {
            NvidiaVendorId => true,
            AmdVendorId => ReadLong(Path.Combine(dir, "mem_info_vram_total")) is { } vram && vram >= DiscreteVramMinBytes,
            IntelVendorId => name.Contains("Arc", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
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
