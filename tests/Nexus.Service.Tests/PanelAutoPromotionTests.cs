using System.Threading.Tasks;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;
using Nexus.Service.Tests.Corsair;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Auto-promotion of curated always-a-panel displays (KnownPanelDisplays):
/// a Xeneon Edge must become a panel with no manual promote, at most once
/// per EDID model per machine, without fighting user-managed or deleted
/// records.
/// </summary>
public class PanelAutoPromotionTests
{
    private const string XeneonDisplayId = "DISPLAY\\CRXED00\\5&1a2b3c4d&0&UID4353";

    private static DisplayTopologyEntryDto Display(
        string id = XeneonDisplayId,
        string name = "Xeneon Edge",
        string manufacturer = "CRX",
        string model = "ED00",
        bool isY70 = false,
        bool hostingSupported = true,
        string? assignedId = null) => new()
    {
        Id = id,
        Name = name,
        Manufacturer = manufacturer,
        Model = model,
        Resolution = new DisplaySizeDto { Width = 2560, Height = 720 },
        ScaleFactor = 1.0,
        IsTouch = false,
        IsY70 = isY70,
        HostingSupported = hostingSupported,
        AssignedPanelDeviceId = assignedId,
    };

    private static DisplayTopologyEntryDto Xeneon(string? assignedId = null) => Display(assignedId: assignedId);

    private static DisplayTopologyResponse Topology(params DisplayTopologyEntryDto[] displays)
    {
        var response = new DisplayTopologyResponse();
        foreach (var d in displays) response.Displays.Add(d);
        return response;
    }

