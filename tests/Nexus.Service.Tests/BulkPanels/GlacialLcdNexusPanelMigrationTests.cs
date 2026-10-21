using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public sealed class GlacialLcdNexusPanelMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glacial-mig-" + Guid.NewGuid().ToString("N"));
    private readonly StreamedPanelStore _streams;
    private readonly TestableConfigStore _store;

    public GlacialLcdNexusPanelMigrationTests()
    {
        Directory.CreateDirectory(_dir);
        _streams = new StreamedPanelStore(Path.Combine(_dir, "streams.json"));
        _store = new TestableConfigStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PanelDeviceRecord Panel(string id, string family) => new()
    {
        Id = id,
        Capabilities = new PanelDeviceCapabilities { Family = family },
    };

    [Fact]
    public void A_panel_mode_install_returns_to_a_native_mode_and_loses_its_stream_record()
    {
        _store.Update(s =>
        {
            var g = s.Devices.GlacialLcd;
            g.UserEdited = true;
            g.ModeChosen = true;
            g.Mode = GlacialLcdSettings.ModePanel;
        });
        _streams.Save(new Dictionary<string, StreamedPanelRecord>
        {
            ["asus-glacial-lcd"] = new() { PanelDeviceId = "p1" },
            ["other"] = new() { PanelDeviceId = "p2" },
        });
        var panels = new Dictionary<string, PanelDeviceRecord> { ["p1"] = Panel("p1", "asus-glacial-lcd"), ["p2"] = Panel("p2", "xeneon-edge") };
        var removed = new List<string>();

        bool changed = GlacialLcdNexusPanelMigration.Run(_store, _streams, id => panels.GetValueOrDefault(id), id => { removed.Add(id); return true; });

        Assert.True(changed);
        Assert.Equal(new[] { "p1" }, removed);
        Assert.Equal(new[] { "other" }, _streams.Load().Keys);
        var g = _store.Load().Devices.GlacialLcd;
        Assert.Equal(GlacialLcdSettings.ModeMonitor, g.Mode);
        Assert.True(g.ModePending);
        Assert.False(g.BrightnessEdited);
        Assert.False(g.DisplayEdited);
    }

    [Fact]
    public void A_native_mode_install_with_a_stream_record_keeps_its_mode_and_resends_it()
    {
        _store.Update(s =>
        {
            var g = s.Devices.GlacialLcd;
            g.UserEdited = true;
            g.ModeChosen = true;
            g.Mode = GlacialLcdSettings.ModeStatic;
        });
        _streams.Save(new Dictionary<string, StreamedPanelRecord> { ["BYZL"] = new() { PanelDeviceId = "p1" } });
        var panels = new Dictionary<string, PanelDeviceRecord> { ["p1"] = Panel("p1", "asus-glacial-lcd") };

        Assert.True(GlacialLcdNexusPanelMigration.Run(_store, _streams, id => panels.GetValueOrDefault(id), _ => true));

        Assert.Empty(_streams.Load());
        var g = _store.Load().Devices.GlacialLcd;
        Assert.Equal(GlacialLcdSettings.ModeStatic, g.Mode);
        Assert.True(g.ModePending);
    }

    [Fact]
    public void A_clean_install_is_left_alone_and_a_second_run_changes_nothing()
    {
        Assert.False(GlacialLcdNexusPanelMigration.Run(_store, _streams, _ => null, _ => true));
        Assert.False(_store.Load().Devices.GlacialLcd.ModePending);

        _store.Update(s => { s.Devices.GlacialLcd.UserEdited = true; s.Devices.GlacialLcd.ModeChosen = true; s.Devices.GlacialLcd.Mode = GlacialLcdSettings.ModePanel; });
        Assert.True(GlacialLcdNexusPanelMigration.Run(_store, _streams, _ => null, _ => true));
        _store.Update(s => s.Devices.GlacialLcd.ModePending = false);
        Assert.False(GlacialLcdNexusPanelMigration.Run(_store, _streams, _ => null, _ => true));
        Assert.False(_store.Load().Devices.GlacialLcd.ModePending);
    }

    [Fact]
    public void A_failed_panel_removal_keeps_the_stream_entry_for_the_next_start_and_orphans_are_swept()
    {
        _streams.Save(new Dictionary<string, StreamedPanelRecord> { ["asus-glacial-lcd"] = new() { PanelDeviceId = "p1" } });
        _store.Update(s => s.PanelDevices["p9"] = Panel("p9", "asus-glacial-lcd"));
        var panels = new Dictionary<string, PanelDeviceRecord> { ["p1"] = Panel("p1", "asus-glacial-lcd") };
        var tried = new List<string>();

        GlacialLcdNexusPanelMigration.Run(_store, _streams, id => panels.GetValueOrDefault(id),
            id => { tried.Add(id); return id == "p1" ? throw new InvalidOperationException("x") : true; });

        Assert.Contains("p9", tried);
        Assert.Equal(new[] { "asus-glacial-lcd" }, _streams.Load().Keys);
    }
}
