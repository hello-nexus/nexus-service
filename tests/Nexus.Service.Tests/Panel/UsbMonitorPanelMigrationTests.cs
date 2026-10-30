using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Panel;

/// <summary>
/// The settings and profile files here are written by the registry and PanelProfileData code paths at
/// the prior schema, not typed by hand. A file captured from a released install with a USB device in
/// secondary-monitor mode still needs adding.
/// </summary>
public sealed class UsbMonitorPanelMigrationTests : IDisposable
{
    private const string UsbId = "usb-record";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-usbmig-" + Guid.NewGuid().ToString("N")[..8]);

    public UsbMonitorPanelMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PanelLayoutDto Layout(string surface, params string[] widgetTypes) => new()
    {
        LayoutSchemaVersion = 2,
        Surface = surface,
        ActivePageId = "p1",
        Pages = new List<PanelPageDto>
        {
            new()
            {
                Id = "p1",
                Widgets = widgetTypes.Select((t, i) => new PanelWidgetDto { Id = $"w{i}", Type = t, Size = "2x2", Col = i * 2, Row = 0 }).ToList(),
            },
        },
    };

    private static PanelDeviceRecord Usb(bool monitor) => new()
    {
        Id = UsbId,
        DisplayName = "Lian Li Universal Screen 8.8",
        SecondaryMonitor = monitor,
        Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor, Family = "lianli-us88", SupportsSecondaryMonitor = true, CssWidth = 480, CssHeight = 1920 },
        Layout = Layout("monitor", "clock"),
        BackgroundColor = "#101010",
    };

    private static PanelDeviceRecord Virtual() => new()
    {
        Id = "virtual-record",
        DisplayName = "NXS 0001",
        DisplayId = @"NXS0001-1",
        DisplayFingerprint = new DisplayFingerprint { Manufacturer = "NXS", Product = "0001" },
        LastSeenAt = 5,
        Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor, CssWidth = 480, CssHeight = 1920 },
        Layout = Layout("monitor", "performance", "media", "weather"),
        BackgroundColor = "#202a44",
        Backdrop = "desktop",
        WidgetSize = "medium",
    };

    private string WriteSettings(NexusSettings settings, string name = "settings.json")
    {
        settings.SchemaVersion = 28;
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings));
        return path;
    }

    private static NexusSettings Prior(bool monitor)
    {
        var s = new NexusSettings();
        s.PanelDevices[UsbId] = Usb(monitor);
        s.PanelDevices["virtual-record"] = Virtual();
        return s;
    }

    [Fact]
    public void Monitor_on_gives_the_device_the_virtual_panels_look()
    {
        using var store = new JsonConfigStore(WriteSettings(Prior(monitor: true)));

        var s = store.Load();

        var usb = s.PanelDevices[UsbId];
        Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Equal(new[] { "performance", "media", "weather" }, usb.Layout!.Pages.Single().Widgets.Select(w => w.Type));
        Assert.Equal("#202a44", usb.BackgroundColor);
        Assert.Equal("medium", usb.WidgetSize);
        Assert.Null(usb.Backdrop);
        Assert.Equal(PanelSurfaces.Monitor, usb.Layout.Surface);
        Assert.True(usb.SecondaryMonitor);
        Assert.Equal(480, usb.Capabilities!.CssWidth);
        var untouched = s.PanelDevices["virtual-record"];
        Assert.Equal(3, untouched.Layout!.Pages.Single().Widgets.Count);
        var pair = Assert.Single(s.UsbMonitorPanelPairs!);
        Assert.Equal((UsbId, "virtual-record"), (pair.UsbId, pair.VirtualId));
    }

    [Fact]
    public void Monitor_off_leaves_the_device_layout_alone()
    {
        using var store = new JsonConfigStore(WriteSettings(Prior(monitor: false)));

        var s = store.Load();

        Assert.Equal(new[] { "clock" }, s.PanelDevices[UsbId].Layout!.Pages.Single().Widgets.Select(w => w.Type));
        Assert.Equal("#101010", s.PanelDevices[UsbId].BackgroundColor);
        Assert.Null(s.UsbMonitorPanelPairs);
    }

    [Fact]
    public void A_virtual_record_without_a_layout_changes_nothing()
    {
        var prior = Prior(monitor: true);
        prior.PanelDevices["virtual-record"].Layout = null;
        using var store = new JsonConfigStore(WriteSettings(prior));

        var s = store.Load();

        Assert.Equal(new[] { "clock" }, s.PanelDevices[UsbId].Layout!.Pages.Single().Widgets.Select(w => w.Type));
    }

    [Fact]
    public void A_turned_off_virtual_panel_does_not_overwrite_the_device_look()
    {
        var prior = Prior(monitor: true);
        prior.PanelDevices["virtual-record"].Hosted = false;
        using var store = new JsonConfigStore(WriteSettings(prior));

        var s = store.Load();

        Assert.Equal(new[] { "clock" }, s.PanelDevices[UsbId].Layout!.Pages.Single().Widgets.Select(w => w.Type));
    }

    [Fact]
    public void Only_the_device_last_seen_with_the_monitor_on_receives_the_look()
    {
        var prior = Prior(monitor: true);
        prior.PanelDevices[UsbId].LastSeenAt = 100;
        var stale = Usb(monitor: true);
        stale.Id = "stale-usb";
        stale.LastSeenAt = 1;
        prior.PanelDevices["stale-usb"] = stale;
        using var store = new JsonConfigStore(WriteSettings(prior));

        var s = store.Load();

        Assert.Equal(3, s.PanelDevices[UsbId].Layout!.Pages.Single().Widgets.Count);
        Assert.Single(s.PanelDevices["stale-usb"].Layout!.Pages.Single().Widgets);
        Assert.Equal(UsbId, Assert.Single(s.UsbMonitorPanelPairs!).UsbId);
    }

    [Fact]
    public void Copied_presets_lose_the_desktop_backdrop_and_take_the_device_surface()
    {
        var prior = Prior(monitor: true);
        prior.PanelDevices[UsbId].Capabilities!.Surface = PanelSurfaces.LcdSquare;
        prior.PanelDevices["virtual-record"].Presets = new List<PanelPreset>
        {
            new() { Id = "p", Name = "Night", Backdrop = "desktop", Layout = Layout("monitor", "clock") },
        };
        using var store = new JsonConfigStore(WriteSettings(prior));

        var preset = Assert.Single(store.Load().PanelDevices[UsbId].Presets!);

        Assert.Null(preset.Backdrop);
        Assert.Equal(PanelSurfaces.LcdSquare, preset.Layout!.Surface);
    }

    [Fact]
    public void The_file_the_device_look_was_replaced_from_is_kept()
    {
        var path = WriteSettings(Prior(monitor: true));
        var original = File.ReadAllText(path);
        using var store = new JsonConfigStore(path);

        store.Load();

        Assert.Equal(original, File.ReadAllText(path + ".pre-v29"));
    }

    [Fact]
    public void An_existing_backup_is_not_overwritten_by_a_later_migration_pass()
    {
        var path = WriteSettings(Prior(monitor: true));
        File.WriteAllText(path + ".pre-v29", "original");
        using var store = new JsonConfigStore(path);

        store.Load();

        Assert.Equal("original", File.ReadAllText(path + ".pre-v29"));
    }

    [Fact]
    public void No_backup_is_written_when_the_migration_changes_nothing()
    {
        var path = WriteSettings(Prior(monitor: false));
        using var store = new JsonConfigStore(path);

        store.Load();

        Assert.False(File.Exists(path + ".pre-v29"));
    }

    [Fact]
    public void A_later_edit_survives_a_reload()
    {
        var path = WriteSettings(Prior(monitor: true));
        using (var first = new JsonConfigStore(path))
        {
            first.Update(s => s.PanelDevices[UsbId].Layout = Layout("monitor", "clock", "cpu"));
            first.FlushNow();
        }

        using var second = new JsonConfigStore(path);

        Assert.Equal(new[] { "clock", "cpu" }, second.Load().PanelDevices[UsbId].Layout!.Pages.Single().Widgets.Select(w => w.Type));
    }

    [Fact]
    public void Every_profile_file_is_migrated_once_and_later_edits_are_kept()
    {
        var live = Prior(monitor: true);
        // A profile file as PanelProfileData writes it: stripped records under the panels marker.
        NexusSettings Profile(string suffix)
        {
            var file = new NexusSettings();
            PanelProfileData.WriteTo(file, live);
            file.PanelDevices[UsbId].BackgroundColor = "#" + suffix;
            return file;
        }
        var profiles = new[] { Profile("111111"), Profile("222222") };
        using var store = new JsonConfigStore(WriteSettings(live));
        var pairs = store.Load().UsbMonitorPanelPairs;
        var round = profiles.Select(p => JsonSerializer.Deserialize(
            JsonSerializer.Serialize(p, PersistenceJsonContext.Default.NexusSettings), PersistenceJsonContext.Default.NexusSettings)!).ToArray();
        foreach (var p in round) p.SchemaVersion = 28;

        foreach (var p in round)
        {
            Assert.True(UsbMonitorPanelMigration.ApplyProfile(p, pairs));
            Assert.Equal(3, p.PanelDevices[UsbId].Layout!.Pages.Single().Widgets.Count);
            Assert.Equal("#202a44", p.PanelDevices[UsbId].BackgroundColor);
            Assert.Equal(UsbMonitorPanelMigration.SchemaVersion, p.SchemaVersion);
        }

        round[0].PanelDevices[UsbId].Layout = Layout("monitor", "clock");
        Assert.False(UsbMonitorPanelMigration.ApplyProfile(round[0], pairs));
        Assert.Single(round[0].PanelDevices[UsbId].Layout!.Pages.Single().Widgets);
    }

    [Fact]
    public void A_profile_without_pairs_is_only_stamped()
    {
        var file = new NexusSettings { SchemaVersion = 28 };
        file.PanelDevices[UsbId] = Usb(monitor: false);

        Assert.True(UsbMonitorPanelMigration.ApplyProfile(file, null));

        Assert.Single(file.PanelDevices[UsbId].Layout!.Pages.Single().Widgets);
        Assert.Equal(UsbMonitorPanelMigration.SchemaVersion, file.SchemaVersion);
    }
}
