using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public class DesktopPanelRecordTests
{
    private static PanelDeviceCapabilities Monitor() => new() { Surface = PanelSurfaces.Monitor };

    private static (PanelDeviceRegistry Registry, InMemoryConfigStore Store) Build(bool seeThrough = true)
    {
        var store = new InMemoryConfigStore();
        return (new PanelDeviceRegistry(store) { SeeThroughHost = seeThrough }, store);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, null, false)]
    public void IsOn_prefers_hosted_over_enabled(bool? hosted, bool? enabled, bool expected)
    {
        Assert.Equal(expected, PanelHosting.IsOn(new PanelDeviceRecord { Hosted = hosted, Enabled = enabled }));
    }

    [Fact]
    public void Clone_projects_enabled_from_hosting_and_keeps_hosted_off_the_response()
    {
        var (registry, store) = Build();
        store.Update(s => s.PanelDevices["r"] = new PanelDeviceRecord
        {
            Id = "r",
            DisplayId = "D1",
            Hosted = true,
            Enabled = false,
            ClickThrough = true,
            GridSlots = 12,
            DesktopImport = new DesktopImportDto { CellPx = 86, Widgets = { new OverlayWidgetDto { Id = "w", Type = "clock" } } },
        });

        var clone = registry.FindByDisplayId("D1")!;

        Assert.True(clone.Enabled);
        Assert.Null(clone.Hosted);
        Assert.True(clone.ClickThrough);
        Assert.Equal(12, clone.GridSlots);
        Assert.Equal("w", Assert.Single(clone.DesktopImport!.Widgets).Id);
        Assert.False(store.Load().PanelDevices["r"].Enabled);
    }

    [Fact]
    public void Turning_a_hosted_panel_off_and_on_goes_through_hosted()
    {
        var (registry, store) = Build();
        store.Update(s => s.PanelDevices["r"] = new PanelDeviceRecord { Id = "r", DisplayId = "D1", Hosted = true, Enabled = false });

        registry.DisablePanelForDisplay("D1");
        var stored = store.Load().PanelDevices["r"];
        Assert.False(stored.Hosted);
        Assert.False(stored.Enabled);
        Assert.Empty(registry.ListAssignments());

        var (_, activated) = registry.AllocateForDisplay("D1", null, Monitor());
        Assert.True(activated);
        Assert.True(store.Load().PanelDevices["r"].Hosted);
        Assert.False(store.Load().PanelDevices["r"].Enabled);
        Assert.Single(registry.ListAssignments());
    }

    [Fact]
    public void Primary_display_forces_the_keep_off_guards_false_and_flags_the_assignment()
    {
        var (registry, store) = Build();
        store.Update(s =>
        {
            s.PanelDevices["a"] = new PanelDeviceRecord { Id = "a", DisplayId = "P", ReserveMonitor = true, KeepCursorOff = true, AlwaysOnTop = true };
            s.PanelDevices["b"] = new PanelDeviceRecord { Id = "b", DisplayId = "S", ReserveMonitor = true, KeepCursorOff = true };
        });

        var list = registry.ListAssignments(id => id == "P");

        var primary = list.Single(a => a.DisplayId == "P");
        Assert.True(primary.IsPrimary);
        Assert.False(primary.ReserveMonitor);
        Assert.False(primary.KeepCursorOff);
        Assert.True(primary.AlwaysOnTop);
        var secondary = list.Single(a => a.DisplayId == "S");
        Assert.False(secondary.IsPrimary);
        Assert.True(secondary.ReserveMonitor);
        Assert.True(secondary.KeepCursorOff);
        Assert.True(registry.ListAssignments().Single(a => a.DisplayId == "P").ReserveMonitor);
    }

    [Theory]
    [InlineData("desktop", true, true)]
    [InlineData("wallpaper", true, false)]
    [InlineData("", true, false)]
    [InlineData("desktop", false, false)]
    public void Click_through_only_reads_true_over_a_desktop_backdrop(string backdrop, bool stored, bool expected)
    {
        var (registry, store) = Build();
        store.Update(s => s.PanelDevices["a"] = new PanelDeviceRecord { Id = "a", DisplayId = "P", Backdrop = backdrop, ClickThrough = stored });

        Assert.Equal(expected, Assert.Single(registry.ListAssignments()).ClickThrough);
    }

    [Fact]
    public void New_primary_panel_gets_desktop_defaults_and_a_secondary_keeps_null_defaults()
    {
        var (registry, store) = Build();

        var (primary, _) = registry.AllocateForDisplay("P", null, Monitor(), primary: true);
        var (secondary, _) = registry.AllocateForDisplay("S", null, Monitor());

        Assert.Equal("desktop", primary.Backdrop);
        Assert.True(primary.ClickThrough);
        Assert.False(primary.AlwaysOnTop);
        Assert.False(primary.ReserveMonitor);
        Assert.False(primary.KeepCursorOff);
        Assert.True(primary.Enabled);
        Assert.True(store.Load().PanelDevices[primary.Id].Hosted);
        Assert.False(store.Load().PanelDevices[primary.Id].Enabled);
        Assert.Null(secondary.Backdrop);
        Assert.Null(secondary.ClickThrough);
        Assert.Null(secondary.AlwaysOnTop);
        Assert.Null(store.Load().PanelDevices[secondary.Id].Hosted);
    }

    [Fact]
    public void Re_enabling_a_disabled_record_on_the_primary_display_applies_the_defaults_through_hosted()
    {
        var (registry, store) = Build();
        store.Update(s => s.PanelDevices["r"] = new PanelDeviceRecord
        {
            Id = "r",
            DisplayId = "P",
            Enabled = false,
            Backdrop = "wallpaper",
            AlwaysOnTop = true,
            ReserveMonitor = true,
        });

        var (record, activated) = registry.AllocateForDisplay("P", null, Monitor(), primary: true);

        Assert.True(activated);
        var stored = store.Load().PanelDevices["r"];
        Assert.True(stored.Hosted);
        Assert.False(stored.Enabled);
        Assert.Equal("desktop", stored.Backdrop);
        Assert.True(stored.ClickThrough);
        Assert.False(stored.AlwaysOnTop);
        Assert.False(stored.ReserveMonitor);
        Assert.True(record.Enabled);
    }

    [Fact]
    public void Re_enabling_off_the_primary_display_leaves_the_record_as_it_was()
    {
        var (registry, store) = Build();
        store.Update(s => s.PanelDevices["r"] = new PanelDeviceRecord { Id = "r", DisplayId = "S", Enabled = false, Backdrop = "wallpaper" });

        registry.AllocateForDisplay("S", null, Monitor());

        var stored = store.Load().PanelDevices["r"];
        Assert.True(stored.Enabled);
        Assert.Null(stored.Hosted);
        Assert.Equal("wallpaper", stored.Backdrop);
    }

    [Fact]
    public void Primary_panel_on_an_opaque_host_skips_the_see_through_flags()
    {
        var (registry, _) = Build(seeThrough: false);

        var (primary, _) = registry.AllocateForDisplay("P", null, Monitor(), primary: true);

        Assert.Null(primary.Backdrop);
        Assert.Null(primary.ClickThrough);
    }

    [Fact]
    public void Patch_takes_click_through_grid_slots_and_clears_the_import()
    {
        var (registry, store) = Build();
        var (r, _) = registry.AllocateForDisplay("D1", null, Monitor());
        store.Update(s => s.PanelDevices[r.Id].DesktopImport = new DesktopImportDto());

        registry.Patch(r.Id, new PanelDevicePatch { ClickThrough = true, GridSlots = 14, ClearDesktopImport = true });

        var stored = store.Load().PanelDevices[r.Id];
        Assert.True(stored.ClickThrough);
        Assert.Equal(14, stored.GridSlots);
        Assert.Null(stored.DesktopImport);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Patch_with_zero_or_less_clears_the_grid_override(int slots)
    {
        var (registry, store) = Build();
        var (r, _) = registry.AllocateForDisplay("D1", null, Monitor());
        registry.Patch(r.Id, new PanelDevicePatch { GridSlots = 8 });

        var clone = registry.Patch(r.Id, new PanelDevicePatch { GridSlots = slots })!;

        Assert.Null(store.Load().PanelDevices[r.Id].GridSlots);
        Assert.Null(clone.GridSlots);
    }

    [Fact]
    public void A_stored_non_positive_grid_override_never_reaches_a_response()
    {
        var (registry, store) = Build();
        store.Update(s => s.PanelDevices["r"] = new PanelDeviceRecord { Id = "r", DisplayId = "D1", GridSlots = 0 });

        Assert.Null(registry.FindByDisplayId("D1")!.GridSlots);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(66)]
    public void Patch_ignores_an_invalid_grid_slot_count(int slots)
    {
        var (registry, store) = Build();
        var (r, _) = registry.AllocateForDisplay("D1", null, Monitor());
        registry.Patch(r.Id, new PanelDevicePatch { GridSlots = 8 });

        registry.Patch(r.Id, new PanelDevicePatch { GridSlots = slots });

        Assert.Equal(8, store.Load().PanelDevices[r.Id].GridSlots);
    }

    [Fact]
    public void Patch_click_through_needs_a_display_bound_record()
    {
        var (registry, store) = Build();
        var q = registry.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Q60 });

        registry.Patch(q.Id, new PanelDevicePatch { ClickThrough = true });

        Assert.Null(store.Load().PanelDevices[q.Id].ClickThrough);
    }

    [Fact]
    public void Grid_slots_travel_with_presets_and_clear_on_reset_but_click_through_does_not_travel()
    {
        var (registry, store) = Build();
        var (r, _) = registry.AllocateForDisplay("D1", null, Monitor());
        registry.Patch(r.Id, new PanelDevicePatch { GridSlots = 10, ClickThrough = true });
        var preset = new PanelPreset();

        PanelPresets.Copy(store.Load().PanelDevices[r.Id], preset);

        Assert.Equal(10, preset.GridSlots);
        var target = new PanelDeviceRecord();
        PanelPresets.Copy(preset, target);
        Assert.Equal(10, target.GridSlots);

        registry.ResetToDefaults(r.Id);
        Assert.Null(store.Load().PanelDevices[r.Id].GridSlots);
        Assert.True(store.Load().PanelDevices[r.Id].ClickThrough);

        registry.ResetHardwareSettings(r.Id);
        Assert.Null(store.Load().PanelDevices[r.Id].ClickThrough);
    }

    [Fact]
    public void See_through_host_covers_windows_and_macos()
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), registry.SeeThroughHost);
    }

    [Fact]
    public void Assignments_signature_covers_backdrop_click_through_and_hosting()
    {
        var s = new NexusSettings();
        s.PanelDevices["a"] = new PanelDeviceRecord { Id = "a", DisplayId = "D1", Backdrop = "desktop" };
        var baseline = OverlayHostBootstrap.AssignmentsSignature(s);

        s.PanelDevices["a"].Backdrop = "wallpaper";
        var backdrop = OverlayHostBootstrap.AssignmentsSignature(s);
        s.PanelDevices["a"].ClickThrough = true;
        var clickThrough = OverlayHostBootstrap.AssignmentsSignature(s);
        s.PanelDevices["a"].Hosted = false;
        var hosting = OverlayHostBootstrap.AssignmentsSignature(s);

        Assert.Equal(4, new[] { baseline, backdrop, clickThrough, hosting }.Distinct().Count());
        Assert.Equal("", hosting);
    }

    [Fact]
    public void Supervisor_wants_a_host_for_a_hosted_record_but_not_for_a_migrated_overlay()
    {
        var s = new NexusSettings();
        s.Panel.AutoLaunch = false;
        s.Overlay.Enabled = true;
        s.Overlay.Layout.Add(new OverlayWidgetDto { Id = "w" });
        Assert.True(OverlaySupervisor.Desired(s, false, false));

        s.Overlay.MigratedToPanels = true;
        Assert.False(OverlaySupervisor.Desired(s, false, false));

        s.PanelDevices["a"] = new PanelDeviceRecord { Id = "a", DisplayId = "D1", Hosted = true, Enabled = false };
        Assert.True(OverlaySupervisor.Desired(s, false, false));
        s.PanelDevices["a"].Hosted = false;
        Assert.False(OverlaySupervisor.Desired(s, false, false));
    }

    [Fact]
    public void New_fields_round_trip_in_both_json_contexts()
    {
        var record = new PanelDeviceRecord
        {
            Id = "r",
            DisplayId = "D1",
            ClickThrough = true,
            GridSlots = 12,
            Hosted = true,
            Enabled = false,
            Layout = new PanelLayoutDto
            {
                Pages = { new PanelPageDto { Id = "p", Widgets = { new PanelWidgetDto { Id = "w", Type = "clock", Locked = true } } } },
            },
            DesktopImport = new DesktopImportDto
            {
                CellPx = 117.82,
                CellUnits = "css",
                Merge = true,
                Widgets = { new OverlayWidgetDto { Id = "o", Type = "app:x", Locked = true, Col = 1.25 } },
            },
        };

        foreach (var json in new[]
        {
            JsonSerializer.Serialize(record, AppJsonContext.Default.PanelDeviceRecord),
            JsonSerializer.Serialize(record, PersistenceJsonContext.Default.PanelDeviceRecord),
        })
        {
            var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelDeviceRecord)!;
            Assert.True(back.ClickThrough);
            Assert.Equal(12, back.GridSlots);
            Assert.True(back.Hosted);
            Assert.True(back.Layout!.Pages[0].Widgets[0].Locked);
            Assert.Equal("css", back.DesktopImport!.CellUnits);
            Assert.True(back.DesktopImport.Merge);
            Assert.Equal(1.25, back.DesktopImport.Widgets[0].Col);
            Assert.Contains("\"desktopImport\"", json);
        }
        Assert.DoesNotContain("locked", JsonSerializer.Serialize(new PanelWidgetDto { Id = "w" }, AppJsonContext.Default.PanelWidgetDto));

        var patch = JsonSerializer.Deserialize("{\"clickThrough\":true,\"gridSlots\":8,\"clearDesktopImport\":true}", AppJsonContext.Default.PanelDevicePatch)!;
        Assert.True(patch.ClickThrough);
        Assert.Equal(8, patch.GridSlots);
        Assert.True(patch.ClearDesktopImport);

        var dto = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new DisplayAssignmentDto { ClickThrough = true, IsPrimary = true }, AppJsonContext.Default.DisplayAssignmentDto),
            AppJsonContext.Default.DisplayAssignmentDto)!;
        Assert.True(dto.ClickThrough);
        Assert.True(dto.IsPrimary);

        var settings = new NexusSettings();
        settings.Overlay.MigratedToPanels = true;
        settings.Overlay.MigratedRecordId = "r";
        var back2 = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings),
            PersistenceJsonContext.Default.NexusSettings)!;
        Assert.True(back2.Overlay.MigratedToPanels);
        Assert.Equal("r", back2.Overlay.MigratedRecordId);
    }

    private sealed class FakeProvider : IDisplayTopologyProvider
    {
        public int Enumerations;
        public bool PositionsAvailable => true;
        public IReadOnlyList<RawDisplayInfo>? Enumerate()
        {
            Enumerations++;
            return Displays();
        }

        private static List<RawDisplayInfo> Displays() => new List<RawDisplayInfo>
        {
            new() { Id = "A", IsPrimary = false },
            new() { Id = "B", IsPrimary = true },
        };
    }

    [Fact]
    public void Topology_service_names_the_primary_display()
    {
        var store = new InMemoryConfigStore();
        var provider = new FakeProvider();
        var service = new DisplayTopologyService(provider, new PanelDeviceRegistry(store), store);

        Assert.False(service.IsPrimaryDisplay("B"));
        Assert.Equal(0, provider.Enumerations);

        service.GetTopology();
        Assert.True(service.IsPrimaryDisplay("B"));
        Assert.False(service.IsPrimaryDisplay("A"));
        Assert.False(service.IsPrimaryDisplay(""));
        Assert.Equal(1, provider.Enumerations);
    }
}
