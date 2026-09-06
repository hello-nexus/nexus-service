using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Peripherals;

namespace Nexus.Service.Peripherals;

/// <summary>
/// The unified supported-devices catalog: the curated peripherals list merged
/// with the lighting/RGB list. Rows sharing a real VID:PID collapse into one -
/// the native (Source="nexus") metadata wins and capabilities from every source
/// are unioned, so e.g. a mouse that Nexus drives natively AND OpenRGB lights up
/// shows once as a mouse carrying both its input caps and "rgb". Rows without a
/// concrete VID:PID (some OpenRGB controllers report none) are never merged.
/// HYTE and iBUYPOWER rows lead the list, in catalog order.
///
/// Cached for the process lifetime.
/// </summary>
public static class AllSupportedDevices
{
    private static IReadOnlyList<SupportedDeviceDto>? _cache;
    private static readonly object _gate = new();

    /// <summary>Vendors listed before every other row; the brands Nexus is built for.</summary>
    internal static readonly string[] LeadingVendors = { "HYTE", "iBUYPOWER" };

    public static IReadOnlyList<SupportedDeviceDto> All
    {
        get
        {
            if (_cache is not null)
            {
                return _cache;
            }
            lock (_gate)
            {
                _cache ??= Merge();
                return _cache;
            }
        }
    }

    private static IReadOnlyList<SupportedDeviceDto> Merge()
    {
        var result = new List<SupportedDeviceDto>();
        var byVidPid = new Dictionary<string, SupportedDeviceDto>();

        // Peripherals (all native) first, then the lighting list (native rows lead,
        // OpenRGB follows), so the first row seen for any VID:PID is the native one
        // and later duplicates only contribute their capabilities.
        foreach (var d in SupportedDevicesCatalog.All.Concat(LightingDevicesCatalog.All))
        {
            var key = VidPidKey(d);
            if (key is null)
            {
                result.Add(Clone(d));
                continue;
            }
            if (byVidPid.TryGetValue(key, out var existing))
            {
                foreach (var cap in d.Capabilities)
                {
                    if (!existing.Capabilities.Contains(cap))
                    {
                        existing.Capabilities.Add(cap);
                    }
                }
                continue;
            }
            var clone = Clone(d);
            byVidPid[key] = clone;
            result.Add(clone);
        }
        return LeadingVendorsFirst(result);
    }

    /// <summary>Stable partition: <see cref="LeadingVendors"/> rows first, the
    /// rest after, both in their incoming order.</summary>
    internal static List<SupportedDeviceDto> LeadingVendorsFirst(List<SupportedDeviceDto> rows)
    {
        var ordered = new List<SupportedDeviceDto>(rows.Count);
        foreach (var d in rows)
        {
            if (IsLeadingVendor(d.Vendor)) ordered.Add(d);
        }
        foreach (var d in rows)
        {
            if (!IsLeadingVendor(d.Vendor)) ordered.Add(d);
        }
        return ordered;
    }

    internal static bool IsLeadingVendor(string vendor)
    {
        foreach (var v in LeadingVendors)
        {
            if (string.Equals(v, vendor, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? VidPidKey(SupportedDeviceDto d)
    {
        if (string.IsNullOrEmpty(d.VendorId) || string.IsNullOrEmpty(d.ProductId) ||
            d.VendorId == "-" || d.ProductId == "-")
        {
            return null;
        }
        return d.VendorId.ToUpperInvariant() + ":" + d.ProductId.ToUpperInvariant();
    }

    private static SupportedDeviceDto Clone(SupportedDeviceDto d) => new()
    {
        Vendor = d.Vendor,
        Model = d.Model,
        Category = d.Category,
        VendorId = d.VendorId,
        ProductId = d.ProductId,
        Capabilities = new List<string>(d.Capabilities),
        Source = d.Source,
    };
}
