using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

public enum TouchMappingOutcome
{
    NoPanel,
    NoDigitizer,
    AlreadyCorrect,
    NeedsRepair,
}

/// <summary>Which tier of the decision matrix produced a repair plan.</summary>
public enum TouchMappingTier
{
    Catalog,
    Generic,
}

public sealed record TouchMappingRepairPlan(
    string DigitizerInterfacePath,
    string PanelDisplayId,
    string PanelMonitorInterfacePath,
    TouchMappingTier Tier);

/// <summary>
/// Pure decision matrix over a touch-mapping snapshot: whether a catalog
/// panel is attached, whether its digitizer is present, and whether the
/// current OS association already matches. A physical panel can expose
/// multiple digitizer HID collections, each with its own Digimon value, so
/// every catalog-matching digitizer is evaluated independently. No I/O -
/// TouchMappingGuard is the only caller that acts on the result.
///
/// A digitizer whose USB descriptor (VID/PID) collides with another catalog
/// entry's is disambiguated by TouchPanelCatalog's companion-hub check (see
/// TouchPanelCatalogEntry.CompanionUsbIds): a companion-scoped entry always
/// wins that digitizer over an unscoped entry with the same VID/PID.
///
/// A second, generic tier runs only when the catalog tier found nothing to
/// repair: on a box with exactly one USB-attached touch digitizer and
/// exactly one touch-expected display (a curated KnownPanelDisplays entry or
/// a Y70-EDID match) that digitizer isn't already mapped to, it infers the
/// pairing without needing the digitizer's VID/PID in the catalog at all. A
/// digitizer already in the catalog is excluded from this tier even when its
/// own panel is absent, so it never gets inferred onto a different
/// touch-expected display (see plans/touch-mapping-auto-repair.md).
///
/// The "exactly one" counts include catalog digitizers and catalog displays,
/// not just generic ones: a box with two catalog panels attached at once has
/// two USB digitizers and two touch-expected displays, so the generic tier
/// stays out even though a pairing might look unambiguous. This keeps the
/// ambiguity gate conservative rather than threading catalog/generic
/// exclusion through the counts too; multi-panel rigs stay
/// catalog-tier-plus-manual-button, same as multi-digitizer rigs.
/// </summary>
public static class TouchMappingDecision
{
    public static (TouchMappingOutcome Outcome, IReadOnlyList<TouchMappingRepairPlan> Plans) Decide(TouchMapSnapshot snapshot)
    {
        var plans = new List<TouchMappingRepairPlan>();
        var matchedPanel = false;
        var matchedDigitizer = false;

        foreach (var display in snapshot.Displays)
        {
            var entry = TouchPanelCatalog.MatchDisplay(display.MonitorInterfacePath);
            if (entry is null) continue;
            matchedPanel = true;

            foreach (var candidate in snapshot.Digitizers)
            {
                if (!TouchPanelCatalog.MatchesDigitizer(entry, candidate)) continue;
                // Most-specific-wins: an unscoped entry (e.g. y70) never
                // claims a digitizer a companion-scoped entry (e.g.
                // xeneon-edge) has already confirmed via its hub sibling,
                // even though their VID/PIDs collide (descriptor-identical
                // digitizer chips on different panels).
                if (!entry.IsCompanionScoped && TouchPanelCatalog.IsClaimedByCompanionScopedEntry(candidate)) continue;
                matchedDigitizer = true;

                if (string.Equals(candidate.AssociatedDisplayId, display.Id, StringComparison.Ordinal)) continue;

                plans.Add(new TouchMappingRepairPlan(
                    candidate.InterfacePath, display.Id, display.MonitorInterfacePath, TouchMappingTier.Catalog));
            }
        }

        if (plans.Count == 0)
        {
            var genericPlan = DecideGenericTier(snapshot);
            if (genericPlan is not null) plans.Add(genericPlan);
        }

        if (plans.Count > 0) return (TouchMappingOutcome.NeedsRepair, plans);
        if (!matchedPanel) return (TouchMappingOutcome.NoPanel, plans);
        if (!matchedDigitizer) return (TouchMappingOutcome.NoDigitizer, plans);
        return (TouchMappingOutcome.AlreadyCorrect, plans);
    }

    private static TouchMappingRepairPlan? DecideGenericTier(TouchMapSnapshot snapshot)
    {
        TouchMapDigitizerInfo? onlyUsbDigitizer = null;
        var usbDigitizerCount = 0;
        foreach (var digitizer in snapshot.Digitizers)
        {
            if (!digitizer.IsUsbAttached) continue;
            usbDigitizerCount++;
            onlyUsbDigitizer = digitizer;
        }
        if (usbDigitizerCount != 1 || onlyUsbDigitizer is null) return null;
        if (TouchPanelCatalog.IsKnownDigitizer(onlyUsbDigitizer.InterfacePath)) return null;

        TouchMapDisplayInfo? touchExpectedDisplay = null;
        var touchExpectedCount = 0;
        foreach (var display in snapshot.Displays)
        {
            if (!IsTouchExpected(display)) continue;
            touchExpectedCount++;
            touchExpectedDisplay = display;
        }
        if (touchExpectedCount != 1 || touchExpectedDisplay is null) return null;
        if (!touchExpectedDisplay.MonitorInterfacePath.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
        if (string.Equals(onlyUsbDigitizer.AssociatedDisplayId, touchExpectedDisplay.Id, StringComparison.Ordinal)) return null;

        return new TouchMappingRepairPlan(
            onlyUsbDigitizer.InterfacePath, touchExpectedDisplay.Id, touchExpectedDisplay.MonitorInterfacePath, TouchMappingTier.Generic);
    }

    /// <summary>
    /// True when a display is a panel model the guard expects to be
    /// touch-capable: either a TouchPanelCatalog EDID match or another
    /// curated KnownPanelDisplays entry with Touch=true. Uses the
    /// Manufacturer/Model the snapshot already carries (the same PnP split
    /// WindowsDisplayIdentity.ResolveIdentity computes) rather than
    /// re-parsing MonitorInterfacePath.
    /// </summary>
    private static bool IsTouchExpected(TouchMapDisplayInfo display) =>
        TouchPanelCatalog.MatchDisplay(display.MonitorInterfacePath) is not null
        || KnownPanelDisplays.Match(display.Manufacturer, display.Model, name: null)?.Touch == true;
}
