using System.Collections.Generic;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Pure logic for the touch-mapping guard: the Digimon registry value
/// format, the panel/digitizer catalog match, and the decision matrix.
/// Golden values are the exact strings captured on the Y70 bench,
/// plans/touch-mapping-auto-repair.md section 4a.
/// </summary>
public sealed class TouchMappingCatalogTests
{
    private const string Y70MonitorPath =
        @"\\?\DISPLAY#RTK0004#5&1264575b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Y70DigitizerPath =
        @"\\?\HID#VID_27C0&PID_0859&MI_00&Col01#a&2d89150c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string OtherMonitorPath =
        @"\\?\DISPLAY#DEL41B7#5&abc12345&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string ThirdPartyDigitizerPath =
        @"\\?\HID#VID_046D&PID_C52B&MI_00#7&1234abcd&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    // -- DigimonRegistryFormat ------------------------------------------

    [Fact]
    public void ValueName_matches_the_bench_captured_format()
    {
        Assert.Equal("20-" + Y70DigitizerPath, DigimonRegistryFormat.ValueName(Y70DigitizerPath));
    }

    // -- TouchPanelCatalog ------------------------------------------------

    [Fact]
    public void MatchDisplay_finds_y70_by_edid_fragment()
    {
        var entry = TouchPanelCatalog.MatchDisplay(Y70MonitorPath);

        Assert.NotNull(entry);
        Assert.Equal("y70", entry!.Family);
    }

    [Fact]
    public void MatchDisplay_returns_null_for_an_unknown_display()
    {
        Assert.Null(TouchPanelCatalog.MatchDisplay(OtherMonitorPath));
        Assert.Null(TouchPanelCatalog.MatchDisplay(""));
    }

