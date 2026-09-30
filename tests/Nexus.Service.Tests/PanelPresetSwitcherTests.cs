using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Drives PanelPresetSwitcher.Tick on a manual clock: the per-panel
/// tracker, the activate hand-off and the restore on an unbound app.</summary>
public sealed class PanelPresetSwitcherTests : IDisposable
{
    private sealed class FakeScreenTime : IScreenTimeProvider
    {
        public string Focused = "";
        public event Action? FocusChanged { add { } remove { } }
        public FocusSession? GetCurrentSession() => Focused.Length == 0 ? null : new FocusSession { Id = "1", Name = Focused };
        public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
    }

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "nexus-panelpresets-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;
    private readonly FakeScreenTime _screenTime = new();
    private readonly ManualTime _time = new();
    private readonly PanelPresetSwitcher _switcher;

    public PanelPresetSwitcherTests()
    {
        _store = new JsonConfigStore(_path);
        _registry = new PanelDeviceRegistry(_store);
        _switcher = new PanelPresetSwitcher(_store, _screenTime, _registry, new MultiplexHub(), _time);
    }

    public void Dispose()
    {
        _switcher.Dispose();
        _store.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private (string PanelId, string Desk, string Game) Seed(bool bind)
    {
        var panel = _registry.Allocate(null, new PanelDeviceCapabilities { Surface = PanelSurfaces.Q60 }).Id;
        _registry.Patch(panel, new PanelDevicePatch { ThemeMode = "dark" });
        var desk = _registry.CreatePreset(panel, "Desk", out _)!.ActiveId!;
        var game = _registry.CreatePreset(panel, "Game", out _)!.ActiveId!;
        _registry.Patch(panel, new PanelDevicePatch { ThemeMode = "light" });
        _registry.ActivatePreset(panel, desk);
        if (bind)
            _registry.SetPresetApps(panel, game, new() { new PresetAppBinding { Id = "proc:chrome", Name = "chrome", ProcessName = "chrome" } }, out _);
        return (panel, desk, game);
    }

    /// <summary>Focuses an app and ticks on both sides of the dwell, as the
    /// prompt and dwell timers would.</summary>
    private void Focus(string app)
    {
        _screenTime.Focused = app;
        _switcher.Tick();
        _time.Ms += (long)AppPresetFocusTracker.Dwell.TotalMilliseconds + 100;
        _switcher.Tick();
    }

    [Fact]
    public void Focusing_a_bound_app_loads_its_preset_then_an_unbound_app_restores()
    {
        var (panel, desk, game) = Seed(bind: true);

        Focus("chrome");
        Assert.Equal(game, _registry.ListPresets(panel)!.ActiveId);
        Assert.Equal("light", _registry.Get(panel)!.ThemeMode);

        Focus("notepad");
        Assert.Equal(desk, _registry.ListPresets(panel)!.ActiveId);
        Assert.Equal("dark", _registry.Get(panel)!.ThemeMode);
    }

    [Fact]
    public void No_bindings_never_switches()
    {
        var (panel, desk, _) = Seed(bind: false);

        Focus("chrome");

        Assert.Equal(desk, _registry.ListPresets(panel)!.ActiveId);
    }

    [Fact]
    public void A_manual_pick_while_the_app_holds_focus_is_not_overridden()
    {
        var (panel, desk, _) = Seed(bind: true);
        Focus("chrome");

        _registry.ActivatePreset(panel, desk);
        _switcher.Tick();

        Assert.Equal(desk, _registry.ListPresets(panel)!.ActiveId);
    }

    [Fact]
    public void With_no_preset_loaded_the_live_personalization_is_never_replaced()
    {
        var (panel, desk, _) = Seed(bind: true);
        _registry.DeletePreset(panel, desk);

        Focus("chrome");

        Assert.Null(_registry.ListPresets(panel)!.ActiveId);
        Assert.Equal("dark", _registry.Get(panel)!.ThemeMode);
    }
}
