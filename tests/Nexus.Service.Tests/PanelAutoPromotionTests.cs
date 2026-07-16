using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;
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
}
