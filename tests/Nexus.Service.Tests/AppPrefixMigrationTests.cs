using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests;

public class AppPrefixMigrationTests
{
    private static PanelLayoutDto Layout(params string[] types)
    {
        var page = new PanelPageDto { Id = "p1" };
        foreach (var t in types)
        {
            page.Widgets.Add(new PanelWidgetDto { Id = Guid.NewGuid().ToString("N"), Type = t });
        }
        return new PanelLayoutDto { Pages = new List<PanelPageDto> { page } };
    }

    [Fact]
    public void Rewrites_legacy_prefix_in_pinned_sidebar_tail()
    {
        var doc = new NexusSettings();
        doc.Ui.PinnedSidebarApps = new List<string> { "monitoring", "marketplace:com.ibuypower.control" };
        AppPrefixMigration.Apply(doc);
        Assert.Equal(new[] { "monitoring", "app:com.ibuypower.control" }, doc.Ui.PinnedSidebarApps);
    }

    [Fact]
    public void Never_sent_pinned_tail_stays_null_and_is_omitted_from_json()
    {
        var doc = new NexusSettings();
        AppPrefixMigration.Apply(doc);
        Assert.Null(doc.Ui.PinnedSidebarApps);
        var json = JsonSerializer.Serialize(doc, PersistenceJsonContext.Default.NexusSettings);
        Assert.DoesNotContain("pinnedSidebarApps", json);
    }

    [Fact]
    public void Rewrites_legacy_prefix_across_every_carrier()
    {
        var doc = new NexusSettings();
        doc.Panel.DashboardLayout = Layout("clock", "marketplace:com.ibuypower.control");
        doc.Panel.Layouts = new PanelLayoutsDefaults();
        doc.Panel.Layouts.Y70.Widgets.Add(new PanelLayoutWidget { Type = "marketplace:com.example.widget" });
        doc.PanelDevices["dev1"] = new PanelDeviceRecord { Layout = Layout("marketplace:com.ibuypower.control") };

        AppPrefixMigration.Apply(doc);

        Assert.Equal("clock", doc.Panel.DashboardLayout.Pages[0].Widgets[0].Type);
        Assert.Equal("app:com.ibuypower.control", doc.Panel.DashboardLayout.Pages[0].Widgets[1].Type);
        Assert.Equal("app:com.example.widget", doc.Panel.Layouts.Y70.Widgets[0].Type);
        Assert.Equal("app:com.ibuypower.control", doc.PanelDevices["dev1"].Layout!.Pages[0].Widgets[0].Type);
    }

    [Fact]
    public void Is_idempotent_and_leaves_app_prefixed_types_alone()
    {
        var doc = new NexusSettings();
        doc.Panel.DashboardLayout = Layout("app:com.ibuypower.control", "marketplace:x");
        AppPrefixMigration.Apply(doc);
        AppPrefixMigration.Apply(doc);
        Assert.Equal("app:com.ibuypower.control", doc.Panel.DashboardLayout.Pages[0].Widgets[0].Type);
        Assert.Equal("app:x", doc.Panel.DashboardLayout.Pages[0].Widgets[1].Type);
    }

    [Fact]
    public void Tolerates_null_carriers()
    {
        var doc = new NexusSettings();
        doc.Panel.DashboardLayout = null;
        doc.Panel.Layouts = null;
        doc.PanelDevices["ghost"] = new PanelDeviceRecord { Layout = null };
        AppPrefixMigration.Apply(doc);
    }

    [Fact]
    public void Migration_v11_rewrites_persisted_dashboard_layout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-v11-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new NexusSettings { SchemaVersion = 10 };
            settings.Panel.DashboardLayout = Layout("marketplace:com.ibuypower.control");
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings));

            var loaded = new JsonConfigStore(path).Load();

            Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal("app:com.ibuypower.control", loaded.Panel.DashboardLayout!.Pages[0].Widgets[0].Type);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
