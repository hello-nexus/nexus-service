using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

public class EnsureY70WidgetTests
{
    private const string InaType = "app:com.hellonexus.ina";

    private sealed class MemoryConfigStore : IConfigStore
    {
        public NexusSettings Settings { get; } = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => Settings;
        public void Update(Action<NexusSettings> mutator) { mutator(Settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private static (PanelDeviceRegistry Registry, MemoryConfigStore Store) WithY70(PanelLayoutDto? layout = null, long lastSeen = 100)
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70-1"] = new PanelDeviceRecord
        {
            Id = "y70-1",
            LastSeenAt = lastSeen,
            Layout = layout,
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 },
        };
        return (new PanelDeviceRegistry(store), store);
    }

    private static List<string> TypesOn(MemoryConfigStore store, string deviceId = "y70-1") =>
        store.Settings.PanelDevices[deviceId].Layout!.Pages.SelectMany(p => p.Widgets).Select(w => w.Type).ToList();

    [Fact]
    public void NoPanelRecordIsRetryableRatherThanDone()
    {
        var registry = new PanelDeviceRegistry(new MemoryConfigStore());
        Assert.Equal(Y70WidgetPlacement.NoPanel, registry.EnsureY70Widget(InaType, "4x4"));
    }

    [Fact]
    public void APanelOnAnotherSurfaceIsNotUsed()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["phone-1"] = new PanelDeviceRecord
        {
            Id = "phone-1",
            Capabilities = new PanelDeviceCapabilities { Surface = "phone" },
        };
        var registry = new PanelDeviceRegistry(store);
        Assert.Equal(Y70WidgetPlacement.NoPanel, registry.EnsureY70Widget(InaType, "4x4"));
    }

    // A null Layout means the record still shows the starter set, so saving
    // only this widget would wipe what the user currently sees.
    [Fact]
    public void PlacingOntoTheStarterLayoutKeepsItsWidgets()
    {
        var (registry, store) = WithY70();
        var starter = PanelLayoutDefaults.ForSurface(PanelSurfaces.Y70);
        var starterCount = starter.Pages.Sum(p => p.Widgets.Count);

        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4"));

        var types = TypesOn(store);
        Assert.Contains(InaType, types);
        Assert.Equal(starterCount + 1, types.Count);
    }

    [Fact]
    public void PlacingAppendsToAnExistingLayoutWithoutDisturbingIt()
    {
        var layout = new PanelLayoutDto
        {
            Surface = PanelSurfaces.Y70,
            Pages = new List<PanelPageDto>
            {
                new() { Id = "p1", Widgets = new List<PanelWidgetDto> { new() { Id = "w1", Type = "clock", Size = "2x2" } } },
            },
        };
        var (registry, store) = WithY70(layout);

        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4"));

        Assert.Equal(new[] { "clock", InaType }, TypesOn(store));
        var placed = store.Settings.PanelDevices["y70-1"].Layout!.Pages[0].Widgets[1];
        Assert.Equal("4x4", placed.Size);
        Assert.NotEqual("", placed.Id);
    }

    [Fact]
    public void PlacingTwiceLeavesOneCopy()
    {
        var (registry, store) = WithY70();
        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4"));
        Assert.Equal(Y70WidgetPlacement.AlreadyPresent, registry.EnsureY70Widget(InaType, "4x4"));
        Assert.Single(TypesOn(store), t => t == InaType);
    }

    [Fact]
    public void AWidgetAlreadyOnAPageIsFoundWhicheverPageItIsOn()
    {
        var layout = new PanelLayoutDto
        {
            Surface = PanelSurfaces.Y70,
            Pages = new List<PanelPageDto>
            {
                new() { Id = "p1", Widgets = new List<PanelWidgetDto>() },
                new() { Id = "p2", Widgets = new List<PanelWidgetDto> { new() { Id = "w1", Type = InaType, Size = "4x4" } } },
            },
        };
        var (registry, store) = WithY70(layout);
        Assert.Equal(Y70WidgetPlacement.AlreadyPresent, registry.EnsureY70Widget(InaType, "4x4"));
        Assert.Single(TypesOn(store), t => t == InaType);
    }

    [Fact]
    public void TheMostRecentlySeenY70Wins()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["old"] = new PanelDeviceRecord
        {
            Id = "old",
            LastSeenAt = 1,
            Layout = new PanelLayoutDto { Surface = PanelSurfaces.Y70, Pages = new List<PanelPageDto> { new() { Id = "p" } } },
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 },
        };
        store.Settings.PanelDevices["new"] = new PanelDeviceRecord
        {
            Id = "new",
            LastSeenAt = 9,
            Layout = new PanelLayoutDto { Surface = PanelSurfaces.Y70, Pages = new List<PanelPageDto> { new() { Id = "p" } } },
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 },
        };
        var registry = new PanelDeviceRegistry(store);

        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4"));

        Assert.Contains(InaType, TypesOn(store, "new"));
        Assert.DoesNotContain(InaType, TypesOn(store, "old"));
    }
}
