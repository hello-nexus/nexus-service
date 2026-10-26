using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;
using static Nexus.Service.Panel.DesktopWidgetsMigration;

namespace Nexus.Service.Tests.Panel;

public class DesktopWidgetsMigrationTests
{
    private const string Primary = "DEL41B7-1";
    private const string Second = "DEL41B7-2";

    private static DisplayTopologyEntryDto Display(string id, bool primary, bool y70 = false, bool hosting = true) => new()
    {
        Id = id,
        Name = "Monitor " + id,
        Manufacturer = "DEL",
        Model = "41B7",
        Resolution = new DisplaySizeDto { Width = 2560, Height = 1440 },
        ScaleFactor = 1.0,
        IsPrimary = primary,
        IsY70 = y70,
        HostingSupported = hosting,
        Fingerprint = new DisplayFingerprint { Manufacturer = "DEL", Product = "41B7", Serial = 7 },
    };

    private static DisplayTopologyResponse Topology(params DisplayTopologyEntryDto[] displays)
    {
        var r = new DisplayTopologyResponse();
        r.Displays.AddRange(displays);
        return r;
    }

    private static DisplayTopologyResponse Two() => Topology(Display(Primary, true), Display(Second, false));
    private static DisplayTopologyResponse One() => Topology(Display(Primary, true));

    private static OverlayWidgetDto Widget(string id, string type = "clock", double col = 0, double row = 0, bool locked = false, string size = "2x2", Dictionary<string, JsonElement>? config = null) => new()
    {
        Id = id,
        Type = type,
        Size = size,
        Col = col,
        Row = row,
        Locked = locked,
        Config = config,
    };

