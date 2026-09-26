using System;
using System.Collections.Generic;

namespace Nexus.Service.Persistence;

/// <summary>
/// Routing helpers for the per-category profile sharing feature. Five
/// categories correspond to the NexusSettings sections that the user can pin to
/// a Primary profile (so switching profiles still loads that profile's data
/// for the pinned category). Theme copies the entire <see cref="ThemeSettings"/>
/// block; Dashboard copies the desktop-side per-profile state (monitoring view
/// state, fan-channel order, the desktop dashboard layout, overlay floating
/// widgets, conflict-alert toggle); Device copies the entire
/// <see cref="StreamDeckSettings"/> and <see cref="KeebSettings"/> blocks,
/// making Stream Deck bindings and keyboard personalization (macros, key
/// overrides, rotary, game mode, firmware lighting, layers) profile-scoped
/// instead of workstation-global. Panel cosmetics + AutoLaunch live at the
/// NexusSettings root under <see cref="PanelSettings"/>; they're
/// workstation-level (they describe how panel devices look and behave, not the
/// active profile) so they are NEVER copied via sharing.
/// </summary>
public static class ProfileSharing
{
    public const string Lighting = "lighting";
    public const string Cooling = "cooling";
    public const string Theme = "theme";
    public const string Dashboard = "dashboard";
    public const string Device = "device";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Lighting, Cooling, Theme, Dashboard, Device,
    };

    public static string? Normalize(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var lower = id.Trim().ToLowerInvariant();
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == lower)
            {
                return All[i];
            }
        }
        return null;
    }

    /// <summary>
    /// A settings object carrying ONLY the shareable categories, for cloud
    /// sync payloads and cross-machine import previews. Everything else -
    /// hardware-bound state (Y70, Devices, PanelDevices, every Panel* field),
    /// Telemetry, and every integration credential block (Steam, Discord,
    /// HomeAssistant, Obs, SmartLights, all mounted at the NexusSettings root)
    /// - is left at its default, so it neither inflates the payload nor
    /// travels off the machine. Category order matters: Dashboard writes
    /// individual Cooling fields, so it must run after Cooling replaces the
    /// whole block.
    /// </summary>
    public static NexusSettings ExtractShareable(NexusSettings source)
    {
        var extract = new NexusSettings();
        for (var i = 0; i < All.Count; i++)
        {
            ApplyCategory(extract, source, All[i]);
        }
        return extract;
    }

    /// <summary>Copies the named category from <paramref name="source"/> onto <paramref name="target"/>. Sibling state on target is preserved.</summary>
    public static void ApplyCategory(NexusSettings target, NexusSettings source, string category)
    {
        switch (Normalize(category))
        {
            case Lighting:
                target.Lighting = source.Lighting;
                break;
            case Cooling:
                target.Cooling = source.Cooling;
                break;
            case Theme:
                target.Theme = source.Theme;
                break;
            case Dashboard:
                target.Monitoring = source.Monitoring;
                target.Cooling.FanChannelOrder = source.Cooling.FanChannelOrder;
                target.Cooling.PreferredCpuTempSensorId = source.Cooling.PreferredCpuTempSensorId;
                target.Cooling.PreferredGpuTempSensorId = source.Cooling.PreferredGpuTempSensorId;
                target.Cooling.PreferredGpuId = source.Cooling.PreferredGpuId;
                target.Panel.DashboardLayout = source.Panel.DashboardLayout;
                target.Panel.DashboardGaugeGradient = source.Panel.DashboardGaugeGradient;
                target.Overlay = source.Overlay;
                target.Ui.ShowConflictAlerts = source.Ui.ShowConflictAlerts;
                target.Ui.AutoKillConflictsAtStartup = source.Ui.AutoKillConflictsAtStartup;
                target.Ui.ConflictAutoKillExclusions = source.Ui.ConflictAutoKillExclusions;
                target.Ui.NotifyConflictLaunches = source.Ui.NotifyConflictLaunches;
                break;
            case Device:
                // The recent-apps ring names this machine's processes and exe
                // paths, so it stays with the target: a profile export starts
                // from a fresh target (empty ring), and a profile load keeps
                // the live ring instead of taking the file's.
                target.StreamDeck = new StreamDeckSettings
                {
                    Decks = source.StreamDeck.Decks,
                    Presets = source.StreamDeck.Presets,
                    Instances = source.StreamDeck.Instances,
                    RecentApps = target.StreamDeck.RecentApps,
                    RecentAppsExcluded = target.StreamDeck.RecentAppsExcluded,
                };
                target.Keeb = source.Keeb;
                break;
        }
    }

    /// <summary>Resets the named category on <paramref name="target"/> to a fresh default value. Sibling state is preserved.</summary>
    public static void ResetCategory(NexusSettings target, string category)
    {
        switch (Normalize(category))
        {
            case Lighting:
                target.Lighting = new LightingSettings { FreeRotationLayouts = true };
                break;
            case Cooling:
                target.Cooling = new CoolingSettings();
                break;
            case Theme:
                target.Theme = new ThemeSettings();
                break;
            case Dashboard:
                target.Monitoring = new MonitoringSettings();
                target.Cooling.FanChannelOrder = null;
                target.Cooling.PreferredCpuTempSensorId = null;
                target.Cooling.PreferredGpuTempSensorId = null;
                target.Cooling.PreferredGpuId = null;
                target.Panel.DashboardLayout = null;
                target.Panel.DashboardGaugeGradient = null;
                target.Overlay = new OverlaySettings();
                target.Ui.ShowConflictAlerts = true;
                target.Ui.AutoKillConflictsAtStartup = false;
                target.Ui.ConflictAutoKillExclusions = new();
                target.Ui.NotifyConflictLaunches = true;
                break;
            case Device:
                target.StreamDeck = new StreamDeckSettings();
                target.Keeb = new KeebSettings();
                break;
        }
    }
}
