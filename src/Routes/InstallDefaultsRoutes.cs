using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Defaults;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

public static class InstallDefaultsRoutes
{
    /// <summary>
    /// Read-only access to the canonical install-defaults table baked
    /// into the AOT binary at data/install-defaults.json. The companion
    /// /defaults/snapshot reads the *live* running config and projects
    /// it back into the same shape; the SPA's debug-tools menu copies
    /// that snapshot to the clipboard so the user can paste it to the
    /// AI to overwrite install-defaults.json.
    /// </summary>
    public static void MapDefaultsEndpoints(this WebApplication app)
    {
        app.MapGet("/defaults", () => InstallDefaults.All).AllowPanel();

        app.MapGet("/defaults/snapshot", (IConfigStore store) =>
        {
            var s = store.Load();
            // Project the live profile into install-defaults shape. Cosmetic
            // fields are copied as-is; runtime-only fields (DashboardLayout,
            // Overlay.Layout, Monitoring.DetailedCollapsed) are intentionally
            // omitted so paste-back-to-install-defaults doesn't smuggle
            // per-user state into the shipped JSON. Layouts is populated by
            // projecting live desktop dashboard + first matching device
            // record per surface, falling back to canonical when absent.
            return new InstallDefaultsDocument
            {
                Theme = new ThemeSettings
                {
                    Language = s.Theme.Language,
                    ThemeMode = s.Theme.ThemeMode,
                    AccentColor = s.Theme.AccentColor,
                },
                Monitoring = new MonitoringSettings
                {
                    ShowAverage = s.Monitoring.ShowAverage,
                    ShowMacStatusBarIcon = s.Monitoring.ShowMacStatusBarIcon,
                    ShowWindowsTrayIcon = s.Monitoring.ShowWindowsTrayIcon,
                },
                Panel = new PanelSettings
                {
                    AutoLaunch = s.Panel.AutoLaunch,
                    ThemeSyncWithDesktop = s.Panel.ThemeSyncWithDesktop,
                    ThemeMode = s.Panel.ThemeMode,
                    AccentSyncWithDesktop = s.Panel.AccentSyncWithDesktop,
                    AccentColor = s.Panel.AccentColor,
                    BackgroundColor = s.Panel.BackgroundColor,
                    BackgroundColorLight = s.Panel.BackgroundColorLight,
                    BackgroundMode = s.Panel.BackgroundMode,
                    BackgroundEffect = s.Panel.BackgroundEffect,
                    BackgroundTemplate = s.Panel.BackgroundTemplate,
                    BackgroundOpacity = s.Panel.BackgroundOpacity,
                    WidgetOpacity = s.Panel.WidgetOpacity,
                    WidgetLabels = s.Panel.WidgetLabels,
                    Layouts = new PanelLayoutsDefaults
                    {
                        Desktop = ProjectLayout(s.Panel.DashboardLayout, "desktop")
                                  ?? InstallDefaults.Panel.Layouts?.Desktop ?? new(),
                        Y70     = ProjectFirstDeviceLayout(s.PanelDevices, "y70")
                                  ?? InstallDefaults.Panel.Layouts?.Y70 ?? new(),
                        Phone   = ProjectFirstDeviceLayout(s.PanelDevices, "phone")
                                  ?? InstallDefaults.Panel.Layouts?.Phone ?? new(),
                        Q60     = ProjectFirstDeviceLayout(s.PanelDevices, "q60")
                                  ?? InstallDefaults.Panel.Layouts?.Q60 ?? new(),
                    },
                },
                Overlay = new OverlaySettings
                {
                    Enabled = s.Overlay.Enabled,
                    AlwaysOnTop = s.Overlay.AlwaysOnTop,
                    Scale = s.Overlay.Scale,
                    Opacity = s.Overlay.Opacity,
                    Monitor = s.Overlay.Monitor,
                },
                Lighting = new LightingDefaults
                {
                    Sync = s.Lighting.Sync,
                    BrightnessEnabled = s.Lighting.BrightnessEnabled,
                    SpeedEnabled = s.Lighting.SpeedEnabled,
                    FrameRate = s.Lighting.FrameRate,
                    ScaleRatio = s.Lighting.ScaleRatio,
                    MusicReactive = s.Lighting.MusicReactive,
                    StaticColor = new LightingStaticColor
                    {
                        R = s.Lighting.StaticColor.R,
                        G = s.Lighting.StaticColor.G,
                        B = s.Lighting.StaticColor.B,
                    },
                    Animate = new LightingAnimateDefaults
                    {
                        Effect = s.Lighting.Animate.Effect,
                        // State is a per-effect dictionary at runtime holding
                        // only deltas from the selected preset look; resolve
                        // through the templates so the export always carries
                        // the active effect's effective slider snapshot.
                        State = (s.Lighting.Animate.States.TryGetValue(s.Lighting.Animate.Effect, out var st) && st is not null
                                ? st
                                : Nexus.Service.Lighting.AnimateTemplateDefaults.ResolveSelected(s.Lighting.Animate.Templates, s.Lighting.Animate.Effect))
                            is { } effective
                            ? new LightingAnimateState
                            {
                                Speed = effective.Speed,
                                Intensity = effective.Intensity,
                                Hue = effective.Hue,
                                Colorize = effective.Colorize,
                                Saturation = effective.Saturation,
                                Contrast = effective.Contrast,
                            }
                            : InstallDefaults.Lighting.Animate.State,
                    },
                    PostProcess = new LightingPostProcess
                    {
                        Hue = s.Lighting.ScreenEffect.Hue,
                        Colorize = s.Lighting.ScreenEffect.Colorize,
                        Saturation = s.Lighting.ScreenEffect.Saturation,
                        Contrast = s.Lighting.ScreenEffect.Contrast,
                    },
                    DevicePreference = InstallDefaults.Lighting.DevicePreference,
                },
                Y70 = new Y70Defaults
                {
                    Orientation = s.Y70.Orientation,
                    Brightness = s.Y70.Brightness,
                    ScreenOff = s.Y70.ScreenOff,
                },
                Keeb = new KeebDefaults
                {
                    RotaryLeft = s.Keeb.RotaryLeft,
                    RotaryRight = s.Keeb.RotaryRight,
                    FirmwareLighting = new KeebFirmwareLightingDefaults
                    {
                        AnimationMode = s.Keeb.FirmwareLighting.AnimationMode,
                        Speed = s.Keeb.FirmwareLighting.Speed,
                        Direction = s.Keeb.FirmwareLighting.Direction,
                        Brightness = s.Keeb.FirmwareLighting.Brightness,
                        KeyReactive = s.Keeb.FirmwareLighting.KeyReactive,
                        KeyReactiveMask = s.Keeb.FirmwareLighting.KeyReactiveMask,
                        KeyReactiveMode = s.Keeb.FirmwareLighting.KeyReactiveMode,
                    },
                },
                Cooling = new CoolingDefaults
                {
                    GlobalSpeedModifier = s.Cooling.GlobalSpeedModifier,
                    ActivePreset = s.Cooling.ActivePreset,
                    Presets = InstallDefaults.Cooling.Presets,
                    DeviceLayoutSize = InstallDefaults.Cooling.DeviceLayoutSize,
                },
                Obs = new ObsDefaults
                {
                    Host = s.Obs.Host,
                    Port = s.Obs.Port,
                },
                ScreenTime = new ScreenTimeDefaults
                {
                    TrackingEnabled = s.ScreenTime.TrackingEnabled,
                },
                Cnvs = new CnvsDefaults
                {
                    PlayAnimation = s.Devices.Cnvs.PlayAnimation,
                    PlayWhenPCOff = s.Devices.Cnvs.PlayWhenPCOff,
                },
                Auth = new AuthDefaults
                {
                    RemoteControlEnabled = s.Auth?.RemoteControlEnabled ?? InstallDefaults.Auth.RemoteControlEnabled,
                },
            };
        }).AllowPanel();
    }

    // Flatten a runtime PanelLayoutDto (pages → widgets) into the
    // install-defaults shape (single flat widget list, no ids / config).
    private static PanelLayoutDefault? ProjectLayout(PanelLayoutDto? dto, string surface)
    {
        if (dto is null) return null;
        var widgets = (dto.Pages.FirstOrDefault()?.Widgets ?? new List<PanelWidgetDto>())
            .Select(w => new PanelLayoutWidget { Type = w.Type, Size = w.Size, Col = w.Col, Row = w.Row, Config = w.Config })
            .ToList();
        return new PanelLayoutDefault
        {
            LayoutSchemaVersion = dto.LayoutSchemaVersion,
            Surface = surface,
            Widgets = widgets,
        };
    }

    private static PanelLayoutDefault? ProjectFirstDeviceLayout(Dictionary<string, PanelDeviceRecord> devices, string surface)
    {
        var match = devices.Values
            .Where(d => d.Layout is not null
                && string.Equals(d.Layout.Surface, surface, System.StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Layout)
            .FirstOrDefault();
        return ProjectLayout(match, surface);
    }
}
