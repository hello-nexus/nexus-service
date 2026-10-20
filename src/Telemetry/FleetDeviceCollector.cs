using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Picks the attached USB peripherals a specs event reports. Ids identify the
/// model; the name is only a label for hardware nobody has curated yet.
/// Serials are never read here - they identify the machine, not the model.
/// </summary>
internal static class FleetDeviceCollector
{
    /// <summary>Bounds the fleet's fact table; the server enforces the same cap.</summary>
    internal const int MaxDevices = 32;

    /// <summary>
    /// Bus plumbing every machine has: root hubs and host controllers. They say
    /// nothing about what a user owns and would crowd real peripherals out of
    /// the cap.
    /// </summary>
    private static readonly HashSet<int> InfrastructureVendors = new()
    {
        0x1d6b, // Linux Foundation - root hubs
        0x8087, // Intel - integrated hubs/controllers
        0x0438, // AMD
        0x1022, // AMD
    };

    private static readonly string[] InfrastructureNames =
    {
        "root hub",
        "host controller",
    };

    /// <summary>
    /// Ordered by (vid, pid) so an enumeration that comes back in a different
    /// order does not look like a hardware change to the specs hash.
    /// </summary>
    public static List<FleetEventDevice> Collect(IEnumerable<UsbDeviceEntry> usb)
    {
        var byId = new Dictionary<(int Vid, int Pid), FleetEventDevice>();
        foreach (var d in usb)
        {
            var vid = d.VendorId;
            var pid = d.ProductId;
            if (vid is <= 0 or > 0xffff || pid is <= 0 or > 0xffff)
                continue;
            if (InfrastructureVendors.Contains(vid))
                continue;
            if (IsInfrastructureName(d.Name))
                continue;

            var candidate = new FleetEventDevice
            {
                Vid = vid,
                Pid = pid,
                Name = Trim(d.Name),
                Manufacturer = Trim(d.Manufacturer),
            };
            // Composite devices enumerate one row per interface; the first row
            // carrying a real product string is the better label.
            if (!byId.TryGetValue((vid, pid), out var existing)
                || (string.IsNullOrEmpty(existing.Name) && !string.IsNullOrEmpty(candidate.Name)))
            {
                byId[(vid, pid)] = candidate;
            }
        }

        return byId.Values
            .OrderBy(d => d.Vid)
            .ThenBy(d => d.Pid)
            .Take(MaxDevices)
            .ToList();
    }

    // "USB2.0 Hub", "USB3.1 Hub", "Generic USB Hub". Requires the usb prefix so a branded lighting hub ("Uni Hub") stays a reported peripheral.
    private static readonly System.Text.RegularExpressions.Regex HubWord = new(@"\busb[0-9. ]*\s*hub\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsInfrastructureName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var lower = name.Trim().ToLowerInvariant();
        return InfrastructureNames.Any(n => lower.Contains(n, StringComparison.Ordinal))
            || HubWord.IsMatch(lower);
    }

    private static string Trim(string? value)
    {
        var v = (value ?? "").Trim();
        return v.Length <= 64 ? v : v.Substring(0, 64);
    }
}
