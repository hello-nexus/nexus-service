using System;
using System.IO;
using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Single-instance surfaces (Y70, Q-series) must reuse one record across
/// (re)connects instead of accreting a fresh one - the Q-series OEM WebView
/// drops its cached deviceId, which otherwise mints a record every connect and
/// orphans the user's theme/layout.
/// </summary>
public sealed class PanelDeviceRegistryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "nexus-paneldev-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;

    public PanelDeviceRegistryTests()
    {
        _store = new JsonConfigStore(_path);
        _registry = new PanelDeviceRegistry(_store) { SeeThroughHost = false };
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private static PanelDeviceCapabilities Caps(string surface) => new() { Surface = surface };

    [Theory]
    [InlineData(PanelSurfaces.Q60)]
    [InlineData(PanelSurfaces.Y70)]
    public void Allocate_SingleInstanceSurface_ReusesOneRecord(string surface)
    {
        var first = _registry.Allocate(null, Caps(surface));
        var second = _registry.Allocate(null, Caps(surface));

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_registry.List());
    }

    [Fact]
    public void Allocate_Phone_DoesNotDedup()
    {
        var first = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        var second = _registry.Allocate(null, Caps(PanelSurfaces.Phone));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _registry.List().Count);
    }

    [Fact]
    public void Allocate_Reuse_PreservesThemeAndName()
    {
        var first = _registry.Allocate("My Q60", Caps(PanelSurfaces.Q60));
        _registry.Patch(first.Id, new PanelDevicePatch { BackgroundColor = "#78350f" });

        var reused = _registry.Allocate(null, Caps(PanelSurfaces.Q60));

        Assert.Equal(first.Id, reused.Id);
        Assert.Equal("My Q60", reused.DisplayName);
        Assert.Equal("#78350f", reused.BackgroundColor);
    }

    [Fact]
    public void Allocate_SingleInstance_DoesNotHijackDisplayBoundRecord()
    {
        var (display, _) = _registry.AllocateForDisplay("DISP-1", "Monitor", Caps(PanelSurfaces.Q60));

        var allocated = _registry.Allocate(null, Caps(PanelSurfaces.Q60));

        Assert.NotEqual(display.Id, allocated.Id);
        Assert.True(string.IsNullOrEmpty(allocated.DisplayId));
        Assert.Equal(2, _registry.List().Count);
    }

    [Fact]
    public void Patch_WidgetPadding_RoundTripsThroughGet()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { WidgetPadding = 80 });
        var fetched = _registry.Get(record.Id);

        Assert.Equal(80, patched!.WidgetPadding);
        Assert.Equal(80, fetched!.WidgetPadding);
    }

    [Fact]
    public void Patch_OmittedWidgetPadding_DoesNotClobberStoredValue()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        _registry.Patch(record.Id, new PanelDevicePatch { WidgetPadding = 0 });

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });

        Assert.Equal(0, patched!.WidgetPadding);
        Assert.Equal("Renamed", patched.DisplayName);
    }

    [Fact]
    public void Patch_TextColor_RoundTripsAndSurvivesOmission()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        Assert.Null(record.TextColorMode);
        Assert.Null(record.TextColor);

        _registry.Patch(record.Id, new PanelDevicePatch { TextColorMode = "custom", TextColor = "#ff8800" });
        var patched = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });

        Assert.Equal("custom", patched!.TextColorMode);
        Assert.Equal("#ff8800", _registry.Get(record.Id)!.TextColor);
    }

    /// <summary>
    /// The service stores null until explicitly patched, same as WidgetOpacity/
    /// WidgetLabels; the default percent is applied client-side.
    /// </summary>
    [Fact]
    public void Allocate_WidgetPadding_AbsentIsNullNotServerDefaulted()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));

        Assert.Null(record.WidgetPadding);
    }

    [Fact]
    public void Patch_Backdrop_RoundTripsThroughGet()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        Assert.Null(record.Backdrop);

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { Backdrop = "desktop" });
        var fetched = _registry.Get(record.Id);

        Assert.Equal("desktop", patched!.Backdrop);
        Assert.Equal("desktop", fetched!.Backdrop);
    }

    [Fact]
    public void Patch_Backdrop_NormalizesCaseAndSurroundingSpace()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { Backdrop = "  Wallpaper " });

        Assert.Equal("wallpaper", patched!.Backdrop);
    }

    // An unrecognised value must not clear the stored mode: the client would
    // silently fall back to its per-surface default on the next read.
    [Fact]
    public void Patch_UnknownBackdrop_LeavesTheStoredValue()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        _registry.Patch(record.Id, new PanelDevicePatch { Backdrop = "desktop" });

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { Backdrop = "nonsense" });

        Assert.Equal("desktop", patched!.Backdrop);
    }

    [Fact]
    public void Patch_OmittedBackdrop_DoesNotClobberStoredValue()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        _registry.Patch(record.Id, new PanelDevicePatch { Backdrop = "theme" });

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });

        Assert.Equal("theme", patched!.Backdrop);
    }

    // The Y70 kiosk opens from hardware detection, so the host reads its
    // backdrop off this lookup rather than a display assignment.
    [Fact]
    public void GetY70Backdrop_ReturnsTheY70RecordsBackdrop()
    {
        Assert.Equal("", _registry.GetY70Backdrop());

        var monitor = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        _registry.Patch(monitor.Id, new PanelDevicePatch { Backdrop = "desktop" });
        Assert.Equal("", _registry.GetY70Backdrop());

        var y70 = _registry.Allocate(null, Caps(PanelSurfaces.Y70));
        _registry.Patch(y70.Id, new PanelDevicePatch { Backdrop = "desktop" });

        Assert.Equal("desktop", _registry.GetY70Backdrop());
    }

    [Fact]
    public void UnsetBackdrop_StaysUnsetUntilLatched()
    {
        var registry = new PanelDeviceRegistry(_store) { SeeThroughHost = true };
        var (monitor, _) = registry.AllocateForDisplay("DISP-1", null, Caps(PanelSurfaces.Monitor));
        var y70 = registry.Allocate(null, Caps(PanelSurfaces.Y70));

        Assert.Null(monitor.Backdrop);
        Assert.Null(registry.Get(y70.Id)!.Backdrop);
        Assert.Equal("", registry.GetY70Backdrop());
        Assert.Equal("", Assert.Single(registry.ListAssignments()).Backdrop);
        Assert.True(registry.HasUnsetDesktopBackdrop());
    }

    [Fact]
    public void LatchDesktopBackdrop_StoresDesktopOnce()
    {
        var registry = new PanelDeviceRegistry(_store) { SeeThroughHost = true };
        var (monitor, _) = registry.AllocateForDisplay("DISP-1", null, Caps(PanelSurfaces.Monitor));
        var y70 = registry.Allocate(null, Caps(PanelSurfaces.Y70));

        Assert.Equal(new[] { monitor.Id, y70.Id }.OrderBy(x => x), registry.LatchDesktopBackdrop().OrderBy(x => x));

        Assert.Equal("desktop", _store.Load().PanelDevices[monitor.Id].Backdrop);
        Assert.Equal("desktop", _store.Load().PanelDevices[y70.Id].Backdrop);
        Assert.Equal("desktop", registry.GetY70Backdrop());
        Assert.Equal("desktop", Assert.Single(registry.ListAssignments()).Backdrop);
        Assert.False(registry.HasUnsetDesktopBackdrop());
        Assert.Empty(registry.LatchDesktopBackdrop());
    }

    [Fact]
    public void LatchDesktopBackdrop_CoversPresetsSavedWhileUnset()
    {
        var registry = new PanelDeviceRegistry(_store) { SeeThroughHost = true };
        var (monitor, _) = registry.AllocateForDisplay("DISP-1", null, Caps(PanelSurfaces.Monitor));
        var saved = registry.CreatePreset(monitor.Id, "Desk", out _)!.Presets.Single();
        registry.CreatePreset(monitor.Id, "Game", out _);

        registry.LatchDesktopBackdrop();
        registry.ActivatePreset(monitor.Id, saved.Id);

        Assert.Equal("desktop", registry.Get(monitor.Id)!.Backdrop);
    }

    [Fact]
    public void LatchDesktopBackdrop_ExplicitChoiceWins_ResetUnsets()
    {
        var registry = new PanelDeviceRegistry(_store) { SeeThroughHost = true };
        var (monitor, _) = registry.AllocateForDisplay("DISP-1", null, Caps(PanelSurfaces.Monitor));
        registry.Patch(monitor.Id, new PanelDevicePatch { Backdrop = "wallpaper" });

        Assert.Empty(registry.LatchDesktopBackdrop());
        Assert.Equal("wallpaper", registry.Get(monitor.Id)!.Backdrop);

        Assert.Null(registry.ResetToDefaults(monitor.Id)!.Backdrop);
        Assert.True(registry.HasUnsetDesktopBackdrop());
    }

    [Fact]
    public void LatchDesktopBackdrop_SkipsY70InCompatibilityMode()
    {
        _store.Update(s => s.Y70.CompatibilityRendering = true);
        var registry = new PanelDeviceRegistry(_store) { SeeThroughHost = true };
        var y70 = registry.Allocate(null, Caps(PanelSurfaces.Y70));
        var (monitor, _) = registry.AllocateForDisplay("DISP-1", null, Caps(PanelSurfaces.Monitor));

        Assert.Equal(new[] { monitor.Id }, registry.LatchDesktopBackdrop());
        Assert.Null(registry.Get(y70.Id)!.Backdrop);
        Assert.Equal("", registry.GetY70Backdrop());
    }

    [Theory]
    [InlineData(PanelSurfaces.Phone)]
    [InlineData(PanelSurfaces.Q60)]
    [InlineData(PanelSurfaces.Monitor)]
    public void LatchDesktopBackdrop_SkipsSurfacesWithoutADesktopBehind(string surface)
    {
        var registry = new PanelDeviceRegistry(_store) { SeeThroughHost = true };
        var record = registry.Allocate(null, Caps(surface));

        Assert.False(registry.HasUnsetDesktopBackdrop());
        Assert.Empty(registry.LatchDesktopBackdrop());
        Assert.Null(registry.Get(record.Id)!.Backdrop);
    }

    [Fact]
    public void LatchDesktopBackdrop_DoesNothingOnOpaqueKioskHost()
    {
        var (monitor, _) = _registry.AllocateForDisplay("DISP-1", null, Caps(PanelSurfaces.Monitor));
        _registry.Allocate(null, Caps(PanelSurfaces.Y70));

        Assert.False(_registry.HasUnsetDesktopBackdrop());
        Assert.Empty(_registry.LatchDesktopBackdrop());
        Assert.Null(_registry.Get(monitor.Id)!.Backdrop);
    }

    [Fact]
    public void Patch_BackgroundFrostLevel_RoundTripsAndSurvivesUnrelatedPatch()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        Assert.Null(record.BackgroundFrostLevel);

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { BackgroundFrostLevel = 75 });
        Assert.Equal(75, patched!.BackgroundFrostLevel);
        Assert.Equal(75, _registry.Get(record.Id)!.BackgroundFrostLevel);

        var renamed = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });
        Assert.Equal(75, renamed!.BackgroundFrostLevel);

        // 0 is a real value (frost off), not "no change".
        var off = _registry.Patch(record.Id, new PanelDevicePatch { BackgroundFrostLevel = 0 });
        Assert.Equal(0, off!.BackgroundFrostLevel);
    }

    [Fact]
    public void Patch_ImmersiveBackdrop_RoundTripsAndSurvivesUnrelatedPatch()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        Assert.Null(record.ImmersiveBackdrop);

        var off = _registry.Patch(record.Id, new PanelDevicePatch { ImmersiveBackdrop = false });
        Assert.False(off!.ImmersiveBackdrop);
        Assert.False(_registry.Get(record.Id)!.ImmersiveBackdrop);

        var renamed = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });
        Assert.False(renamed!.ImmersiveBackdrop);

        var on = _registry.Patch(record.Id, new PanelDevicePatch { ImmersiveBackdrop = true });
        Assert.True(on!.ImmersiveBackdrop);
    }

    [Fact]
    public void Patch_GaugeGradient_RoundTripsCopiesAndResets()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        Assert.Null(record.GaugeGradient);

        var stops = new List<PanelGaugeGradientStop>
        {
            new() { At = 0, Color = "#2563eb" },
            new() { At = 0.7, Color = "#f59e0b" },
            new() { At = 1, Color = "#ef4444" },
        };
        var patched = _registry.Patch(record.Id, new PanelDevicePatch { GaugeGradient = stops });
        Assert.Equal(3, patched!.GaugeGradient!.Count);
        Assert.Equal(0.7, patched.GaugeGradient[1].At);
        Assert.Equal("#f59e0b", patched.GaugeGradient[1].Color);

        // The record holds its own copy: mutating the caller's list changes nothing.
        stops[1].Color = "#000000";
        Assert.Equal("#f59e0b", _registry.Get(record.Id)!.GaugeGradient![1].Color);

        var renamed = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });
        Assert.Equal(3, renamed!.GaugeGradient!.Count);

        var reset = _registry.ResetToDefaults(record.Id);
        Assert.Null(reset!.GaugeGradient);
    }

    [Fact]
    public void Patch_BackgroundMediaSlideshow_RoundTripsAndSurvivesUnrelatedPatch()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        Assert.Null(record.BackgroundMediaSlideshow);
        Assert.Null(record.BackgroundMediaInterval);

        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            BackgroundMediaSlideshow = true,
            BackgroundMediaInterval = 60,
            BackgroundMediaShuffle = true,
            BackgroundMediaFinishVideos = false,
        });
        Assert.True(patched!.BackgroundMediaSlideshow);
        Assert.Equal(60, patched.BackgroundMediaInterval);
        Assert.True(patched.BackgroundMediaShuffle);
        Assert.False(patched.BackgroundMediaFinishVideos);

        var renamed = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });
        Assert.True(renamed!.BackgroundMediaSlideshow);
        Assert.Equal(60, renamed.BackgroundMediaInterval);

        // false is a real value (slideshow off), not "no change".
        var off = _registry.Patch(record.Id, new PanelDevicePatch { BackgroundMediaSlideshow = false });
        Assert.False(off!.BackgroundMediaSlideshow);
        Assert.Equal(60, off.BackgroundMediaInterval);

        var ordered = _registry.Patch(record.Id, new PanelDevicePatch { BackgroundMediaOrder = new List<string> { "b", "a" } });
        Assert.Equal(new[] { "b", "a" }, ordered!.BackgroundMediaOrder);
        Assert.Equal(new[] { "b", "a" }, _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Again" })!.BackgroundMediaOrder);
    }

    [Fact]
    public void ResetToDefaults_ClearsCustomizations_KeepsIdentity()
    {
        var record = _registry.Allocate("My Panel", Caps(PanelSurfaces.Phone));
        _registry.Patch(record.Id, new PanelDevicePatch
        {
            Layout = new PanelLayoutDto { Surface = PanelSurfaces.Phone },
            ThemeMode = "light",
            AccentColor = "#8b5cf6",
            BackgroundColor = "#1f0d36",
            BackgroundColorLight = "#f3e8ff",
            BackgroundMode = "shader",
            BackgroundEffect = "plasma",
            BackgroundTemplate = 2,
            BackgroundTemplates = new Dictionary<string, int> { ["plasma"] = 2 },
            BackgroundOpacity = 0.3,
            Backdrop = "desktop",
            BackgroundMediaId = "asset-1",
            BackgroundMediaType = "static",
            BackgroundMediaSlideshow = true,
            BackgroundMediaInterval = 300,
            BackgroundMediaShuffle = true,
            BackgroundMediaFinishVideos = false,
            BackgroundMediaOrder = new List<string> { "asset-2", "asset-1" },
            BackgroundFrostLevel = 100,
            ImmersiveBackdrop = false,
            WidgetOpacity = 0.7,
            WidgetLabels = true,
            WidgetPadding = 25,
            ThemeSyncWithDesktop = false,
            AccentSyncWithDesktop = false,
        });

        var reset = _registry.ResetToDefaults(record.Id);

        Assert.NotNull(reset);
        Assert.Equal(record.Id, reset!.Id);
        Assert.Equal("My Panel", reset.DisplayName);
        Assert.Equal(record.FirstSeenAt, reset.FirstSeenAt);
        Assert.NotNull(reset.Capabilities);
        Assert.Null(reset.Layout);
        Assert.Null(reset.ThemeMode);
        Assert.Null(reset.AccentColor);
        Assert.Null(reset.BackgroundColor);
        Assert.Null(reset.BackgroundColorLight);
        Assert.Null(reset.BackgroundMode);
        Assert.Null(reset.BackgroundEffect);
        Assert.Null(reset.BackgroundTemplate);
        Assert.Null(reset.BackgroundTemplates);
        Assert.Null(reset.BackgroundOpacity);
        Assert.Null(reset.Backdrop);
        Assert.Null(reset.BackgroundMediaId);
        Assert.Null(reset.BackgroundMediaType);
        Assert.Null(reset.BackgroundMediaSlideshow);
        Assert.Null(reset.BackgroundMediaInterval);
        Assert.Null(reset.BackgroundMediaShuffle);
        Assert.Null(reset.BackgroundMediaFinishVideos);
        Assert.Null(reset.BackgroundMediaOrder);
        Assert.Null(reset.BackgroundFrostLevel);
        Assert.Null(reset.ImmersiveBackdrop);
        Assert.Null(reset.WidgetOpacity);
        Assert.Null(reset.WidgetLabels);
        Assert.Null(reset.WidgetPadding);
        Assert.Null(reset.ThemeSyncWithDesktop);
        Assert.Null(reset.AccentSyncWithDesktop);
        Assert.Null(_registry.Get(record.Id)!.Layout);
    }

    [Fact]
    public void ResetToDefaults_DisplayBound_KeepsBindingEnabledAndMonitorSettings()
    {
        var (record, _) = _registry.AllocateForDisplay("DISPLAY-1", "Edge", Caps(PanelSurfaces.Monitor));
        _registry.Patch(record.Id, new PanelDevicePatch { ReserveMonitor = false, AutoOrient = false, KeepCursorOff = true, BackgroundFrostLevel = 25 });

        var reset = _registry.ResetToDefaults(record.Id);

        Assert.NotNull(reset);
        Assert.Equal("DISPLAY-1", reset!.DisplayId);
        Assert.NotEqual(false, reset.Enabled);
        // Monitor behavior is hardware scope; personalization keeps it.
        Assert.False(reset.ReserveMonitor);
        Assert.False(reset.AutoOrient);
        Assert.True(reset.KeepCursorOff);
        Assert.True(Assert.Single(_registry.ListAssignments()).KeepCursorOff);
        Assert.Null(reset.BackgroundFrostLevel);
    }

    [Fact]
    public void ResetHardwareSettings_ClearsMonitorBehavior_KeepsPersonalization()
    {
        var (record, _) = _registry.AllocateForDisplay("DISPLAY-1", "Edge", Caps(PanelSurfaces.Monitor));
        _registry.Patch(record.Id, new PanelDevicePatch { ReserveMonitor = false, AutoOrient = false, KeepCursorOff = true, BackgroundFrostLevel = 25 });
        _registry.UpdateXeneonEdgeSettings("DISPLAY-1", new XeneonEdgeSettingsDto { Brightness = 5 });

        var reset = _registry.ResetHardwareSettings(record.Id);

        Assert.NotNull(reset);
        Assert.Null(reset!.ReserveMonitor);
        Assert.Null(reset.AutoOrient);
        Assert.Null(reset.KeepCursorOff);
        Assert.False(Assert.Single(_registry.ListAssignments()).KeepCursorOff);
        Assert.Null(reset.XeneonEdgeSettings);
        // Personalization is the other scope; hardware reset keeps it.
        Assert.Equal(25, reset.BackgroundFrostLevel);
        Assert.Equal("DISPLAY-1", reset.DisplayId);
    }

    [Fact]
    public void HighResolution_persists_and_hardware_reset_clears_it()
    {
        var record = _registry.Allocate("Screen", Caps(PanelSurfaces.Monitor));

        Assert.True(_registry.Patch(record.Id, new PanelDevicePatch { HighResolution = true })!.HighResolution);
        Assert.True(_registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" })!.HighResolution);

        Assert.Null(_registry.ResetHardwareSettings(record.Id)!.HighResolution);
    }

    [Fact]
    public void ResetToDefaults_reseeds_a_streamed_strip_and_clears_a_display_bound_one()
    {
        var strip = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor, CssWidth = 1920, CssHeight = 480 };
        var streamed = _registry.Allocate("Strip", strip);
        var (bound, _) = _registry.AllocateForDisplay("DISPLAY-1", "Edge", strip);

        var reseeded = _registry.ResetToDefaults(streamed.Id)!.Layout;

        Assert.Contains(Assert.Single(reseeded!.Pages).Widgets, w => w.Type == "weather" && w.Size == "4x4");
        Assert.Null(_registry.ResetToDefaults(bound.Id)!.Layout);
    }

    [Fact]
    public void ResetToDefaults_UnknownId_ReturnsNull()
    {
        Assert.Null(_registry.ResetToDefaults("nope"));
        Assert.Null(_registry.ResetHardwareSettings("nope"));
    }

    /// <summary>Null until explicitly patched; the client resolves the default
    /// per surface.</summary>
    [Fact]
    public void Allocate_Backdrop_AbsentIsNullNotServerDefaulted()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));

        Assert.Null(record.Backdrop);
    }

    [Fact]
    public void UpdateXeneonEdgeSettings_PartialUpdate_DoesNotClobberOtherStoredControls()
    {
        var (display, _) = _registry.AllocateForDisplay("DISP-XENEON", "Xeneon Edge", Caps(PanelSurfaces.Monitor));
        _registry.UpdateXeneonEdgeSettings(display.DisplayId!, new XeneonEdgeSettingsDto { Brightness = 50, Red = 151 });

        _registry.UpdateXeneonEdgeSettings(display.DisplayId!, new XeneonEdgeSettingsDto { Brightness = 80 });

        var fetched = _registry.Get(display.Id);
        Assert.Equal(80, fetched!.XeneonEdgeSettings!.Brightness);
        Assert.Equal(151, fetched.XeneonEdgeSettings!.Red);
    }

    [Fact]
    public void ResolveCoverBackgroundHex_NullRecord_ReturnsEmpty()
    {
        Assert.Equal("", PanelDeviceRegistry.ResolveCoverBackgroundHex(null));
    }

    [Fact]
    public void ResolveCoverBackgroundHex_NoColoursSet_ReturnsEmpty()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));

        Assert.Equal("", PanelDeviceRegistry.ResolveCoverBackgroundHex(record));
    }

    [Fact]
    public void ResolveCoverBackgroundHex_DefaultThemeMode_PrefersTheDarkSlot()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            BackgroundColor = "#1f0d36",
            BackgroundColorLight = "#f3e8ff",
        });

        Assert.Equal("#1f0d36", PanelDeviceRegistry.ResolveCoverBackgroundHex(patched));
    }

    [Fact]
    public void ResolveCoverBackgroundHex_ExplicitLightThemeMode_PrefersTheLightSlot()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            ThemeMode = "light",
            BackgroundColor = "#1f0d36",
            BackgroundColorLight = "#f3e8ff",
        });

        Assert.Equal("#f3e8ff", PanelDeviceRegistry.ResolveCoverBackgroundHex(patched));
    }

    [Fact]
    public void ResolveCoverBackgroundHex_LightThemeModeButOnlyDarkSlotSet_FallsBackToTheDarkSlot()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            ThemeMode = "light",
            BackgroundColor = "#1f0d36",
        });

        Assert.Equal("#1f0d36", PanelDeviceRegistry.ResolveCoverBackgroundHex(patched));
    }

    /// <summary>A Y70 kiosk that maps on the desktop monitor before the compositor
    /// moves it reports that monitor's shape; the surface is identity, not a sample.</summary>
    [Theory]
    [InlineData(PanelSurfaces.Y70)]
    [InlineData(PanelSurfaces.Q60)]
    public void Patch_KeepsSingleInstanceSurface_WhenViewportSampleDisagrees(string surface)
    {
        var record = _registry.Allocate(null, Caps(surface));

        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Phone, CssWidth = 2560, CssHeight = 1440, Dpr = 1 },
        });

        Assert.Equal(surface, patched!.Capabilities?.Surface);
        Assert.Equal(2560, patched.Capabilities?.CssWidth);
    }

    [Fact]
    public void Patch_LetsAPhoneRecordChangeSurface()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));

        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Desktop },
        });

        Assert.Equal(PanelSurfaces.Desktop, patched!.Capabilities?.Surface);
    }

    private static PanelLayoutDto Layout(string widgetType) => new()
    {
        Pages = { new PanelPageDto { Id = "p1", Widgets = { new PanelWidgetDto { Id = "w1", Type = widgetType } } } },
    };

    [Fact]
    public void ActivatePreset_AppliesTheSnapshot_AndCapturesThePresetBeingLeft()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Q60));
        _registry.Patch(record.Id, new PanelDevicePatch { Layout = Layout("clock"), ThemeMode = "light" });
        var desk = _registry.CreatePreset(record.Id, "Desk", out _)!.ActiveId!;
        var game = _registry.CreatePreset(record.Id, "Game", out _)!.ActiveId!;
        // Edits while Game is loaded belong to Game.
        _registry.Patch(record.Id, new PanelDevicePatch { Layout = Layout("gallery"), AccentColor = "#ff0000", ThemeMode = "dark", TextColorMode = "custom", TextColor = "#00ff00" });

        var afterDesk = _registry.ActivatePreset(record.Id, desk);

        Assert.Equal(desk, afterDesk!.ActiveId);
        var live = _registry.Get(record.Id)!;
        Assert.Equal("clock", live.Layout!.Pages[0].Widgets[0].Type);
        Assert.Equal("light", live.ThemeMode);
        // Null in the snapshot resets the field rather than leaving Game's accent.
        Assert.Null(live.AccentColor);
        Assert.Null(live.TextColor);

        _registry.ActivatePreset(record.Id, game);
        live = _registry.Get(record.Id)!;
        Assert.Equal("gallery", live.Layout!.Pages[0].Widgets[0].Type);
        Assert.Equal("#ff0000", live.AccentColor);
        Assert.Equal("dark", live.ThemeMode);
        Assert.Equal("#00ff00", live.TextColor);
    }

    [Fact]
    public void Preset_DoesNotShareTheLiveLayout()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Q60));
        _registry.Patch(record.Id, new PanelDevicePatch { Layout = Layout("clock") });
        var desk = _registry.CreatePreset(record.Id, "Desk", out _)!.ActiveId!;
        _registry.CreatePreset(record.Id, "Other", out _);

        // Widget settings mutate the stored layout in place.
        _store.Update(s => s.PanelDevices[record.Id].Layout!.Pages[0].Widgets[0].Type = "weather");
        _registry.ActivatePreset(record.Id, desk);

        Assert.Equal("clock", _registry.Get(record.Id)!.Layout!.Pages[0].Widgets[0].Type);
    }

    [Fact]
    public void Presets_NeverRideTheRecordSnapshot()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Q60));
        _registry.CreatePreset(record.Id, "Desk", out _);

        Assert.Null(_registry.Get(record.Id)!.Presets);
        Assert.Single(_registry.ListPresets(record.Id)!.Presets);
    }

    [Fact]
    public void CreatePreset_RefusesPastTheCap()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Q60));
        for (var i = 0; i < PanelPresets.Cap; i++)
            _registry.CreatePreset(record.Id, "P" + i, out _);

        var result = _registry.CreatePreset(record.Id, "one too many", out var capped);

        Assert.True(capped);
        Assert.Null(result);
        Assert.Equal(PanelPresets.Cap, _registry.ListPresets(record.Id)!.Presets.Count);
    }

    [Fact]
    public void SetPresetApps_RefusesAnAppAnotherPresetOfThePanelHolds()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Q60));
        var desk = _registry.CreatePreset(record.Id, "Desk", out _)!.ActiveId!;
        var game = _registry.CreatePreset(record.Id, "Game", out _)!.ActiveId!;
        _registry.SetPresetApps(record.Id, game, new() { new PresetAppBinding { Id = "proc:eldenring", Name = "Elden Ring", ProcessName = "eldenring" } }, out _);

        var result = _registry.SetPresetApps(
            record.Id, desk, new() { new PresetAppBinding { Id = "shortcut:1", Name = "Elden Ring", ProcessName = "eldenring" } }, out var conflict);

        Assert.Null(result);
        Assert.Equal(("Elden Ring", "Game"), conflict);
    }

    [Fact]
    public void ResetToDefaults_ClearsPresets()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Q60));
        _registry.CreatePreset(record.Id, "Desk", out _);

        _registry.ResetToDefaults(record.Id);

        var presets = _registry.ListPresets(record.Id)!;
        Assert.Empty(presets.Presets);
        Assert.Null(presets.ActiveId);
    }
}