    private static (PanelAutoPromotion Promotion, PanelDeviceRegistry Registry, InMemoryConfigStore Store) Build()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        return (new PanelAutoPromotion(registry, store), registry, store);
    }

    [Fact]
    public void Curated_display_is_promoted_once_with_known_capabilities()
    {
        var (promotion, registry, store) = Build();

        var created = promotion.Reconcile(Topology(Xeneon()));

        Assert.Single(created);
        var record = registry.FindByDisplayId(XeneonDisplayId);
        Assert.NotNull(record);
        Assert.Equal("monitor", record!.Capabilities?.Surface);
        Assert.Equal("xeneon-edge", record.Capabilities?.Family);
        Assert.True(record.Capabilities?.Touch);
        Assert.Contains("CRX:ED00", store.Load().AutoPromotedPanelModels);

        Assert.Empty(promotion.Reconcile(Topology(Xeneon(assignedId: record.Id))));
        Assert.Single(store.Load().PanelDevices);
    }

    [Fact]
    public void Deleted_auto_record_is_not_recreated()
    {
        var (promotion, registry, store) = Build();
        var created = promotion.Reconcile(Topology(Xeneon()));
        var recordId = Assert.Single(created);

        store.Update(s => s.PanelDevices.Remove(recordId));

        Assert.Empty(promotion.Reconcile(Topology(Xeneon())));
        // A port change mints a fresh displayId; the model-keyed seen set
        // must still suppress re-promotion of the deleted panel.
        Assert.Empty(promotion.Reconcile(Topology(Display(id: "DISPLAY\\CRXED00\\9&other&0&UID9999"))));
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void User_managed_record_is_marked_seen_and_untouched()
    {
        var (promotion, registry, store) = Build();
        var (manual, _) = registry.AllocateForDisplay(XeneonDisplayId, "My Edge", null);
        registry.DisablePanelForDisplay(XeneonDisplayId);

        var created = promotion.Reconcile(Topology(Xeneon()));

        Assert.Empty(created);
        var record = registry.FindByDisplayId(XeneonDisplayId);
        Assert.Equal(manual.Id, record!.Id);
        Assert.False(record.Enabled ?? true);
        Assert.Contains("CRX:ED00", store.Load().AutoPromotedPanelModels);
    }

    [Fact]
    public void Non_curated_y70_and_unhosted_displays_are_ignored()
    {
        var (promotion, _, store) = Build();
        var plain = Display(id: "DISPLAY\\SAM71AA\\1", manufacturer: "SAM", model: "71AA", name: "Odyssey G9");
        var y70 = Display(id: "DISPLAY\\RTK0004\\1", isY70: true);
        var unhosted = Display(id: XeneonDisplayId + "-b", hostingSupported: false);

        Assert.Empty(promotion.Reconcile(Topology(plain, y70, unhosted)));
        Assert.Empty(store.Load().PanelDevices);
        Assert.Empty(store.Load().AutoPromotedPanelModels);
    }

    private const string Y60DisplayId = "DISPLAY\\RTKBC32\\5&2b3c4d5e&0&UID256";

    private static DisplayTopologyEntryDto Y60(int width = 1920, int height = 515, string id = Y60DisplayId) => new()
    {
        Id = id,
        Name = "RTK BC32",
        Manufacturer = "RTK",
        Model = "BC32",
        Resolution = new DisplaySizeDto { Width = width, Height = height },
        ScaleFactor = 1.0,
        IsTouch = false,
        HostingSupported = true,
    };

    private static (PanelAutoPromotion Promotion, PanelDeviceRegistry Registry, InMemoryConfigStore Store, FakeDisplayOrientationProvider Orientation) BuildWithOrientation()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var orientation = new FakeDisplayOrientationProvider();
        return (new PanelAutoPromotion(registry, store, orientation), registry, store, orientation);
    }

    [Fact]
    public void Y60_kit_is_promoted_with_family_dpi_and_no_touch()
    {
        var (promotion, registry, store, _) = BuildWithOrientation();

        Assert.Single(promotion.Reconcile(Topology(Y60())));

        var record = registry.FindByDisplayId(Y60DisplayId);
        Assert.Equal("hyte-y60", record!.Capabilities?.Family);
        Assert.Equal(158, record.Capabilities?.Dpi);
        Assert.False(record.Capabilities?.Touch);
        Assert.Contains("RTK:BC32", store.Load().AutoPromotedPanelModels);
    }

    [Fact]
    public void Y60_first_promotion_rotates_to_portrait_flipped_and_persists_once()
    {
        var (promotion, registry, _, orientation) = BuildWithOrientation();

        promotion.Reconcile(Topology(Y60()));

        var call = Assert.Single(orientation.Calls);
        Assert.Equal((Y60DisplayId, "PortraitFlipped"), (call.DisplayId, call.Orientation));
        Assert.Equal("PortraitFlipped", registry.FindByDisplayId(Y60DisplayId)!.Capabilities?.Orientation);

        // Rotated: resolution swaps; later passes and a user-picked orientation are left alone.
        var record = registry.FindByDisplayId(Y60DisplayId)!;
        promotion.Reconcile(Topology(Y60(515, 1920)));
        registry.UpdateDisplayOrientation(Y60DisplayId, "Portrait");
        promotion.Reconcile(Topology(Y60()));
        Assert.Single(orientation.Calls);
        Assert.Equal("Portrait", registry.FindByDisplayId(Y60DisplayId)!.Capabilities?.Orientation);
        Assert.Equal(record.Id, registry.FindByDisplayId(Y60DisplayId)!.Id);
    }

    [Fact]
    public void Y60_already_portrait_is_promoted_without_rotating()
    {
        var (promotion, _, _, orientation) = BuildWithOrientation();

        Assert.Single(promotion.Reconcile(Topology(Y60(515, 1920))));

        Assert.Empty(orientation.Calls);
    }

    [Fact]
    public async Task Y60_rotation_that_fails_is_replayed_and_then_persisted()
    {
        var (promotion, registry, _, orientation) = BuildWithOrientation();
        orientation.NextOk = false;
        orientation.NextError = "no helper connected";

        promotion.Reconcile(Topology(Y60()));
        Assert.Null(registry.FindByDisplayId(Y60DisplayId)!.Capabilities?.Orientation);

        orientation.NextOk = true;
        await promotion.ReplayPendingAsync();

        Assert.Equal(2, orientation.Calls.Count);
        Assert.Equal("PortraitFlipped", registry.FindByDisplayId(Y60DisplayId)!.Capabilities?.Orientation);
        await promotion.ReplayPendingAsync();
        Assert.Equal(2, orientation.Calls.Count);
    }

    [Fact]
    public void Generic_rtk_bc32_monitor_at_other_resolution_is_not_promoted()
    {
        var (promotion, _, store, orientation) = BuildWithOrientation();
        var generic = Y60(1920, 1080, id: "DISPLAY\\RTKBC32\\generic");
        generic.Name = "RTK FHD HDR";

        Assert.Empty(promotion.Reconcile(Topology(generic)));
        Assert.Empty(store.Load().PanelDevices);
        Assert.Empty(orientation.Calls);
    }
}
