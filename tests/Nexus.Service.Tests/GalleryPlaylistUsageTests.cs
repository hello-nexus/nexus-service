using System.Text.Json;
using Nexus.Service.Gallery;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public sealed class GalleryPlaylistUsageTests
{
    private static Dictionary<string, JsonElement> Plays(string playlistId) =>
        new() { ["playlistId"] = JsonSerializer.SerializeToElement(playlistId) };

    private static PanelWidgetDto Gallery(string? playlistId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = "gallery",
        Config = playlistId is null ? null : Plays(playlistId),
    };

    private static PanelLayoutDto Layout(params PanelWidgetDto[] widgets) =>
        new() { Pages = new() { new PanelPageDto { Id = "p1", Widgets = widgets.ToList() } } };

    [Fact]
    public void Finds_EverySurface_ThatPlaysIt_AndCountsEachWidget()
    {
        var s = new NexusSettings();
        s.Panel.DashboardLayout = Layout(Gallery("pl-a"), Gallery("pl-a"), Gallery("pl-b"), Gallery(null));
        s.PanelDevices["y70"] = new PanelDeviceRecord { Id = "y70", DisplayName = "Y70 Touch", Layout = Layout(Gallery("pl-a")) };
        s.PanelDevices["edge"] = new PanelDeviceRecord { Id = "edge", DisplayName = "Xeneon Edge", Layout = Layout(Gallery("pl-b")) };
        s.Overlay.Layout.Add(new OverlayWidgetDto { Id = "o1", Type = "gallery", Config = Plays("pl-a") });

        var uses = GalleryPlaylistUsage.Find(s, "pl-a");

        Assert.Equal(3, uses.Count);
        Assert.Equal((GalleryPlaylistUseSurfaces.Dashboard, "", 2), (uses[0].Surface, uses[0].Name, uses[0].Count));
        Assert.Equal((GalleryPlaylistUseSurfaces.Panel, "Y70 Touch", 1), (uses[1].Surface, uses[1].Name, uses[1].Count));
        Assert.Equal((GalleryPlaylistUseSurfaces.Desktop, "", 1), (uses[2].Surface, uses[2].Name, uses[2].Count));
    }

    [Fact]
    public void Counts_AQSeriesRememberedGalleryConfig()
    {
        var s = new NexusSettings();
        var layout = Layout(new PanelWidgetDto { Id = "c", Type = "clock" });
        layout.SingleWidgetConfigs = new() { ["gallery"] = Plays("pl-a") };
        s.PanelDevices["q60"] = new PanelDeviceRecord { Id = "q60", DisplayName = "Q60", Layout = layout };

        var use = Assert.Single(GalleryPlaylistUsage.Find(s, "pl-a"));
        Assert.Equal("Q60", use.Name);
    }

    [Fact]
    public void Counts_SavedDashboardPresets_ButNotTheActivePresetsStaleCopy()
    {
        var s = new NexusSettings();
        s.Panel.DashboardLayout = Layout(Gallery("pl-a"));
        s.Panel.DashboardPresets = new()
        {
            new DashboardPreset { Id = "active", Name = "Default", Layout = Layout(Gallery("pl-a"), Gallery("pl-a")) },
            new DashboardPreset { Id = "saved", Name = "Gaming", Layout = Layout(Gallery("pl-a")) },
        };
        s.Panel.DashboardActivePresetId = "active";

        var use = Assert.Single(GalleryPlaylistUsage.Find(s, "pl-a"));
        Assert.Equal((GalleryPlaylistUseSurfaces.Dashboard, 2), (use.Surface, use.Count));
    }

    [Fact]
    public void IgnoresOtherWidgetTypes_AndNonStringValues()
    {
        var s = new NexusSettings();
        s.Panel.DashboardLayout = Layout(
            new PanelWidgetDto { Id = "x", Type = "clock", Config = Plays("pl-a") },
            new PanelWidgetDto { Id = "y", Type = "gallery", Config = new() { ["playlistId"] = JsonSerializer.SerializeToElement(1) } });

        Assert.Empty(GalleryPlaylistUsage.Find(s, "pl-a"));
    }
}
