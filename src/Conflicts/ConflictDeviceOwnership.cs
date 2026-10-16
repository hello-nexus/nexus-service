using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Which catalog apps compete for an OpenRGB device, resolved by the vendor
/// string the daemon reports. Universal RGB apps
/// (<see cref="ConflictAppDefinition.ClaimsAllRgb"/>) join every result. The
/// SPA joins these ids against the detected-conflict list to show, per running
/// app, the devices it and Nexus are both after. Curated handlers map through
/// <see cref="Devices.DeviceControlPolicy"/> instead.
/// </summary>
public static class ConflictDeviceOwnership
{
    /// <summary>Whether the app maps to any device: it claims all RGB, names a vendor, or has a curated handler in <see cref="Devices.DeviceControlPolicy"/>.</summary>
    public static bool DeviceOverlapKnown(ConflictAppDefinition def) =>
        def.ClaimsAllRgb
        || def.Vendors.Length > 0
        || Devices.DeviceControlPolicy.AllHandlersByApp().Any(g => string.Equals(g.Key, def.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Catalog ids competing for an OpenRGB card: by the VID:PID in its HID path when it carries one, else by <see cref="AppIdsForVendor"/>.</summary>
    public static List<string> AppIdsForCard(string? vendor, string? location) =>
        ConflictRelevance.ParseHidPath(location) is { } id
            ? ConflictRelevance.AppIdsForUsbId(ConflictAppCatalog.All, id.VendorId, id.ProductId)
            : AppIdsForVendor(vendor);

    /// <summary>Catalog ids competing for an OpenRGB device with this vendor string. Empty for an empty vendor.</summary>
    public static List<string> AppIdsForVendor(string? vendor)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(vendor))
            return result;
        foreach (var def in ConflictAppCatalog.All)
        {
            if (def.ClaimsAllRgb)
            {
                result.Add(def.Id);
                continue;
            }
            foreach (var v in def.Vendors)
            {
                if (vendor.Contains(v, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(def.Id);
                    break;
                }
            }
        }
        return result;
    }
}
