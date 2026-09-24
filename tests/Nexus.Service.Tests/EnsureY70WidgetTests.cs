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
        Assert.Equal(Y70WidgetPlacement.NoPanel, registry.EnsureY70Widget(InaType, "4x4", out _));
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
        Assert.Equal(Y70WidgetPlacement.NoPanel, registry.EnsureY70Widget(InaType, "4x4", out _));
    }

    // A null Layout means the record still shows the starter set, so saving
    // only this widget would wipe what the user currently sees.
    [Fact]
    public void PlacingOntoTheStarterLayoutKeepsItsWidgets()
    {
        var (registry, store) = WithY70();
        var starter = PanelLayoutDefaults.ForSurface(PanelSurfaces.Y70);
        var starterCount = starter.Pages.Sum(p => p.Widgets.Count);

        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4", out _));

        var types = TypesOn(store);
        Assert.Contains(InaType, types);
        Assert.Equal(starterCount + 1, types.Count);
    }

    // A 4x4 appended at a fixed cell landed on top of whatever already sat
    // there, and the client's repagination cannot repair a page it fails to
    // repack. Observed on the Y70: the Ina widget overlapped the clock.
    [Fact]
    public void PlacingNeverOverlapsAWidgetAlreadyOnThePage()
    {
        var layout = new PanelLayoutDto
        {
            Surface = PanelSurfaces.Y70,
            Pages = new List<PanelPageDto>
            {
                new() { Id = "p1", Widgets = new List<PanelWidgetDto>
                {
                    new() { Id = "w1", Type = "clock", Size = "4x2", Col = 0, Row = 0 },
                    new() { Id = "w2", Type = "cooling", Size = "2x2", Col = 0, Row = 2 },
                } },
            },
        };
        var (registry, store) = WithY70(layout);

        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4", out _));

        var placed = store.Settings.PanelDevices["y70-1"].Layout!.Pages
            .SelectMany(p => p.Widgets).Single(w => w.Type == InaType);
        foreach (var other in store.Settings.PanelDevices["y70-1"].Layout!.Pages.SelectMany(p => p.Widgets))
        {
            if (ReferenceEquals(other, placed)) continue;
            Assert.False(Overlaps(placed, other), $"Ina at ({placed.Col},{placed.Row}) overlaps {other.Type} at ({other.Col},{other.Row})");
        }
        // Old behaviour hard-coded (0,0), which is exactly where the clock sits.
        Assert.False(placed.Col == 0 && placed.Row == 0);
    }

    private static bool Overlaps(PanelWidgetDto a, PanelWidgetDto b)
    {
        var (ac, ar) = Span(a.Size); var (bc, br) = Span(b.Size);
        return a.Col < b.Col + bc && a.Col + ac > b.Col && a.Row < b.Row + br && a.Row + ar > b.Row;
    }

    private static (int Cols, int Rows) Span(string size) => size switch
    {
        "1x1" => (1, 1), "2x2" => (2, 2), "4x2" => (4, 2), "4x4" => (4, 4), _ => (2, 2),
    };

    // A full page must spill rather than stack on top of the existing widgets.
    [Fact]
    public void AFullPageSpillsToANewPage()
    {
        var full = new List<PanelWidgetDto>();
        for (var r = 0; r < 16; r += 2) full.Add(new PanelWidgetDto { Id = $"w{r}", Type = "clock", Size = "4x2", Col = 0, Row = r });
        var layout = new PanelLayoutDto
        {
            Surface = PanelSurfaces.Y70,
            Pages = new List<PanelPageDto> { new() { Id = "p1", Widgets = full } },
        };
        var (registry, store) = WithY70(layout);

        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4", out _));

        var pages = store.Settings.PanelDevices["y70-1"].Layout!.Pages;
        Assert.Equal(2, pages.Count);
        Assert.Contains(pages[1].Widgets, w => w.Type == InaType);
        Assert.DoesNotContain(pages[0].Widgets, w => w.Type == InaType);
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

        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4", out _));

        Assert.Equal(new[] { "clock", InaType }, TypesOn(store));
        var placed = store.Settings.PanelDevices["y70-1"].Layout!.Pages[0].Widgets.Single(w => w.Type == InaType);
        Assert.Equal("4x4", placed.Size);
        Assert.NotEqual("", placed.Id);
        Assert.False(Overlaps(placed, store.Settings.PanelDevices["y70-1"].Layout!.Pages[0].Widgets[0]));
    }

    [Fact]
    public void PlacingTwiceLeavesOneCopy()
    {
        var (registry, store) = WithY70();
        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4", out _));
        Assert.Equal(Y70WidgetPlacement.AlreadyPresent, registry.EnsureY70Widget(InaType, "4x4", out _));
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
        Assert.Equal(Y70WidgetPlacement.AlreadyPresent, registry.EnsureY70Widget(InaType, "4x4", out _));
        Assert.Single(TypesOn(store), t => t == InaType);
    }

    // The caller broadcasts to this id; a running kiosk that never refetches
    // PATCHes its whole layout back and drops the append.
    [Fact]
    public void PlacingNamesTheRecordItWroteTo()
    {
        var (registry, _) = WithY70();
        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4", out var deviceId));
        Assert.Equal("y70-1", deviceId);
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

        Assert.Equal(Y70WidgetPlacement.Placed, registry.EnsureY70Widget(InaType, "4x4", out _));

        Assert.Contains(InaType, TypesOn(store, "new"));
        Assert.DoesNotContain(InaType, TypesOn(store, "old"));
    }
}
