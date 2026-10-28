using System;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Monitors that exist only because a Nexus device integration created or owns
/// them: the Nexus virtual display a streamed panel's "secondary monitor" mode
/// brings up, and the USB display a Lian Li LCD exposes in its USB-display mode.
/// The device's own entry represents them, so they never become a generic
/// display panel record and never appear as standalone displays. Matched on the
/// EDID manufacturer and product code (the strongest identity the topology
/// carries), never on resolution.
/// </summary>
public static class DeviceOwnedDisplays
{
    // EISA id + product code. NXS/0001 is the Nexus virtual display driver;
    // TUR/0000 is the Lian Li LCD in USB-display mode (USB 1A86:AD20).
    private static readonly (string Manufacturer, string Product)[] Owned =
    {
        ("NXS", "0001"),
        ("TUR", "0000"),
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
    /// back to the display id, which starts with the EDID identity.
    /// </summary>
    public static bool PruneRecords(NexusSettings settings)
    {
        var stale = settings.PanelDevices
            .Where(kv => IsOwned(kv.Value.DisplayFingerprint) || IsOwnedHardwareId(kv.Value.DisplayId))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in stale) settings.PanelDevices.Remove(id);
        return stale.Count > 0;
    }
}