    [Fact]
    public void MatchesDigitizer_matches_catalog_vid_pid_case_insensitively()
    {
        var entry = TouchPanelCatalog.MatchDisplay(Y70MonitorPath)!;

        Assert.True(TouchPanelCatalog.MatchesDigitizer(entry, Y70DigitizerPath));
        Assert.True(TouchPanelCatalog.MatchesDigitizer(entry,
            @"\\?\HID#VID_222a&PID_0001&MI_00&Col02#a&1234abcd&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.False(TouchPanelCatalog.MatchesDigitizer(entry, ThirdPartyDigitizerPath));
        Assert.False(TouchPanelCatalog.MatchesDigitizer(entry, ""));
    }

    // -- TouchMappingDecision ----------------------------------------------

    private static TouchMapSnapshot Snapshot(
        List<TouchMapDisplayInfo> displays, List<TouchMapDigitizerInfo> digitizers) =>
        new() { Displays = displays, Digitizers = digitizers };

    [Fact]
    public void Decide_returns_NoPanel_when_no_catalog_display_is_present()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "d1", MonitorInterfacePath = OtherMonitorPath } },
            new List<TouchMapDigitizerInfo>());

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NoPanel, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Decide_returns_NoDigitizer_when_the_panel_display_has_no_matching_digitizer()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>());

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Decide_ignores_a_digitizer_outside_the_catalog()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = ThirdPartyDigitizerPath, AssociatedDisplayId = "y70-1" },
            });

        var (outcome, _) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
    }

    [Fact]
    public void Decide_returns_AlreadyCorrect_when_the_association_matches()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "y70-1" },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.AlreadyCorrect, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Decide_returns_NeedsRepair_when_associated_to_the_wrong_display()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "primary-monitor" },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
        var plan = Assert.Single(plans);
        Assert.Equal(Y70DigitizerPath, plan.DigitizerInterfacePath);
        Assert.Equal("y70-1", plan.PanelDisplayId);
        Assert.Equal(Y70MonitorPath, plan.PanelMonitorInterfacePath);
    }

    [Fact]
    public void Decide_returns_NeedsRepair_when_the_digitizer_has_no_association_at_all()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "" },
            });

        var (outcome, _) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
    }

    [Fact]
    public void Decide_evaluates_every_matching_digitizer_independently()
    {
        const string secondCollectionPath =
            @"\\?\HID#VID_27C0&PID_0859&MI_00&Col02#a&2d89150c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "y70-1" },
                new() { InterfacePath = secondCollectionPath, AssociatedDisplayId = "primary-monitor" },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
        var plan = Assert.Single(plans);
        Assert.Equal(secondCollectionPath, plan.DigitizerInterfacePath);
    }

    [Fact]
    public void Decide_repairs_every_mismatched_digitizer_when_none_are_already_correct()
    {
        const string secondCollectionPath =
            @"\\?\HID#VID_27C0&PID_0859&MI_00&Col02#a&2d89150c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "primary-monitor" },
                new() { InterfacePath = secondCollectionPath, AssociatedDisplayId = "primary-monitor" },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, p => p.DigitizerInterfacePath == Y70DigitizerPath);
        Assert.Contains(plans, p => p.DigitizerInterfacePath == secondCollectionPath);
    }

    // -- Xeneon Edge companion-hub disambiguation ---------------------------
    //
    // The Edge's touch digitizer is descriptor-identical to the Y70's: same
    // VID_27C0&PID_0859, same BusReportedDeviceDesc/productString, no
    // ContainerId difference. Ground truth captured live on the Y70 box:
    // both digitizers reported the same (Y70's) HMONITOR, so the naive fix
    // (a bare VID/PID catalog row for the Edge) would drag the Y70's own
    // digitizer onto the Edge and vice versa. CompanionUsbIds/CompanionHardwareIds
    // disambiguate via what else shares the digitizer's internal USB hub.

    private const string XeneonEdgeMonitorPath =
        @"\\?\DISPLAY#CRXED00#5&1a2b3c4d&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string XeneonEdgeDigitizerPath =
        @"\\?\HID#VID_27C0&PID_0859&MI_00&Col01#8&7d3660c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string XeneonEdgeCompanionHardwareId = @"USB\VID_1B1C&PID_1D0D\002125375656";

    [Fact]
    public void MatchDisplay_finds_xeneon_edge_by_edid_fragment()
    {
        var entry = TouchPanelCatalog.MatchDisplay(XeneonEdgeMonitorPath);

        Assert.NotNull(entry);
        Assert.Equal(KnownPanelDisplays.XeneonEdgeFamily, entry!.Family);
        Assert.True(entry.IsCompanionScoped);
    }

    [Fact]
    public void MatchesDigitizer_requires_the_companion_sibling_for_a_companion_scoped_entry()
    {
        var entry = TouchPanelCatalog.MatchDisplay(XeneonEdgeMonitorPath)!;
        var withSibling = new TouchMapDigitizerInfo
        {
            InterfacePath = XeneonEdgeDigitizerPath,
            CompanionHardwareIds = { XeneonEdgeCompanionHardwareId },
        };
        var withoutSibling = new TouchMapDigitizerInfo { InterfacePath = XeneonEdgeDigitizerPath };

        Assert.True(TouchPanelCatalog.MatchesDigitizer(entry, withSibling));
        Assert.False(TouchPanelCatalog.MatchesDigitizer(entry, withoutSibling));
    }

    [Fact]
    public void Decide_binds_the_edge_digitizer_to_the_edge_display_given_its_companion_sibling()
    {
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "edge-1", MonitorInterfacePath = XeneonEdgeMonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new()
                {
                    InterfacePath = XeneonEdgeDigitizerPath,
                    AssociatedDisplayId = "primary-monitor",
                    CompanionHardwareIds = { XeneonEdgeCompanionHardwareId },
                },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
        var plan = Assert.Single(plans);
        Assert.Equal(XeneonEdgeDigitizerPath, plan.DigitizerInterfacePath);
        Assert.Equal("edge-1", plan.PanelDisplayId);
        Assert.Equal(XeneonEdgeMonitorPath, plan.PanelMonitorInterfacePath);
        Assert.Equal(TouchMappingTier.Catalog, plan.Tier);
    }

    [Fact]
    public void Decide_never_plans_the_y70_digitizer_onto_the_edge_display()
    {
        // The Y70's digitizer carries no companion sibling (its hub has no
        // Corsair control endpoint), so it must never match the Edge's
        // companion-scoped catalog entry despite the identical VID/PID, even
        // when Windows misreports it as associated with the Edge's display.
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "edge-1", MonitorInterfacePath = XeneonEdgeMonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "primary-monitor" },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NoDigitizer, outcome);
        Assert.Empty(plans);
    }

    [Fact]
    public void Decide_produces_exactly_one_plan_for_the_live_box_snapshot()
    {
        // Ground truth captured live on the Y70 box: GetPointerDevices
        // reported both digitizers associated with the Y70's HMONITOR. Only
        // the Edge's digitizer should move; the Y70's is already correct.
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo>
            {
                new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath },
                new() { Id = "edge-1", MonitorInterfacePath = XeneonEdgeMonitorPath },
            },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "y70-1" },
                new()
                {
                    InterfacePath = XeneonEdgeDigitizerPath,
                    AssociatedDisplayId = "y70-1",
                    CompanionHardwareIds = { XeneonEdgeCompanionHardwareId },
                },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
        var plan = Assert.Single(plans);
        Assert.Equal(XeneonEdgeDigitizerPath, plan.DigitizerInterfacePath);
        Assert.Equal("edge-1", plan.PanelDisplayId);
        Assert.Equal(XeneonEdgeMonitorPath, plan.PanelMonitorInterfacePath);
    }

    [Fact]
    public void Decide_on_a_y70_only_box_is_unaffected_by_the_xeneon_catalog_entry()
    {
        // Regression guard: a box with only a Y70 attached, no Edge display
        // and no companion hardware ids recorded, must produce the same plan
        // it did before the Xeneon Edge catalog entry existed.
        var snapshot = Snapshot(
            new List<TouchMapDisplayInfo> { new() { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            new List<TouchMapDigitizerInfo>
            {
                new() { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "primary-monitor" },
            });

        var (outcome, plans) = TouchMappingDecision.Decide(snapshot);

        Assert.Equal(TouchMappingOutcome.NeedsRepair, outcome);
        var plan = Assert.Single(plans);
        Assert.Equal(Y70DigitizerPath, plan.DigitizerInterfacePath);
        Assert.Equal("y70-1", plan.PanelDisplayId);
        Assert.Equal(Y70MonitorPath, plan.PanelMonitorInterfacePath);
        Assert.Equal(TouchMappingTier.Catalog, plan.Tier);
    }
}
