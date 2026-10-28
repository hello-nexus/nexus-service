using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Monitors that exist only because a Nexus device integration created them,
/// such as the virtual display a streamed panel's secondary monitor mode brings
/// up. The device's own entry represents them, so they never become a generic
/// display panel record and never appear as standalone displays. Matched on the
/// EDID manufacturer and product code, never on resolution. Identities shared
/// with unrelated hardware are not listed: the topology carries no evidence
/// that separates them.
/// </summary>
public static class DeviceOwnedDisplays
{
    private static readonly (string Manufacturer, string Product)[] Owned =
    {
        ("NXS", "0001"),
    };

    public static bool IsOwned(string? manufacturer, string? product)
    {
        if (string.IsNullOrEmpty(manufacturer) || string.IsNullOrEmpty(product)) return false;
        foreach (var (m, p) in Owned)
        {
            if (string.Equals(manufacturer, m, StringComparison.OrdinalIgnoreCase)
                && string.Equals(product, p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsOwned(DisplayFingerprint? fingerprint)
        => fingerprint is not null && IsOwned(fingerprint.Manufacturer, fingerprint.Product);

    public static bool IsOwned(RawDisplayInfo info)
        => IsOwned(info.Fingerprint) || IsOwned(info.Manufacturer, info.Model) || IsOwnedHardwareId(info.RawHardwareId);

    /// <summary>The Windows PnP interface path (\\?\DISPLAY#TUR0000#...) or a display id names the owned EDID identity.</summary>
    public static bool IsOwnedHardwareId(string? hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId)) return false;
        foreach (var (m, p) in Owned)
        {
            var identity = m + p;
            if (hardwareId.Contains("DISPLAY#" + identity + "#", StringComparison.OrdinalIgnoreCase)
                || hardwareId.StartsWith(identity + "-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes panel records that earlier builds allocated for an owned display.
    /// Returns true when it removed any. Matches the stored fingerprint, falling
    /// back to the display id, which starts with the EDID identity. Tolerates
    /// missing collections and null entries.
    /// </summary>
    public static bool PruneRecords(NexusSettings settings)
    {
        var records = settings?.PanelDevices;
        if (records is null || records.Count == 0) return false;
        var stale = new List<string>();
        foreach (var kv in records)
        {
            var record = kv.Value;
            if (record is null) continue;
            if (IsOwned(record.DisplayFingerprint) || IsOwnedHardwareId(record.DisplayId)) stale.Add(kv.Key);
        }
        foreach (var id in stale) records.Remove(id);
        return stale.Count > 0;
    }
}
