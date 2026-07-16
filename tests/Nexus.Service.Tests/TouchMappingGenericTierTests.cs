using System.Collections.Generic;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// The generic touch-mapping tier: single-USB-digitizer inference for a
/// touch-expected display whose attached digitizer doesn't match any
/// catalog VID/PID (a not-yet-catalogued touch panel). Runs only when the
/// catalog tier produced no plan. Uses the Xeneon Edge's real EDID identity
/// as the touch-expected display and a generic (non-Xeneon) digitizer VID/PID
/// as the stand-in uncatalogued digitizer - the Xeneon Edge's actual
/// digitizer (VID_27C0&amp;PID_0859) is itself a catalog entry, so the display
/// is now always a matched catalog panel; see TouchMappingCatalogTests for
/// the Xeneon Edge digitizer's own companion-hub disambiguation.
/// </summary>
public sealed class TouchMappingGenericTierTests
{
    private const string Y70MonitorPath =
        @"\\?\DISPLAY#RTK0004#5&1264575b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Y70DigitizerPath =
        @"\\?\HID#VID_27C0&PID_0859&MI_00&Col01#a&2d89150c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string XeneonEdgeMonitorPath =
        @"\\?\DISPLAY#CRXED00#5&1a2b3c4d&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string UnknownMonitorPath =
        @"\\?\DISPLAY#DEL41B7#5&abc12345&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string InvalidMonitorPath = "DISPLAY#CRXED00#5&1a2b3c4d&0&UID1";
    private const string GenericDigitizerPath =
        @"\\?\HID#VID_046D&PID_C52B&MI_00#7&1234abcd&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string SecondGenericDigitizerPath =
        @"\\?\HID#VID_04F3&PID_2D4A&MI_00#7&5678abcd&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private static TouchMapSnapshot Snapshot(
        List<TouchMapDisplayInfo> displays, List<TouchMapDigitizerInfo> digitizers) =>
        new() { Displays = displays, Digitizers = digitizers };

    private static TouchMapDisplayInfo XeneonEdgeDisplay(string id, string monitorInterfacePath = XeneonEdgeMonitorPath) =>
        new() { Id = id, MonitorInterfacePath = monitorInterfacePath, Manufacturer = "CRX", Model = "ED00" };

    [Fact]
    public void Generic_tier_repairs_the_lone_usb_digitizer_to_the_touch_expected_display()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { XeneonEdgeDisplay("xeneon-1") },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = GenericDigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = true },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
        var plan = Assert.Single(plans);
        Assert.Equal(GenericDigitizerPath, plan.DigitizerInterfacePath);
        Assert.Equal("xeneon-1", plan.PanelDisplayId);
        Assert.Equal(XeneonEdgeMonitorPath, plan.PanelMonitorInterfacePath);
        Assert.Equal(TouchMappingTier.Generic, plan.Tier);
    }

    [Fact]
    public void Generic_tier_is_a_noop_when_two_usb_digitizers_are_present()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { XeneonEdgeDisplay("xeneon-1") },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = GenericDigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = true },
                new() { InterfacePath = SecondGenericDigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = true },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        // The Xeneon Edge display is itself a catalog entry, so the coarse
        // outcome is NoDigitizer (a catalog panel is attached, no digitizer
        // resolved to it), not NoPanel.
        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Generic_tier_is_a_noop_for_a_non_usb_digitizer()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { XeneonEdgeDisplay("xeneon-1") },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = GenericDigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = false },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }

    /// <summary>
    /// A CfgMgr32 devnode-parentage walk failure (unresolvable interface,
    /// broken CM_Get_Parent chain) classifies as IsUsbAttached=false the same
    /// way an actually-non-USB digitizer does - the decision layer can't tell
    /// the two apart, and both must fail closed to no plan.
    /// </summary>
    [Fact]
    public void Generic_tier_is_a_noop_when_the_usb_ancestor_walk_failed()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { XeneonEdgeDisplay("xeneon-1") },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = GenericDigitizerPath, AssociatedDisplayId = "", IsUsbAttached = false },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Generic_tier_skips_a_catalog_known_digitizer_even_when_its_own_panel_is_absent()
    {
        // The Y70's digitizer (catalog VID/PID, no companion sibling) is
        // present but the Y70 display itself is not attached; a different
        // touch-expected display (Xeneon Edge) is. The lone USB digitizer
        // must not be inferred onto it even though it fails the Xeneon
        // Edge catalog entry's companion-hub check (no sibling recorded).
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { XeneonEdgeDisplay("xeneon-1") },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = true },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Catalog_tier_repairs_and_generic_tier_does_not_double_plan()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = true },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
        var plan = Assert.Single(plans);
        Assert.Equal(TouchMappingTier.Catalog, plan.Tier);
    }

    [Fact]
    public void Generic_tier_is_a_noop_when_the_digitizer_already_targets_the_touch_expected_display()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { XeneonEdgeDisplay("xeneon-1") },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = GenericDigitizerPath, AssociatedDisplayId = "xeneon-1", IsUsbAttached = true },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        // The Xeneon Edge display is a TouchPanelCatalog entry, so the
        // catalog tier's bookkeeping sees a matched panel here; the generic
        // digitizer never matches its VID/PID, so the coarse outcome is
        // NoDigitizer even though nothing needs repairing (no plan either way).
        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Generic_tier_is_a_noop_when_no_display_is_touch_expected()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "d1", MonitorInterfacePath = UnknownMonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = GenericDigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = true },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NoPanel, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Generic_tier_is_a_noop_with_two_touch_expected_displays()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo>
            {
                XeneonEdgeDisplay("xeneon-1"),
                new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath },
            },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = GenericDigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = true },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        // The Y70 display makes the catalog tier's matchedPanel true (it is a
        // catalog entry), but no catalog digitizer matches it, so the coarse
        // outcome is NoDigitizer; the generic tier stays out because two
        // touch-expected displays are ambiguous for a single digitizer.
        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Generic_tier_is_a_noop_when_the_touch_expected_display_path_is_not_an_interface_path()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { XeneonEdgeDisplay("xeneon-1", InvalidMonitorPath) },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = GenericDigitizerPath, AssociatedDisplayId = "primary-monitor", IsUsbAttached = true },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        // InvalidMonitorPath still contains the "CRXED00" fragment, so the
        // catalog tier matches the panel; the generic tier's own interface-path
        // guard is what keeps it from producing a plan.
        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }
}
