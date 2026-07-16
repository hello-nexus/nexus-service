using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Hyte.Y70Display;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// A touch panel model the touch-mapping guard is allowed to repair: the
/// display's EDID hardware-id fragments (matched the same way
/// DisplayTopologyService.IsY70Display does) and the USB VID/PIDs of the
/// digitizer that should target it. Scoped per entry so a digitizer never
/// matches a different model's panel.
///
/// CompanionUsbIds disambiguates a digitizer whose USB descriptor (VID/PID,
/// even the bus-reported name) is identical to another catalog entry's: a
/// device must also have a sibling on its own internal USB hub matching one
/// of these ids to count as a match. Null/empty means VID/PID alone is
/// sufficient (the Y70's digitizer has no descriptor-identical sibling
/// panel). See TouchMapDigitizerInfo.CompanionHardwareIds.
/// </summary>
public sealed record TouchPanelCatalogEntry(
    string Family,
    IReadOnlyList<string> DisplayHardwareIdFragments,
    IReadOnlyList<UsbId> DigitizerIds,
    IReadOnlyList<UsbId>? CompanionUsbIds = null)
{
    public bool IsCompanionScoped => CompanionUsbIds is { Count: > 0 };
}

/// <summary>
/// Panels the touch-mapping guard knows how to repair. The guard is a no-op
/// for any digitizer or display that doesn't match an entry here.
/// </summary>
public static class TouchPanelCatalog
{
    // The Xeneon Edge's touch digitizer (VID_27C0&PID_0859) is descriptor-
    // identical to the Y70's - same VID/PID, same BusReportedDeviceDesc and
    // productString ("TouchScreen"), no ContainerId difference. CompanionUsbIds
    // disambiguates: the Edge's digitizer shares its internal USB hub with the
    // Corsair control endpoint (VID_1B1C&PID_1D0D), which the Y70's digitizer's
    // hub never carries.
    private static readonly UsbId[] XeneonEdgeDigitizerIds = { new(0x27C0, 0x0859) };
    private static readonly UsbId[] XeneonEdgeCompanionUsbIds = { new(0x1B1C, 0x1D0D) };

    private static readonly TouchPanelCatalogEntry[] Entries =
    {
        new("y70", Y70DisplayProtocol.DdcPanelHardwareNames, Y70Handler.TouchDigitizers),
        new(KnownPanelDisplays.XeneonEdgeFamily, new[] { "CRXED00" }, XeneonEdgeDigitizerIds, XeneonEdgeCompanionUsbIds),
    };

    public static TouchPanelCatalogEntry? MatchDisplay(string monitorInterfacePath)
    {
        if (string.IsNullOrEmpty(monitorInterfacePath)) return null;
        foreach (var entry in Entries)
        {
            foreach (var fragment in entry.DisplayHardwareIdFragments)
            {
                if (monitorInterfacePath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return entry;
            }
        }
        return null;
    }

    public static bool MatchesDigitizer(TouchPanelCatalogEntry entry, string digitizerInterfacePath)
    {
        if (string.IsNullOrEmpty(digitizerInterfacePath)) return false;
        foreach (var id in entry.DigitizerIds)
        {
            var fragment = $"VID_{id.VendorId:X4}&PID_{id.ProductId:X4}";
            if (digitizerInterfacePath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Full match for a live digitizer: VID/PID as above, plus - for a
    /// companion-scoped entry - a sibling on the digitizer's own USB hub
    /// matching one of entry.CompanionUsbIds. A companion-scoped entry never
    /// matches without a confirmed sibling, even on a VID/PID hit.
    /// </summary>
    public static bool MatchesDigitizer(TouchPanelCatalogEntry entry, TouchMapDigitizerInfo digitizer)
    {
        if (!MatchesDigitizer(entry, digitizer.InterfacePath)) return false;
        if (!entry.IsCompanionScoped) return true;
        foreach (var hardwareId in digitizer.CompanionHardwareIds)
        {
            foreach (var id in entry.CompanionUsbIds!)
            {
                var fragment = $"VID_{id.VendorId:X4}&PID_{id.ProductId:X4}";
                if (hardwareId.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when a companion-scoped entry claims this digitizer (VID/PID and
    /// a confirmed hub sibling). Used to keep an unscoped entry (e.g. y70)
    /// from matching a digitizer that a more specific entry (e.g. xeneon-edge)
    /// already owns, even though their VID/PIDs collide - see TouchMappingDecision.
    /// </summary>
    public static bool IsClaimedByCompanionScopedEntry(TouchMapDigitizerInfo digitizer)
    {
        foreach (var entry in Entries)
        {
            if (entry.IsCompanionScoped && MatchesDigitizer(entry, digitizer)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the digitizer matches any catalog entry's VID/PID, regardless
    /// of whether that entry's panel display is currently attached. The
    /// generic touch-mapping tier uses this to exclude a catalog digitizer
    /// even when its own panel is absent, so it never gets inferred onto a
    /// different touch-expected display.
    /// </summary>
    public static bool IsKnownDigitizer(string digitizerInterfacePath)
    {
        if (string.IsNullOrEmpty(digitizerInterfacePath)) return false;
        foreach (var entry in Entries)
        {
            if (MatchesDigitizer(entry, digitizerInterfacePath)) return true;
        }
        return false;
    }
}
