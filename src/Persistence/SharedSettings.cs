using System.Collections.Generic;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Persistence;

// Shared POCOs used by both install-defaults (the seed table) and the live
// NexusSettings document. install-defaults populates the cosmetic + seed fields
// and leaves runtime-only fields (DashboardLayout, OverlayLayout,
// DetailedCollapsed) null; the live profile populates runtime-only fields and
// usually leaves Layouts null because the install-defaults table remains the
// source of truth for seeding new device records.

public sealed class ThemeSettings
{
    public string Language { get; set; } = "en";
    public string ThemeMode { get; set; } = "system";
    public string AccentColor { get; set; } = "#2563eb";
    // The desktop app's *resolved* theme ("dark"/"light"), republished whenever
    // it changes. Lets remote panels in sync mode follow the desktop OS's
    // light↔dark instead of re-resolving "system" against their own device's OS
    // (the wrong OS). Empty = never published; panels fall back to ThemeMode.
    public string ResolvedThemeMode { get; set; } = "";
    // Dashboard window backdrop ("glass"/"gradient"/"flat") and accent policy
    // ("system" follows the OS accent, "custom" uses AccentColor). Formerly
    // client-only (per-browser localStorage), which diverged across window
    // contexts (embedded WebView2 vs a --app temp profile). Empty = unset: a
    // client that has never written them keeps its own default and seeds the
    // server once, so an upgrade doesn't clobber an existing choice.
    public string BackgroundMode { get; set; } = "";
    public string AccentSource { get; set; } = "";
}

public sealed class MonitoringSettings
{
    public bool ShowAverage { get; set; } = true;
    public bool ShowMacStatusBarIcon { get; set; } = true;
    public bool ShowWindowsTrayIcon { get; set; } = true;
    /// <summary>IDs of sections collapsed on the Monitoring "Detailed" tab. Empty list = every section expanded. The SPA writes the full list on every toggle so the persisted state matches the current UI exactly.</summary>
    public List<string> DetailedCollapsed { get; set; } = new();
}

public sealed class PanelSettings
{
    /// <summary>Runtime visibility of the Y70 panel kiosk. When true, the
    /// nexus-overlay sidecar opens the kiosk window (and auto-relaunches when
    /// the Y70 reconnects). Default on. Surfaced as "Show Panel" in the UI.</summary>
    public bool AutoLaunch { get; set; } = true;
    /// <summary>When true, the overlay keeps the Y70 panel monitor exclusive to
    /// the kiosk - foreign windows that land on it are relocated back to a
    /// normal monitor. Default on. Surfaced as "Keep panel clear of other
    /// windows" under Panel settings.</summary>
    public bool ReserveMonitor { get; set; } = true;
    public bool ThemeSyncWithDesktop { get; set; } = true;
    public string ThemeMode { get; set; } = "system";
    public bool AccentSyncWithDesktop { get; set; } = true;
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BackgroundColorLight { get; set; }
    public string BackgroundMode { get; set; } = "solid";
    public string BackgroundEffect { get; set; } = "aurora";
    public int BackgroundTemplate { get; set; }
    public double BackgroundOpacity { get; set; } = 0.4;
    /// <summary>Opacity of the entire panel surface itself (0 = fully transparent,
    /// desktop wallpaper visible through the kiosk; 1 = fully opaque). Distinct
    /// from BackgroundOpacity which is the dim of the background effect over the
    /// panel's widgets. Surfaced as "Panel Opacity" on monitor-style panels only.</summary>
    public double PanelOpacity { get; set; } = 1.0;
    public double WidgetOpacity { get; set; } = 1.0;
    public bool WidgetLabels { get; set; } = false;
    /// <summary>Layout seeds for new device records + first-time desktop dashboard. Populated in install-defaults; null in the live profile (the embedded install-defaults table remains the source of truth for seeding new device records).</summary>
    public PanelLayoutsDefaults? Layouts { get; set; }
    /// <summary>Active desktop dashboard layout (profile-scoped). Null in install-defaults; null in the live profile means "seed from Layouts.Desktop on first load".</summary>
    public PanelLayoutDto? DashboardLayout { get; set; }
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public bool AlwaysOnTop { get; set; }
    public int Scale { get; set; } = 100;
    public double Opacity { get; set; } = 1.0;
    public int Monitor { get; set; } = -1;
    /// <summary>Pinned floating-widget instances. Empty list = no widgets pinned.</summary>
    public List<OverlayWidgetDto> Layout { get; set; } = new();
}