    private static (DesktopWidgetsMigration Migration, InMemoryConfigStore Store, OverlayMonitorMap Map) Build(HostOs os, params OverlayWidgetDto[] widgets)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Overlay.Enabled = true;
            s.Overlay.Layout.AddRange(widgets);
        });
        var map = new OverlayMonitorMap();
        return (new DesktopWidgetsMigration(store, map, null, null) { Os = os }, store, map);
    }

    private static PanelDeviceRecord Only(InMemoryConfigStore store) => Assert.Single(store.Load().PanelDevices.Values);

    private static string Snapshot(InMemoryConfigStore store) =>
        JsonSerializer.Serialize(store.Load(), PersistenceJsonContext.Default.NexusSettings);

    [Fact]
    public void Empty_layout_sets_the_marker_and_creates_nothing()
    {
        var (m, store, _) = Build(HostOs.Windows);

        Assert.Null(m.Run(One()));

        Assert.True(store.Load().Overlay.MigratedToPanels);
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void Second_run_changes_nothing()
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("a"));
        Assert.NotNull(m.Run(One()));
        var before = Snapshot(store);

        Assert.Null(m.Run(One()));

        Assert.Equal(before, Snapshot(store));
    }

    [Fact]
    public void Negative_monitor_targets_the_primary()
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.Overlay.Monitor = -1);

        m.Run(Two());

        Assert.Equal(Primary, Only(store).DisplayId);
    }

    [Fact]
    public void Index_resolves_through_the_posted_map_and_an_unknown_index_falls_back_to_primary()
    {
        var (m, store, map) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.Overlay.Monitor = 1);
        map.Set(new[]
        {
            new OverlayMonitorDto { Index = 0, DisplayId = Primary, Primary = true },
            new OverlayMonitorDto { Index = 1, DisplayId = Second },
        });
        m.Run(Two());
        Assert.Equal(Second, Only(store).DisplayId);

        var (m2, store2, map2) = Build(HostOs.Windows, Widget("a"));
        store2.Update(s => s.Overlay.Monitor = 5);
        map2.Set(new[] { new OverlayMonitorDto { Index = 0, DisplayId = Primary, Primary = true } });
        m2.Run(Two());
        Assert.Equal(Primary, Only(store2).DisplayId);
    }

    [Fact]
    public void Windows_with_several_displays_and_no_map_defers_without_a_marker()
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.Overlay.Monitor = 1);

        Assert.Null(m.Run(Two()));

        Assert.False(store.Load().Overlay.MigratedToPanels);
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void Windows_with_one_display_resolves_it_without_a_map()
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.Overlay.Monitor = 3);

        m.Run(One());

        Assert.Equal(Primary, Only(store).DisplayId);
    }

    [Fact]
    public void MacOS_ignores_the_monitor_index()
    {
        var (m, store, map) = Build(HostOs.MacOS, Widget("a"));
        store.Update(s => s.Overlay.Monitor = 1);
        map.Set(new[] { new OverlayMonitorDto { Index = 1, DisplayId = Second } });

        m.Run(Two());

        Assert.Equal(Primary, Only(store).DisplayId);
    }

    [Fact]
    public void Missing_topology_defers()
    {
        var (m, store, _) = Build(HostOs.MacOS, Widget("a"));

        Assert.Null(m.Run(new DisplayTopologyResponse()));
        Assert.Null(m.Run());

        Assert.False(store.Load().Overlay.MigratedToPanels);
    }

    [Fact]
    public void Linux_does_nothing_and_sets_no_marker()
    {
        var (m, store, _) = Build(HostOs.Linux, Widget("a"));

        Assert.Null(m.Run(One()));

        Assert.False(store.Load().Overlay.MigratedToPanels);
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void Y70_target_falls_back_to_the_primary()
    {
        var (m, store, map) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.Overlay.Monitor = 1);
        map.Set(new[] { new OverlayMonitorDto { Index = 1, DisplayId = Second } });

        m.Run(Topology(Display(Primary, true), Display(Second, false, y70: true)));

        Assert.Equal(Primary, Only(store).DisplayId);
    }

    [Theory]
    [InlineData(true, false, 0.0, true)]
    [InlineData(false, true, 0.35, false)]
    [InlineData(true, true, 1.0, true)]
    public void New_record_carries_the_desktop_flags(bool onTop, bool overlayEnabled, double opacity, bool expectedOnTop)
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("a"));
        store.Update(s =>
        {
            s.Overlay.AlwaysOnTop = onTop;
            s.Overlay.Enabled = overlayEnabled;
            s.Overlay.Opacity = opacity;
        });

        m.Run(One());

        var r = Only(store);
        Assert.Equal("desktop", r.Backdrop);
        Assert.True(r.ClickThrough);
        Assert.Equal(expectedOnTop, r.AlwaysOnTop);
        Assert.False(r.ReserveMonitor);
        Assert.False(r.KeepCursorOff);
        Assert.Equal(opacity, r.WidgetOpacity);
        Assert.True(r.ThemeSyncWithDesktop);
        Assert.True(r.AccentSyncWithDesktop);
        Assert.Equal(overlayEnabled, r.Hosted);
        Assert.Equal(overlayEnabled, PanelHosting.IsOn(r));
        Assert.False(r.Enabled);
        Assert.Equal("monitor", r.Capabilities?.Surface);
        Assert.NotNull(r.DisplayFingerprint);
        var layout = Assert.IsType<PanelLayoutDto>(r.Layout);
        Assert.Equal(2, layout.LayoutSchemaVersion);
        Assert.Empty(Assert.Single(layout.Pages).Widgets);
        Assert.False(r.DesktopImport!.Merge);
    }

    [Theory]
    [InlineData(50, 43.0)]
    [InlineData(100, 86.0)]
    [InlineData(137, 117.82)]
    [InlineData(200, 172.0)]
    [InlineData(10, 43.0)]
    [InlineData(900, 172.0)]
    public void Cell_px_follows_the_clamped_scale(int scale, double expected)
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.Overlay.Scale = scale);

        m.Run(One());

        Assert.Equal(expected, Only(store).DesktopImport!.CellPx, 2);
    }

    [Theory]
    [InlineData(HostOs.Windows, "device")]
    [InlineData(HostOs.MacOS, "css")]
    public void Cell_units_follow_the_host_os(HostOs os, string units)
    {
        var (m, store, _) = Build(os, Widget("a"));

        m.Run(One());

        Assert.Equal(units, Only(store).DesktopImport!.CellUnits);
        Assert.Equal(1, Only(store).DesktopImport!.Version);
    }

    [Fact]
    public void Discord_is_dropped_and_apps_pins_config_and_locks_survive()
    {
        using var doc = JsonDocument.Parse("{\"panelOnly\":true,\"n\":3}");
        var config = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var (m, store, _) = Build(HostOs.Windows,
            Widget("d", "discord"),
            Widget("p", "app:com.example.thing", 4.25, 1.5, locked: true, size: "2x4", config: config),
            Widget("c", "clock", 1, 1));

        m.Run(One());

        var widgets = Only(store).DesktopImport!.Widgets;
        Assert.Equal(new[] { "p", "c" }, widgets.Select(w => w.Id));
        var pin = widgets[0];
        Assert.Equal("app:com.example.thing", pin.Type);
        Assert.True(pin.Locked);
        Assert.Equal(4.25, pin.Col);
        Assert.Equal("2x4", pin.Size);
        Assert.True(pin.Config!["panelOnly"].GetBoolean());
        Assert.NotSame(store.Load().Overlay.Layout[1], pin);
    }

    [Fact]
    public void Only_discord_widgets_set_the_marker_without_a_record()
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("d", "discord"));

        Assert.Null(m.Run(One()));

        Assert.True(store.Load().Overlay.MigratedToPanels);
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void Existing_active_record_keeps_its_flags_and_looks_and_merges()
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.PanelDevices["r1"] = new PanelDeviceRecord
        {
            Id = "r1",
            DisplayId = Primary,
            Backdrop = "wallpaper",
            ClickThrough = false,
            AlwaysOnTop = true,
            ReserveMonitor = true,
            WidgetOpacity = 0.9,
            Layout = new PanelLayoutDto { LayoutSchemaVersion = 2, Surface = "monitor" },
        });

        Assert.Equal("r1", m.Run(One()));

        var r = Only(store);
        Assert.Equal("wallpaper", r.Backdrop);
        Assert.False(r.ClickThrough);
        Assert.True(r.AlwaysOnTop);
        Assert.True(r.ReserveMonitor);
        Assert.Equal(0.9, r.WidgetOpacity);
        Assert.Null(r.Hosted);
        Assert.True(r.DesktopImport!.Merge);
        Assert.Equal("r1", store.Load().Overlay.MigratedRecordId);
    }

    [Fact]
    public void Existing_disabled_record_gets_the_desktop_flags_and_keeps_its_layout()
    {
        var layout = new PanelLayoutDto
        {
            LayoutSchemaVersion = 2,
            Surface = "monitor",
            Pages = new List<PanelPageDto> { new() { Id = "pg", Widgets = new List<PanelWidgetDto> { new() { Id = "w", Type = "clock" } } } },
        };
        var (m, store, _) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.PanelDevices["r1"] = new PanelDeviceRecord { Id = "r1", DisplayId = Primary, Enabled = false, Layout = layout });

        m.Run(One());

        var r = Only(store);
        Assert.Same(layout, r.Layout);
        Assert.Equal("desktop", r.Backdrop);
        Assert.True(r.ClickThrough);
        Assert.True(r.Hosted);
        Assert.False(r.Enabled);
        Assert.True(r.DesktopImport!.Merge);
    }

    [Fact]
    public void The_overlay_block_is_left_as_it_was_apart_from_the_marker()
    {
        var (m, store, _) = Build(HostOs.Windows, Widget("a"), Widget("d", "discord"));
        store.Update(s =>
        {
            s.Overlay.Scale = 137;
            s.Overlay.Opacity = 0.4;
            s.Overlay.Monitor = -1;
            s.Overlay.AlwaysOnTop = true;
        });
        var before = JsonSerializer.Serialize(store.Load().Overlay, PersistenceJsonContext.Default.OverlaySettings);

        m.Run(One());

        var after = store.Load().Overlay;
        Assert.True(after.MigratedToPanels);
        Assert.Equal(Only(store).Id, after.MigratedRecordId);
        after.MigratedToPanels = false;
        after.MigratedRecordId = null;
        Assert.Equal(before, JsonSerializer.Serialize(after, PersistenceJsonContext.Default.OverlaySettings));
    }

    [Fact]
    public void Posting_the_monitor_map_after_a_deferral_migrates()
    {
        var (m, store, map) = Build(HostOs.Windows, Widget("a"));
        store.Update(s => s.Overlay.Monitor = 1);
        Assert.Null(m.Run(Two()));

        map.Set(new[] { new OverlayMonitorDto { Index = 1, DisplayId = Second } });

        Assert.NotNull(m.Run(Two()));
        Assert.Equal(Second, Only(store).DisplayId);
    }

    private static readonly OverlayWidgetDto[] OneWidget = { new() { Id = "a", Type = "clock" } };

    private sealed class ProfileFixture : System.IDisposable
    {
        public string Dir { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-desktop-mig-" + System.Guid.NewGuid().ToString("N")[..8]);
        public JsonConfigStore Store { get; }
        public ProfileManager Profiles { get; }
        public string TargetId { get; }

        public ProfileFixture(bool panelsShared)
        {
            System.IO.Directory.CreateDirectory(Dir);
            Store = new JsonConfigStore(System.IO.Path.Combine(Dir, "settings.json"));
            Profiles = new ProfileManager(Store);
            Profiles.Initialize();
            TargetId = Profiles.CreateProfile("Target").Id;
            Profiles.CreateProfile("Other");
            Store.Update(s =>
            {
                s.Overlay.MigratedToPanels = true;
                s.SharedCategories = panelsShared ? new List<string> { ProfileSharing.Panels } : new List<string>();
            });
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(Dir, $"profile-{TargetId}.json"),
                "{\"overlay\":{\"enabled\":true,\"layout\":[{\"id\":\"x\",\"type\":\"clock\",\"size\":\"2x2\"}]}}");
        }

        public void Dispose()
        {
            Profiles.Dispose();
            Store.Dispose();
            try { System.IO.Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_later_profile_with_shared_panels_gets_only_the_marker()
    {
        using var f = new ProfileFixture(panelsShared: true);

        f.Profiles.SwitchProfile(f.TargetId);

        Assert.True(f.Store.Load().Overlay.MigratedToPanels);
        Assert.Single(f.Store.Load().Overlay.Layout);
        var m = new DesktopWidgetsMigration(f.Store, new OverlayMonitorMap(), null, null) { Os = HostOs.Windows };
        Assert.Null(m.Run(One()));
        Assert.Empty(f.Store.Load().PanelDevices);
    }

    [Fact]
    public void A_later_profile_with_per_profile_panels_migrates_normally()
    {
        using var f = new ProfileFixture(panelsShared: false);

        f.Profiles.SwitchProfile(f.TargetId);

        Assert.False(f.Store.Load().Overlay.MigratedToPanels);
        var m = new DesktopWidgetsMigration(f.Store, new OverlayMonitorMap(), null, null) { Os = HostOs.Windows };
        Assert.NotNull(m.Run(One()));
        Assert.Equal("x", Assert.Single(Assert.Single(f.Store.Load().PanelDevices.Values).DesktopImport!.Widgets).Id);
    }

    [Fact]
    public void MarkLaterProfile_needs_a_migrated_predecessor()
    {
        var s = new NexusSettings();
        s.Overlay.Layout.AddRange(OneWidget);

        DesktopWidgetsMigration.MarkLaterProfile(s, priorMigrated: false);

        Assert.False(s.Overlay.MigratedToPanels);
    }
}
