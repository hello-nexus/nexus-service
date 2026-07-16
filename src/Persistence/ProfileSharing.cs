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
                target.Overlay = source.Overlay;
                target.Ui.ShowConflictAlerts = source.Ui.ShowConflictAlerts;
                break;
            case Device:
                target.StreamDeck = source.StreamDeck;
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
                target.Lighting = new LightingSettings();
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
                target.Overlay = new OverlaySettings();
                target.Ui.ShowConflictAlerts = true;
                break;
            case Device:
                target.StreamDeck = new StreamDeckSettings();
                target.Keeb = new KeebSettings();
                break;
        }
    }
}