// Layout seed types - used by install-defaults to define starter widget sets
// per surface. Live PanelDeviceRecord.Layout and PanelSettings.DashboardLayout
// use the richer PanelLayoutDto with ids + per-instance widget config.

public sealed class PanelLayoutsDefaults
{
    public PanelLayoutDefault Desktop { get; set; } = new();
    public PanelLayoutDefault Y70 { get; set; } = new();
    public PanelLayoutDefault Phone { get; set; } = new();
    public PanelLayoutDefault Q60 { get; set; } = new();
}

public sealed class PanelLayoutDefault
{
    public int LayoutSchemaVersion { get; set; } = 2;
    public string Surface { get; set; } = "";
    public List<PanelLayoutWidget> Widgets { get; set; } = new();
}

public sealed class PanelLayoutWidget
{
    public string Type { get; set; } = "";
    public string Size { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
    /// <summary>Optional per-instance seed config copied verbatim into the new widget instance when this seed is materialized. Same shape as <see cref="PanelWidgetDto.Config"/>: keys are widget-defined; values are raw JSON.</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? Config { get; set; }
}

// GET /preferences response. Mirrors PreferencesPatch shape so the SPA reads
// and writes through the same nested keys. CoolingPrefs is a slim projection
// of CoolingSettings (only the prefs surface - the full cooling view has its
// own endpoints).
public sealed class Preferences
{
    public ThemeSettings Theme { get; set; } = new();
    public PanelSettings Panel { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public MonitoringSettings Monitoring { get; set; } = new();
    public CoolingPrefs Cooling { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public UnitsSettings Units { get; set; } = new();
    public UpdatePrefs Update { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
}

public sealed class CoolingPrefs
{
    public List<string>? FanChannelOrder { get; set; }
    public string? PreferredCpuTempSensorId { get; set; }
    public string? PreferredGpuTempSensorId { get; set; }
    /// <summary>Primary GPU (by model name) for monitoring/sensor display. null = auto.</summary>
    public string? PreferredGpuId { get; set; }
}

/// <summary>Per-kind temperature ceiling in Celsius. Defaults come from
/// <see cref="TemperatureInsights"/>'s threshold constants, the same values the
/// graph-history episode shading uses; keep them the single source.</summary>
public sealed class DiagnosticsThresholds
{
    public double CpuC { get; set; } = TemperatureInsights.CpuThresholdC;
    public double GpuC { get; set; } = TemperatureInsights.GpuThresholdC;
    public double StorageC { get; set; } = TemperatureInsights.StorageThresholdC;
    public double RamC { get; set; } = TemperatureInsights.RamThresholdC;
}

public sealed class DiagnosticsThresholdsPatch
{
    public double? CpuC { get; set; }
    public double? GpuC { get; set; }
    public double? StorageC { get; set; }
    public double? RamC { get; set; }
}

/// <summary>Master + per-category native-notification toggles for diagnostics
/// alerts, plus the minimum gap between repeat alerts for the same
/// component. Every toggle defaults off; DiagnosticsAlertService only raises
/// a native notification when Enabled and the reason's own category are
/// both true.</summary>
public sealed class DiagnosticsNotifications
{
    // Master switch stays off by default; the per-category flags default on, so
    // turning notifications on notifies for every category without extra setup.
    public bool Enabled { get; set; }
    public bool HighTemp { get; set; } = true;
    public bool StorageHealth { get; set; } = true;
    public bool Cooling { get; set; } = true;
    public bool MemoryTest { get; set; } = true;
    public bool SystemDevices { get; set; } = true;
    public bool GpuThrottle { get; set; } = true;
    public int CooldownMinutes { get; set; } = 60;
}

public sealed class DiagnosticsNotificationsPatch
{
    public bool? Enabled { get; set; }
    public bool? HighTemp { get; set; }
    public bool? StorageHealth { get; set; }
    public bool? Cooling { get; set; }
    public bool? MemoryTest { get; set; }
    public bool? SystemDevices { get; set; }
    public bool? GpuThrottle { get; set; }
    public int? CooldownMinutes { get; set; }
}

/// <summary>Per-area enable flag for diagnostics health status + alerts. A
/// disabled area is excluded from GET /diagnostics/health aggregation and
/// never produces a notification.</summary>
public sealed class DiagnosticsComponents
{
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Storage { get; set; } = true;
    public bool Ram { get; set; } = true;
    public bool Cooling { get; set; } = true;
    public bool System { get; set; } = true;
}

public sealed class DiagnosticsComponentsPatch
{
    public bool? Cpu { get; set; }
    public bool? Gpu { get; set; }
    public bool? Storage { get; set; }
    public bool? Ram { get; set; }
    public bool? Cooling { get; set; }
    public bool? System { get; set; }
}

public sealed class DiagnosticsSettings
{
    public DiagnosticsThresholds Thresholds { get; set; } = new();
    /// <summary>Minutes an over-threshold warning is kept after the component
    /// last measured hot. 0 = clears as soon as the latest sample cools.</summary>
    public int WarningLingerMinutes { get; set; }
    public DiagnosticsNotifications Notifications { get; set; } = new();
    public DiagnosticsComponents Components { get; set; } = new();
}

public sealed class DiagnosticsSettingsPatch
{
    public DiagnosticsThresholdsPatch? Thresholds { get; set; }
    public int? WarningLingerMinutes { get; set; }
    public DiagnosticsNotificationsPatch? Notifications { get; set; }
    public DiagnosticsComponentsPatch? Components { get; set; }
}

// PATCH wrappers. POST /preferences accepts PreferencesPatch with optional
// per-domain sub-patches; each sub-patch's fields are nullable so the handler
// can distinguish "client left this out" from "client explicitly sent value".

public sealed class PreferencesPatch
{
    public ThemeSettingsPatch? Theme { get; set; }
    public PanelSettingsPatch? Panel { get; set; }
    public OverlaySettingsPatch? Overlay { get; set; }
    public MonitoringSettingsPatch? Monitoring { get; set; }
    public CoolingPrefsPatch? Cooling { get; set; }
    public UiSettingsPatch? Ui { get; set; }
    public UnitsSettingsPatch? Units { get; set; }
    public UpdatePrefsPatch? Update { get; set; }
    public DiagnosticsSettingsPatch? Diagnostics { get; set; }
}

public sealed class ThemeSettingsPatch
{
    public string? Language { get; set; }
    public string? ThemeMode { get; set; }
    public string? AccentColor { get; set; }
    public string? ResolvedThemeMode { get; set; }
    public string? BackgroundMode { get; set; }
    public string? AccentSource { get; set; }
}

public sealed class PanelSettingsPatch
{
    public bool? AutoLaunch { get; set; }
    public bool? ReserveMonitor { get; set; }
    public bool? ThemeSyncWithDesktop { get; set; }
    public string? ThemeMode { get; set; }
    public bool? AccentSyncWithDesktop { get; set; }
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BackgroundColorLight { get; set; }
    public string? BackgroundMode { get; set; }
    public string? BackgroundEffect { get; set; }
    public int? BackgroundTemplate { get; set; }
    public double? BackgroundOpacity { get; set; }
    public double? PanelOpacity { get; set; }
    public double? WidgetOpacity { get; set; }
    public bool? WidgetLabels { get; set; }
    public PanelLayoutDto? DashboardLayout { get; set; }
}

public sealed class OverlaySettingsPatch
{
    public bool? Enabled { get; set; }
    public bool? AlwaysOnTop { get; set; }
    public int? Scale { get; set; }
    public double? Opacity { get; set; }
    public int? Monitor { get; set; }
    public List<OverlayWidgetDto>? Layout { get; set; }
}

public sealed class MonitoringSettingsPatch
{
    public bool? ShowAverage { get; set; }
    public bool? ShowMacStatusBarIcon { get; set; }
    public bool? ShowWindowsTrayIcon { get; set; }
    public List<string>? DetailedCollapsed { get; set; }
}

public sealed class UpdatePrefs
{
    public string UpdateMode { get; set; } = "always";
    public string UpdateChannel { get; set; } = "production";
    public string LastDismissedUpdateVersion { get; set; } = "";
    public string LastRunVersion { get; set; } = "";
}

public sealed class UpdatePrefsPatch
{
    public string? UpdateMode { get; set; }
    public string? UpdateChannel { get; set; }
    public string? LastDismissedUpdateVersion { get; set; }
    public string? LastRunVersion { get; set; }
}

public sealed class CoolingPrefsPatch
{
    public List<string>? FanChannelOrder { get; set; }
    // The two sensor-id fields reuse `string?` for both "field omitted" and
    // "reset to auto" - null on the wire means the client did not send it
    // (handler preserves the stored value); an empty string means the user
    // explicitly cleared their pinned choice (handler stores null). Do not
    // collapse these into a single semantic without updating ProfileRoutes.
    public string? PreferredCpuTempSensorId { get; set; }
    public string? PreferredGpuTempSensorId { get; set; }
    // Same omitted-vs-reset semantics as the temp sensor fields above: null =
    // not sent (preserve stored), empty string = reset to auto (store null).
    public string? PreferredGpuId { get; set; }
}
