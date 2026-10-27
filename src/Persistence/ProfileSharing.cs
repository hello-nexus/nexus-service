using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Persistence;

/// <summary>
/// Routing helpers for the per-category profile sharing feature. Six
/// categories correspond to the NexusSettings sections that the user can pin to
/// a Primary profile (so switching profiles still loads that profile's data
/// for the pinned category). Theme copies the entire <see cref="ThemeSettings"/>
/// block; Dashboard copies the desktop-side per-profile state (monitoring view
/// state, fan-channel order, the desktop dashboard layout, overlay floating
/// widgets, conflict-alert toggle); Device copies the entire
/// <see cref="StreamDeckSettings"/> and <see cref="KeebSettings"/> blocks,
/// making Stream Deck bindings and keyboard personalization (macros, key
/// overrides, rotary, game mode, firmware lighting, layers) profile-scoped
/// instead of workstation-global. Panels carries each panel's look and the
/// cooler display content (see <see cref="PanelProfileData"/>); a profile file
/// holds it as stripped records that apply onto the matching live panels. Device
/// and Panels default to Shared (see <see cref="NexusSettings.SharedCategories"/>).
/// Panel cosmetics + AutoLaunch at the NexusSettings root under
/// <see cref="PanelSettings"/> are workstation-level and are NEVER copied via sharing.
/// </summary>
public static class ProfileSharing
{
    public const string Lighting = "lighting";
    public const string Cooling = "cooling";
    public const string Theme = "theme";
    public const string Dashboard = "dashboard";
    public const string Device = "device";
    public const string Panels = "panels";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Lighting, Cooling, Theme, Dashboard, Device, Panels,
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
    public static NexusSettings ExtractShareable(NexusSettings source, NexusSettings? prior = null)
    {
        // Panel records the profile file holds for panels absent from this PC ride along.
        var extract = new NexusSettings { PanelDevices = prior?.PanelDevices.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new() };
        for (var i = 0; i < All.Count; i++)
        {
            WriteCategory(extract, source, All[i]);
        }
        return extract;
    }

    /// <summary>
    /// Writes a category into a profile file or export payload. Identical to
    /// <see cref="ApplyCategory"/> except Panels, which a file stores as
    /// stripped records instead of matching onto live ones.
    /// </summary>
    public static void WriteCategory(NexusSettings file, NexusSettings source, string category)
    {
        if (Normalize(category) == Panels)
        {
            PanelProfileData.WriteTo(file, source);
            return;
        }
        ApplyCategory(file, source, category);
    }

    /// <summary>Copies the named category from <paramref name="source"/> onto <paramref name="target"/>. Sibling state on target is preserved. Panels copies looks onto target's matching panel records only.</summary>
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
                // ResolvedThemeMode is the host OS theme, not profile content.
                // Copied, not shared: writing a profile file passes the live settings
                // as source, and patching the host value into it would wipe the live one.
                var hostTheme = target.Theme.ResolvedThemeMode;
                target.Theme = source.Theme.Copy();
                target.Theme.ResolvedThemeMode = hostTheme;
                break;
            case Dashboard:
                target.Monitoring = source.Monitoring;
                target.Cooling.FanChannelOrder = source.Cooling.FanChannelOrder;
                target.Cooling.PreferredCpuTempSensorId = source.Cooling.PreferredCpuTempSensorId;
                target.Cooling.PreferredGpuTempSensorId = source.Cooling.PreferredGpuTempSensorId;
                target.Cooling.PreferredGpuId = source.Cooling.PreferredGpuId;
                target.Panel.DashboardLayout = source.Panel.DashboardLayout;
                target.Panel.DashboardPresets = source.Panel.DashboardPresets;
                target.Panel.DashboardActivePresetId = source.Panel.DashboardActivePresetId;
                target.Panel.DashboardGaugeGradient = source.Panel.DashboardGaugeGradient;
                // The retired overlay's block still travels with its profile: its markers are per profile and
                // an unloaded profile's widgets migrate when it is first loaded.
                target.Overlay = source.Overlay;
                target.Ui.ShowConflictAlerts = source.Ui.ShowConflictAlerts;
                target.Ui.AutoKillConflictsAtStartup = source.Ui.AutoKillConflictsAtStartup;
                target.Ui.NotifyConflictLaunches = source.Ui.NotifyConflictLaunches;
                target.Ui.EndConflictsOnLaunch = source.Ui.EndConflictsOnLaunch;
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
            case Panels:
                PanelProfileData.ApplyToLive(target, source);
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
                target.Theme = new ThemeSettings { ResolvedThemeMode = target.Theme.ResolvedThemeMode };
                break;
            case Dashboard:
                target.Monitoring = new MonitoringSettings();
                target.Cooling.FanChannelOrder = null;
                target.Cooling.PreferredCpuTempSensorId = null;
                target.Cooling.PreferredGpuTempSensorId = null;
                target.Cooling.PreferredGpuId = null;
                target.Panel.DashboardLayout = null;
                target.Panel.DashboardPresets = null;
                target.Panel.DashboardActivePresetId = null;
                target.Panel.DashboardGaugeGradient = null;
                target.Overlay = new LegacyOverlaySettings();
                target.Ui.ShowConflictAlerts = true;
                target.Ui.AutoKillConflictsAtStartup = false;
                target.Ui.NotifyConflictLaunches = true;
                target.Ui.EndConflictsOnLaunch = false;
                break;
            case Device:
                target.StreamDeck = new StreamDeckSettings();
                target.Keeb = new KeebSettings();
                break;
            case Panels:
                PanelProfileData.Reset(target);
                break;
        }
    }
}
