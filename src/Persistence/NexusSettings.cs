using System.Collections.Generic;
using Nexus.Service.Deck;
using Nexus.Service.Defaults;

namespace Nexus.Service.Persistence;

/// <summary>
/// Root settings document persisted to disk. Every controller that needs to remember
/// state across restarts reads/writes through IConfigStore, which mutates this object.
///
/// New fields are SAFE to add - JSON deserialization tolerates missing keys via the
/// default values on each property. Renamed or deleted fields are NOT safe; bump
/// SchemaVersion and write a migration in JsonConfigStore.Load() if you do that.
/// </summary>
public sealed class NexusSettings
{
    /// <summary>
    /// Latest persisted-settings schema number. Bump in lockstep when adding
    /// a migration in <c>JsonConfigStore.Load()</c>. Lives as a constant so
    /// tests and tooling can reference "current" without bit-rotting.
    /// </summary>
    public const int CurrentSchemaVersion = 16;

    /// <summary>Persisted profile schema. v2 nests Theme/Panel/Overlay/Monitoring out of UiSettings into matching top-level POCOs that mirror install-defaults.json. v3 drops the <c>{s/n/b}</c> wrapper on per-widget config values; values are raw JSON (string/number/bool/object/array). v4 retires the type-scoped marketplace <c>Widgets</c> bag - every placement keeps its own config under <see cref="Nexus.Service.Models.Panel.PanelWidgetDto.Config"/>. v5 renames the <c>performance</c> cooling preset to <c>turbo</c>. v6 re-keys per-card LED map overrides/aspect ratios into the device-scoped segment-local <see cref="DevicesSettings.DeviceLedOverrides"/> / <see cref="DevicesSettings.DeviceAspectRatios"/> (zones model). v8 marks any pre-existing settings.json as already-onboarded (see <see cref="OnboardingCompleted"/>) so the first-run welcome screen only shows for installs with no settings.json at all. v9 prunes <see cref="AnimateSettings.Templates"/> to user deltas against the canonical defaults (see <see cref="Nexus.Service.Lighting.AnimateTemplateDefaults"/>). v10 prunes <see cref="AnimateSettings.States"/> entries equal to the effect's resolved selected-slot look. v11 rewrites the legacy <c>marketplace:</c> app-placement prefix to <c>app:</c> across all persisted widget types (see <see cref="Nexus.Service.Widgets.AppPrefixMigration"/>). v12 adds the <see cref="ProfileSharing.Device"/> sharing category (Stream Deck bindings), defaulted to Shared so an upgrading install keeps today's workstation-global behavior. v13 marks any pre-existing settings.json as already lighting-onboarded (see <see cref="LightingOnboardingCompleted"/>) so the lighting device-selection screen only shows for fresh installs. v14 seeded a per-page density mode that was later removed; it is now a plain version bump. v15 seeds <see cref="UiSettings.LightingDashboardMode"/> and <see cref="UiSettings.CoolingDashboardMode"/> to "advanced" for any pre-existing settings.json, so the reintroduced per-page density mode default ("simple") only applies to fresh installs. v16 marks any pre-existing settings.json as already features-onboarded (see <see cref="FeaturesOnboardingCompleted"/>) so the feature-pillars onboarding screen only shows for fresh installs; <see cref="Features"/> itself is additive (every flag already defaults true) and carries no migration arm. The v1-v4 load-time migrations were removed; records now load as-is and a malformed/older file falls back to defaults (see <see cref="JsonConfigStore"/>).</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ThemeSettings Theme { get; set; } = new();
    public MonitoringSettings Monitoring { get; set; } = new();
    public PanelSettings Panel { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public AuthSettings? Auth { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();
    public KeebSettings Keeb { get; set; } = new();
    public CoolingSettings Cooling { get; set; } = new();
    public Y70Settings Y70 { get; set; } = new();
    public QSeriesSettings QSeries { get; set; } = new();
    public TryxSettings Tryx { get; set; } = new();
    public DevicesSettings Devices { get; set; } = new();
    public SmartLightsSettings SmartLights { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public UnitsSettings Units { get; set; } = new();
    public ScreenTimeSettings ScreenTime { get; set; } = new();
    public FpsSettings Fps { get; set; } = new();
    public FocusSettings Focus { get; set; } = new();
    public ObsSettings Obs { get; set; } = new();
    /// <summary>Per-app volume mixer: remembered levels and named presets. NOT profile-scoped (absent from ProfileManager.CloneSettings): which apps are loud is a property of this workstation, not of a lighting/cooling persona.</summary>
    public AudioMixerSettings AudioMixer { get; set; } = new();
    public SteamSettings Steam { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();
    public HomeAssistantSettings HomeAssistant { get; set; } = new();
    public TelemetrySettings Telemetry { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    public FeaturesSettings Features { get; set; } = new();
    /// <summary>Registered panel devices keyed by opaque deviceId. Each record carries the per-device layout + theme overrides + capabilities. NOT profile-scoped: device identity is hardware-level and survives profile switches.</summary>
    public Dictionary<string, Nexus.Service.Models.Panel.PanelDeviceRecord> PanelDevices { get; set; } = new();

    /// <summary>EDID model identities ("CRX:ED00") panel auto-promotion has already acted on (or first observed as user-managed). A listed model is never auto-promoted again, so deleting an auto-created panel record sticks across port changes. NOT profile-scoped.</summary>
    public List<string> AutoPromotedPanelModels { get; set; } = new();

    /// <summary>User-overridden display name for this host PC. Empty means "fall back to Environment.MachineName". Surfaced in the panel tray header and in the QR/claim payload paired phones see. NOT profile-scoped: a host has one name regardless of which profile is active.</summary>
    public string HostDisplayName { get; set; } = "";

    /// <summary>Folder where phone→PC transfers land. Empty means auto-resolve (interactive user's Downloads/Nexus, falling back to CommonApplicationData/Nexus/inbox - see <see cref="Nexus.Service.Transfer.TransferInbox"/>). NOT profile-scoped.</summary>
    public string TransferInboxPath { get; set; } = "";

    /// <summary>Profile id designated as the source for any category currently in <c>SharedCategories</c>. When a category is shared, switching profiles still loads its values from this profile, and edits to that category save back here. NOT profile-scoped: this routing decision is workstation-level and survives profile switches. Null means no Primary; shared categories then fall back to the active profile.</summary>
    public string? PrimaryProfileId { get; set; }

    /// <summary>Category ids currently set to Shared. Allowed values: "lighting", "cooling", "theme", "dashboard", "device". Categories not in this list are per-profile (the default); "device" defaults to Shared on a fresh install so Stream Deck bindings and keyboard personalization start out workstation-global. NOT profile-scoped: workstation-level. Hardware-bound state (Y70, Devices, panel defaults) always lives at workstation root and is never per-profile, so it never appears here.</summary>
    public List<string> SharedCategories { get; set; } = new() { ProfileSharing.Device };

    /// <summary>One-time flag: the device sharing category's initial seed of StreamDeck+Keeb into every profile file has run. Workstation-level, not profile-scoped.</summary>
    public bool DeviceCategorySeeded { get; set; }

    /// <summary>OTA self-update settings. NOT profile-scoped: workstation-level.</summary>
    public UpdateSettings Update { get; set; } = new();

    /// <summary>Seconds (0-60, 0 disables) to hold one-shot hardware enumeration - LHM's open and the OpenRGB spawn - after service start, so a boot-time bus burst from third-party vendor tools drains first. See <see cref="Nexus.Service.Lifecycle.StartupDelayGate"/>. NOT profile-scoped: workstation-level.</summary>
    public int StartupDelaySeconds { get; set; }

    /// <summary>Disables all GPU monitoring: LHM skips GPU hardware entirely (no
    /// ADL FrameMetrics/PMLog sessions, no per-node D3DKMT statistics) and the
    /// per-process PDH GPU counters never start. Diagnostic switch for boxes
    /// where driver-level GPU telemetry interacts badly with the graphics stack
    /// (AMD + HAGS TDR reports); GPU tiles and per-process GPU columns go
    /// empty. Read once at service start - a change takes effect next boot.
    /// NOT profile-scoped: workstation-level.</summary>
    public bool DisableGpuMonitoring { get; set; }

    /// <summary>True once the desktop first-run welcome screen has been shown and
    /// dismissed. Install-scoped, not cloud profile synced: excluded from
    /// <see cref="Nexus.Service.Persistence.ProfileManager"/>'s CloneSettings
    /// allowlist and from <see cref="ProfileSharing"/> categories, so it never
    /// rides a profile export/import or a cloud push/pull. Lives in
    /// settings.json (not a separate marker file) so a factory reset wipes it
    /// and the welcome screen reappears.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>True once the Nexus 2 (legacy HYTE Nexus) returning-user
    /// welcome screen has been shown and dismissed. Install-scoped like
    /// <see cref="OnboardingCompleted"/>: excluded from
    /// <see cref="Nexus.Service.Persistence.ProfileManager"/>'s CloneSettings
    /// allowlist and from <see cref="ProfileSharing"/> categories, so it never
    /// rides a profile export/import or a cloud push/pull, and a factory
    /// reset re-offers the screen.</summary>
    public bool Nexus2MigrationOffered { get; set; }

    /// <summary>True once the lighting device-selection onboarding screen (shown
    /// right after the welcome screen) has been dismissed. Same scoping rules as
    /// <see cref="OnboardingCompleted"/>: install-scoped, never profile-synced,
    /// wiped by factory reset so the screen reappears.</summary>
    public bool LightingOnboardingCompleted { get; set; }

    /// <summary>True once the feature-pillars onboarding screen ("How much
    /// control do you want?", shown right before the Import-from-apps step)
    /// has been dismissed. Same scoping rules as <see cref="OnboardingCompleted"/>:
    /// install-scoped, never profile-synced, wiped by factory reset so the
    /// screen reappears. Schema v16 marks any pre-existing settings.json as
    /// already-complete (see <see cref="JsonConfigStore"/>), so only a fresh
    /// install sees the screen.</summary>
    public bool FeaturesOnboardingCompleted { get; set; }

    /// <summary>True once at least one Nexus 2 personalization category has been
    /// imported through the migration screen. Install-scoped like
    /// <see cref="Nexus2MigrationOffered"/>: excluded from CloneSettings and
    /// ProfileSharing, so it never rides a profile export/import or a cloud
    /// push/pull, and a factory reset allows re-importing.</summary>
    public bool Nexus2MigrationCompleted { get; set; }

    /// <summary>True once the FanControl import has been offered and dismissed.
    /// Install-scoped like <see cref="Nexus2MigrationOffered"/>: never rides a
    /// profile export/import or a cloud push/pull, and a factory reset re-offers
    /// the screen.</summary>
    public bool FanControlImportOffered { get; set; }

    /// <summary>True once at least one FanControl category has been imported.
    /// Same scoping as <see cref="FanControlImportOffered"/>.</summary>
    public bool FanControlImportCompleted { get; set; }

    /// <summary>Physical Stream Deck bindings, keyed by device serial. Profile-scoped via the <see cref="ProfileSharing.Device"/> sharing category (defaults to Shared, so it behaves like a workstation-global setting until the user opts a profile out).</summary>
    public StreamDeckSettings StreamDeck { get; set; } = new();

    /// <summary>AI Integration (MCP server) configuration. NOT profile-scoped: the listener, its token, and consent toggles are workstation-level.</summary>
    public AiIntegrationSettings AiIntegration { get; set; } = new();
}

/// <summary>
/// AI Integration: an in-process MCP (Model Context Protocol) server an AI
/// client can connect to for read/write access to this PC's hardware. Master
/// off by default; while off, <see cref="Nexus.Service.Mcp.McpServerHost"/>
/// does not bind any port. Each Allow* flag gates one <see cref="Nexus.Service.Mcp.McpCapability"/>
/// and is read live on every tool call, so a toggle flip takes effect on the
/// next call with no restart.
/// </summary>
public sealed class AiIntegrationSettings
{
    public bool Enabled { get; set; }
    /// <summary>Bearer token the MCP client presents on every request. Minted on first enable; empty until then.</summary>
    public string Token { get; set; } = "";
    /// <summary>Loopback port the MCP listener binds. 0 means an ephemeral port (test use only).</summary>
    public int Port { get; set; } = 9420;
    public bool AllowTelemetry { get; set; } = true;
    public bool AllowCooling { get; set; } = true;
    public bool AllowLighting { get; set; } = true;
    public bool AllowProfiles { get; set; } = true;
    public bool AllowHistory { get; set; } = true;

    /// <summary>Active local assistant model id (e.g. "qwen3.5:4b"), or "" when
    /// none is selected. Runtime install state is derived, not persisted; only
    /// the user's model choice survives a restart.</summary>
    public string AssistantActiveModel { get; set; } = "";

    /// <summary>Port the managed (or detected system) Ollama runtime binds.
    /// 0 means the Ollama default (11434).</summary>
    public int AssistantRuntimePort { get; set; }

    /// <summary>Opt-in to adopt an Ollama already listening on the default port
    /// instead of the managed download. Off by default: the service runs as
    /// LocalSystem, and whatever answers on the default port would otherwise
    /// receive every prompt and drive the tool calls; any local process can
    /// bind that port first.</summary>
    public bool UseSystemOllama { get; set; }
}

/// <summary>Global on/off for each functional pillar. Additive, all default
/// true, no migration arm needed: absent on any pre-existing settings.json
/// deserializes every flag to true via these initializers.</summary>
public sealed class FeaturesSettings
{
    public bool Lighting { get; set; } = true;
    public bool Cooling { get; set; } = true;
    public bool Monitoring { get; set; } = true;
    public bool Diagnostics { get; set; } = true;
}

/// <summary>
/// Paired network ("smart") lights - Philips Hue and (later) Nanoleaf, WLED,
/// LIFX, etc. Each entry is one controllable light surfaced as a
/// <see cref="Nexus.Service.Models.Devices.LightingDevice"/> card alongside the
/// USB / serial RGB devices. Discovery + pairing populate this list; the
/// SmartLightProvider routes control + canvas frames to the owning driver.
/// </summary>
public sealed class SmartLightsSettings
{
    public List<SmartLightConfig> Devices { get; set; } = new();
    /// <summary>Per-brand toggle (key = brand prefix like "hue"). A brand absent or
    /// false is OFF: not scanned, not probed, and its lights stay off the lighting
    /// canvas. Default off so a fresh install probes nothing until a brand is enabled.</summary>
    public Dictionary<string, bool> BrandEnabled { get; set; } = new();
}

public sealed class SmartLightConfig
{
    /// <summary>Stable device id, e.g. "hue:&lt;bridgeId&gt;:&lt;rid&gt;". The id prefix
    /// (brand) is how <see cref="Nexus.Service.Lighting.CompositeLightingDeviceProvider"/>
    /// routes control + frames.</summary>
    public string Id { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Last-known LAN host (ip or hostname) of the controller / bridge.</summary>
    public string Host { get; set; } = "";
    /// <summary>Hardware-stable key (bridge id / device serial / MAC) used to
    /// re-resolve the host when DHCP moves it.</summary>
    public string StableKey { get; set; } = "";
    /// <summary>Auth credential (Hue app-key, Nanoleaf/Twinkly token). Wrapped at
    /// rest via <see cref="Nexus.Service.Security.SecretProtector"/> on Windows;
    /// plaintext on macOS/Linux (same as Steam/Discord secrets).</summary>
    public string Token { get; set; } = "";
    /// <summary>Brand-specific opaque payload (Hue v2 resource id, LIFX zone
    /// count, Nanoleaf panel layout, …). Driver-defined contents.</summary>
    public string Extra { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class TelemetrySettings
{
    /// <summary>Default true only for a fresh install (JsonConfigStore.Load's !File.Exists branch); existing/migrated/corrupt-fallback settings keep this false until the user opts in.</summary>
    public bool CollectAnonymousData { get; set; }

    /// <summary>Anonymous per-install id; survives opt-out so a later opt-in resumes it instead of double-counting installs.</summary>
    public string InstallId { get; set; } = "";

    /// <summary>True once the one-time "install" fleet event reached nexus-api; an existing opted-in install backfills it once after upgrading.</summary>
    public bool FleetInstallDelivered { get; set; }

    /// <summary>Hash of the specs summary last delivered to nexus-api; a changed hash triggers a re-send.</summary>
    public string FleetSpecsHash { get; set; } = "";

    /// <summary>"opt_out"/"opt_in" fleet event awaiting delivery, persisted before CollectAnonymousData flips so a crash recovers on the next retry pass.</summary>
    public string FleetPendingConsentEvent { get; set; } = "";

    /// <summary>Round-trip UTC timestamp FleetPendingConsentEvent was armed; an opt_out undelivered past 7 days is abandoned instead of retried forever.</summary>
    public string FleetPendingConsentSince { get; set; } = "";
}

public sealed class ScreenTimeSettings
{
    /// <summary>When false, providers stop writing new focus sessions to the store. Reads of existing history continue to work.</summary>
    public bool TrackingEnabled { get; set; } = InstallDefaults.ScreenTime.TrackingEnabled;
}

public sealed class FpsSettings
{
    /// <summary>When false, capture stops entirely: no fps history series, no game sessions, no upload. Reads of existing history continue to work.</summary>
    public bool TrackingEnabled { get; set; } = InstallDefaults.Fps.TrackingEnabled;
}

/// <summary>
/// Focus modes: named sets of "hold things back while I am busy" behavior.
/// Ships with a Game and a Streaming mode; the user can rename, retune, add
/// and remove them. At most one is active at a time - the first eligible mode
/// in list order wins, so ordering is the user's precedence control.
/// </summary>
public sealed class FocusSettings
{
    public List<FocusModeSettings> Modes { get; set; } = FocusModeSettings.StockModes();
}

/// <summary>Trigger ids a mode can activate on. Persisted as strings so an unknown value from a newer build degrades to manual instead of failing the whole settings load.</summary>
public static class FocusTriggers
{
    public const string Manual = "manual";
    public const string Game = "game";
    public const string Obs = "obs";

    public static bool IsKnown(string? id) => id is Manual or Game or Obs;
}

public sealed class FocusModeSettings
{
    public const string GameModeId = "game";
    public const string StreamingModeId = "streaming";

    /// <summary>Stable id. The two stock ids are fixed; user modes get a guid.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Icon key from the web's focus icon set, not a path.</summary>
    public string Icon { get; set; } = "gamepad";

    /// <summary>Stock modes can be renamed, retuned and disabled, never deleted: their ids are wired to triggers and a reset needs a known-good pair to restore.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>What activates the mode. <see cref="FocusTriggers.Manual"/> means only the top bar switches it on.</summary>
    public string Trigger { get; set; } = FocusTriggers.Manual;

    public bool HoldNotifications { get; set; } = true;
    public bool HoldBackgroundTraffic { get; set; } = true;
    public bool TurnPanelDisplaysOff { get; set; }

    /// <summary>How long the mode stays active after its trigger clears.</summary>
    public int ExitGraceSeconds { get; set; } = 3;

    public static List<FocusModeSettings> StockModes() => new()
    {
        new FocusModeSettings
        {
            Id = GameModeId,
            Name = "Game Mode",
            Icon = "gamepad",
            BuiltIn = true,
            Trigger = FocusTriggers.Game,
            HoldNotifications = true,
            HoldBackgroundTraffic = true,
        },
        new FocusModeSettings
        {
            Id = StreamingModeId,
            Name = "Streaming",
            Icon = "broadcast",
            BuiltIn = true,
            Trigger = FocusTriggers.Obs,
            HoldNotifications = true,
            HoldBackgroundTraffic = true,
        },
    };
}

public sealed class ObsSettings
{
    public string Host { get; set; } = InstallDefaults.Obs.Host;
    public int Port { get; set; } = InstallDefaults.Obs.Port;
    public string Password { get; set; } = "";
}

public sealed class AudioMixerSettings
{
    /// <summary>Re-apply a remembered level when that app next opens a session.
    /// On by default: Windows resets app levels on relaunch and reboot, which is
    /// the complaint this feature exists to answer.</summary>
    public bool StickyLevels { get; set; } = true;

    /// <summary>Remembered levels keyed by strip id (process name, lowercased).
    /// Only apps the user has actually moved in the mixer land here.</summary>
    public Dictionary<string, Nexus.Service.Models.Activity.AudioMixerLevelDto> Levels { get; set; } = new();

    public List<Nexus.Service.Models.Activity.AudioMixerPresetDto> Presets { get; set; } = new();
}

public sealed class SteamSettings
{
    public string ApiKey { get; set; } = "";
    public string SteamId { get; set; } = "";
}

public sealed class DiscordSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Rich Presence needs no OAuth, so it is independent of ClientId/ClientSecret above.</summary>
    public bool RichPresenceEnabled { get; set; }
    /// <summary>One of DiscordRichPresence.Presets; empty resolves to the first.</summary>
    public string RichPresencePreset { get; set; } = "";
}

public sealed class HomeAssistantSettings
{
    public string Url { get; set; } = "";
    /// <summary>Wrapped via SecretProtector on Windows; plaintext on macOS/Linux.</summary>
    public string Token { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// UI-only residual state that has no install-defaults equivalent. Everything
/// that mirrors install-defaults.json now lives on NexusSettings root in
/// <see cref="ThemeSettings"/>, <see cref="MonitoringSettings"/>,
/// <see cref="PanelSettings"/>, and <see cref="OverlaySettings"/>.
/// </summary>
public sealed class UiSettings
{
    public bool ShowConflictAlerts { get; set; } = true;
    /// <summary>Shut down every catalog conflict app except the opted-out ones, once, at service start. Off by default - terminating another vendor's app is destructive, so it stays opt-in.</summary>
    public bool AutoKillConflictsAtStartup { get; set; }
    /// <summary>Catalog ids the user opted OUT of the startup shutdown. Stored as exclusions, not as an allow-list, so an app added to the catalog later is covered without a settings migration. Non-null so a profile that never wrote one sends an empty list rather than nothing, which the client would merge as "keep whatever the last profile had".</summary>
    public List<string> ConflictAutoKillExclusions { get; set; } = new();
    /// <summary>True once the web has auto-placed the OEM app onto the dashboard.</summary>
    public bool OemAppSeeded { get; set; }
    /// <summary>Sidebar pinned-app tail (app keys / app:&lt;id&gt; placement types),
    /// in display order. Null when the client has never sent it - the web then
    /// falls back to its local copy, so a machine-wiped browser recovers the
    /// pin from here instead of losing it (OemAppSeeded blocks a reseed).</summary>
    public List<string>? PinnedSidebarApps { get; set; }
    /// <summary>Per-page density of the dashboard lighting/cooling pages, "simple" or "advanced"; migration seeds pre-existing installs to "advanced".</summary>
    public string LightingDashboardMode { get; set; } = "simple";
    public string CoolingDashboardMode { get; set; } = "simple";
}

/// <summary>
/// Partial update DTO for POST /preferences. Every field is nullable so the
/// handler can distinguish "client left this out" from "client explicitly sent value".
/// </summary>
public sealed class UiSettingsPatch
{
    public bool? ShowConflictAlerts { get; set; }
    public bool? AutoKillConflictsAtStartup { get; set; }
    public List<string>? ConflictAutoKillExclusions { get; set; }
    public bool? OemAppSeeded { get; set; }
    public List<string>? PinnedSidebarApps { get; set; }
    public string? LightingDashboardMode { get; set; }
    public string? CoolingDashboardMode { get; set; }
}

/// <summary>
/// User-unit display preferences. The service stores and echoes these
/// verbatim; it never interprets the values (the client owns the semantics
/// of each string).
/// </summary>
public sealed class UnitsSettings
{
    /// <summary>"c" | "f".</summary>
    public string MonitoringTempUnit { get; set; } = "c";
    /// <summary>"system" | "12h" | "24h".</summary>
    public string TimeFormat { get; set; } = "system";
    /// <summary>"system" | "dot" | "comma".</summary>
    public string NumberFormat { get; set; } = "system";
}

/// <summary>Partial update DTO for the units block of POST /preferences.</summary>
public sealed class UnitsSettingsPatch
{
    public string? MonitoringTempUnit { get; set; }
    public string? TimeFormat { get; set; }
    public string? NumberFormat { get; set; }
}

/// <summary>One device's Static look: the effect plus the exact tint it was
/// assigned with. Mirrors Nexus.Service.Lighting.StaticDeviceAssignment.</summary>
public sealed class StaticDeviceLook
{
    public string Effect { get; set; } = "";
    /// <summary>Flat palette pick, "#rrggbb". Set means the look is that colour
    /// and every field below is inert.</summary>
    public string Color { get; set; } = "";
    public float Intensity { get; set; } = 1f;
    public float Hue { get; set; }
    public float Colorize { get; set; }
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
    public Dictionary<string, float> Params { get; set; } = new();
    /// <summary>Template slot the look came from. Carried for the UI's benefit -
    /// nothing in the render path reads it - so a preset can restore the pick
    /// exactly as it was made.</summary>
    public int Slot { get; set; }
}

public sealed class LightingSettings
{
    public string Sync { get; set; } = InstallDefaults.Lighting.Sync;
    public Dictionary<string, float> BrightnessScale { get; set; } = new();
    public bool BrightnessEnabled { get; set; } = InstallDefaults.Lighting.BrightnessEnabled;
    /// <summary>Master brightness cap applied to every LED channel before it
    /// leaves the RGB bridge. Caps per-device <see cref="LightingDevicePreference.Brightness"/>
    /// so the effective brightness for a given LED is <c>min(global, device / 100)</c>:
    /// a zone can never render brighter than the master level.
    /// Range 0..1; default 1.0 (no cap).</summary>
    public float GlobalBrightness { get; set; } = 1.0f;
    public Dictionary<string, int> SpeedScale { get; set; } = new();
    public bool SpeedEnabled { get; set; } = InstallDefaults.Lighting.SpeedEnabled;
    public int FrameRate { get; set; } = InstallDefaults.Lighting.FrameRate;
    public double ScaleRatio { get; set; } = InstallDefaults.Lighting.ScaleRatio;
    public Dictionary<string, DeviceLayout> DeviceLayouts { get; set; } = new();
    /// <summary>
    /// Per-device Static assignments, keyed by lighting-device id. A device
    /// listed here wears its own look in Static instead of the shared canvas.
    /// Persisted so the assignment survives a service restart - it is what the
    /// hardware is meant to show, not a UI preference.
    /// </summary>
    public Dictionary<string, StaticDeviceLook> StaticDeviceLooks { get; set; } = new();
    // Named snapshots of DeviceLayouts. Capped by the route layer.
    public List<LayoutPreset> LayoutPresets { get; set; } = new();
    // Preset the live DeviceLayouts was last loaded from; null = none selected.
    public string? ActiveLayoutPresetId { get; set; }
    public string LastMediaId { get; set; } = "";
    public AnimateSettings Animate { get; set; } = new();
    /// <summary>Static-mode selection and per-key looks. Baselines resolve from
    /// <see cref="AnimateSettings.Templates"/> - template slots are keyed by effect
    /// and shared across modes, so a fill keeps the colours it had before Static
    /// existed - while States here stays separate, letting a pattern hold a
    /// different look frozen than it does animated.</summary>
    public StaticSettings Static { get; set; } = new();
    /// <summary>Last static colour the user picked (r,g,b 0..255).</summary>
    public StaticColorSettings StaticColor { get; set; } = new();
    /// <summary>When true, BeatsProvider runs audio capture + spectrum analysis and
    /// publishes to AudioState so shaders react via the u_audio* uniforms.</summary>
    public bool MusicReactive { get; set; } = InstallDefaults.Lighting.MusicReactive;
    /// <summary>Post-process applied to the Screen Mirror frame stream (hue / colorize / saturation / contrast). Persists across sessions so the user's tweak survives a service restart.</summary>
    public PostProcessSettings ScreenEffect { get; set; } = new();
    /// <summary>Post-process applied to Media Library playback frames. Same shape as ScreenEffect but tracked independently - users typically tune media differently from screen capture.</summary>
    public PostProcessSettings MediaEffect { get; set; } = new();
    /// <summary>Which GPU renders the lighting shaders. "auto" = let the OS pick;
    /// otherwise the GpuReadout.Name of the chosen card. Keyed by name (not
    /// enumeration index) so it survives driver re-enumeration. Restart-to-apply:
    /// Windows writes the DirectX UserGpuPreferences key before the GL context
    /// inits; Linux matches it against the EGL device list. macOS ignores it.</summary>
    public string RenderGpu { get; set; } = "auto";
    /// <summary>
    /// When true, Nexus blanks every lighting device it drives as the host
    /// suspends, and restores the running effect on resume. Devices that keep
    /// their bus powered across S3 - RAM over SMBus above all - otherwise hold
    /// the last frame they were sent and stay lit through sleep. Transient at
    /// the hardware level: nothing about the active mode is persisted, so a
    /// crash or power loss while asleep comes back to the same effect.
    /// </summary>
    public bool SleepBlackout { get; set; } = InstallDefaults.Lighting.SleepBlackout;

    /// <summary>
    /// When true, Nexus fades every lighting device it drives to black when the
    /// session locks, and fades the running effect back in when it unlocks. The
    /// host stays powered throughout, so unlike <see cref="SleepBlackout"/> this
    /// fixes nothing - it is there because a machine nobody is at has no reason
    /// to be lit. Applied on lock TRANSITIONS only, never
    /// seeded from the lock state at startup, so a cold boot sitting at the
    /// login screen comes up lit.
    /// </summary>
    public bool LockBlackout { get; set; } = InstallDefaults.Lighting.LockBlackout;
}

/// <summary>
/// Canvas post-process snapshot shared by Screen Mirror and Media modes. Defaults
/// are identity (no shift, full saturation, linear contrast) so new profiles
/// render exactly what the source frame contains.
/// </summary>
public sealed class PostProcessSettings
{
    public float Hue { get; set; } = InstallDefaults.Lighting.PostProcess.Hue;
    public float Colorize { get; set; } = InstallDefaults.Lighting.PostProcess.Colorize;
    public float Saturation { get; set; } = InstallDefaults.Lighting.PostProcess.Saturation;
    public float Contrast { get; set; } = InstallDefaults.Lighting.PostProcess.Contrast;
    /// <summary>Mirror the frame horizontally before applying the colour post-process. Used by the Mirror-mode filter set.</summary>
    public bool FlipX { get; set; }
    /// <summary>Mirror the frame vertically before applying the colour post-process.</summary>
    public bool FlipY { get; set; }
    /// <summary>When true, the Reactive sub-mode replaces the standard post-process with a GPU-rendered glow driven by per-band colours extracted from the source frame.</summary>
    public bool Reactive { get; set; }
    public float Reactivity { get; set; } = InstallDefaults.Lighting.PostProcess.Reactivity;
    public float Intensity { get; set; } = InstallDefaults.Lighting.PostProcess.Intensity;
}

public sealed class StaticColorSettings
{
    public byte R { get; set; } = InstallDefaults.Lighting.StaticColor.R;
    public byte G { get; set; } = InstallDefaults.Lighting.StaticColor.G;
    public byte B { get; set; } = InstallDefaults.Lighting.StaticColor.B;
}

public sealed class AnimateSettings
{
    /// <summary>Key of the last-selected animate effect.</summary>
    public string Effect { get; set; } = InstallDefaults.Lighting.Animate.Effect;
    /// <summary>Last-activated slider state keyed by effect name, sparse: an entry
    /// exists only when the look differs from the effect's resolved selected-slot
    /// look (see <see cref="Nexus.Service.Lighting.AnimateTemplateDefaults.ResolveSelected"/>);
    /// readers resolve absent entries the same way. Switching between effects
    /// restores exactly what the user last saw without storing default looks.</summary>
    public Dictionary<string, AnimateEffectState> States { get; set; } = new();
    /// <summary>Four pre-tweaked template slots per effect plus the currently-selected
    /// index. Drives the 1/2/3/4 button row in the animate drawer. Templates[effect].Slots[Selected]
    /// mirrors States[effect] for the currently-selected template; the other slots persist
    /// across sessions so the user can round-trip between their own presets.</summary>
    public Dictionary<string, AnimateEffectTemplates> Templates { get; set; } = new();
}

public sealed class StaticSettings
{
    /// <summary>Key of the last-selected static fill or pattern.</summary>
    public string Effect { get; set; } = Nexus.Service.Lighting.StaticEffectCatalog.DefaultEffect;
    /// <summary>Sparse per-key look, same delta-from-resolved-slot rule as
    /// <see cref="AnimateSettings.States"/>. Speed is unused - static runs at 0.</summary>
    public Dictionary<string, AnimateEffectState> States { get; set; } = new();
}

public sealed class AnimateEffectState
{
    public int Speed { get; set; } = InstallDefaults.Lighting.Animate.State.Speed;
    public float Intensity { get; set; } = InstallDefaults.Lighting.Animate.State.Intensity;
    public float Hue { get; set; } = InstallDefaults.Lighting.Animate.State.Hue;
    public float Colorize { get; set; } = InstallDefaults.Lighting.Animate.State.Colorize;
    public float Saturation { get; set; } = InstallDefaults.Lighting.Animate.State.Saturation;
    public float Contrast { get; set; } = InstallDefaults.Lighting.Animate.State.Contrast;
    public Dictionary<string, float> Params { get; set; } = new();
}

public sealed class AnimateEffectTemplates
{
    /// <summary>Index of the currently-active slot, 0..3.</summary>
    public int Selected { get; set; }
    /// <summary>Preset slider states for this effect, sparse: only user-edited
    /// slots are stored. A null (or absent trailing) entry means "use the
    /// canonical default from <see cref="Nexus.Service.Lighting.AnimateTemplateDefaults"/>".
    /// Edits persist into whichever slot is currently Selected.</summary>
    public List<AnimateEffectState?> Slots { get; set; } = new();
}

public sealed class DeviceLayout
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; } = InstallDefaults.Cooling.DeviceLayoutSize.W;
    public float H { get; set; } = InstallDefaults.Cooling.DeviceLayoutSize.H;
    public int Rotation { get; set; }
}

public sealed class LayoutPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, DeviceLayout> Layouts { get; set; } = new();
    // Per-device power captured at save time: ids that are off. Null on a
    // preset saved before per-preset power existed - activate then leaves the
    // global disabled list untouched (old behavior) instead of wiping it.
    public List<string>? DisabledDevices { get; set; }
    // Per-device ignore flag captured at save time: ids Nexus does not drive.
    // Null on a preset saved before per-preset ignore state existed - activate
    // then leaves the live list untouched.
    public List<string>? UncontrolledDevices { get; set; }
    // Mode + effect selection captured at save time. Null on a preset saved
    // before per-preset looks - activate then leaves the live look untouched.
    public LightingPresetLook? Look { get; set; }
    /// <summary>Per-device Static assignments captured at save time - what each
    /// device wears instead of the shared canvas. Null on a preset saved before
    /// per-preset assignments; activate then leaves the live ones untouched.</summary>
    public Dictionary<string, StaticDeviceLook>? StaticDeviceLooks { get; set; }
    /// <summary>Per-device brightness / hue / saturation captured at save time,
    /// same null-means-untouched contract as the fields above.</summary>
    public Dictionary<string, LightingDevicePreference>? DevicePrefs { get; set; }
    /// <summary>Apps that auto-activate this preset when they take focus. Null
    /// on a preset saved before per-app activation existed; an app appears
    /// under at most one preset (the route unbinds it elsewhere on assign).</summary>
    public List<PresetAppBinding>? Apps { get; set; }
}

/// <summary>One app bound to a <see cref="LayoutPreset"/>. <see cref="ProcessName"/>
/// is the match key against the focused window's process name, resolved once at
/// bind time (a Start-menu pick carries a display name, the focus signal carries
/// a process name). Empty when it could not be resolved - the switcher then
/// falls back to comparing normalized display names.</summary>
public sealed class PresetAppBinding
{
    /// <summary>Shortcut target id from <c>GET /shortcuts</c>, or
    /// <c>proc:&lt;name&gt;</c> for an app picked off the running list.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProcessName { get; set; } = "";
}

/// <summary>Lighting selection a preset restores, captured whenever the active
/// preset is set and a mode starts.</summary>
public sealed class LightingPresetLook
{
    /// <summary><see cref="LightingSettings.Sync"/> at capture time: an animate
    /// effect key, or "static" / "screen" / "media" / "music" / "gamesync" / "none".</summary>
    public string Sync { get; set; } = "";
    public string AnimateEffect { get; set; } = "";
    public string StaticEffect { get; set; } = "";
    /// <summary>Resolved look for <see cref="AnimateEffect"/>, stored dense.
    /// <see cref="AnimateSettings.States"/> is keyed by effect and shared by every
    /// preset, so a reference would collide between two presets on one effect.</summary>
    public AnimateEffectState? AnimateState { get; set; }
    public AnimateEffectState? StaticState { get; set; }
    /// <summary>Selected template slot, or -1 when the effect has no slots.</summary>
    public int AnimateSlot { get; set; } = -1;
    public int StaticSlot { get; set; } = -1;
    /// <summary>Master brightness cap, 0..1. Null on a look captured before this
    /// was part of a preset - activate then leaves the live level alone rather
    /// than defaulting to 0 and blacking every device out.</summary>
    public float? GlobalBrightness { get; set; }
    /// <summary>Music-reactive capture toggle. Null on a look captured before it
    /// was part of a preset - activate then leaves the live setting alone.</summary>
    public bool? MusicReactive { get; set; }
    /// <summary>Media-mode clip. Without it two Media presets restore whichever
    /// clip was selected last, globally.</summary>
    public string LastMediaId { get; set; } = "";
    /// <summary>Mirror-mode post-process. Null on a look captured before this was
    /// part of a preset - activate then leaves the live filter alone.</summary>
    public PostProcessSettings? ScreenEffect { get; set; }
    /// <summary>Media-mode post-process, tracked separately from
    /// <see cref="ScreenEffect"/> the same way the live settings are.</summary>
    public PostProcessSettings? MediaEffect { get; set; }
}

public sealed class KeebSettings
{
    public string RotaryLeft { get; set; } = InstallDefaults.Keeb.RotaryLeft;
    public string RotaryRight { get; set; } = InstallDefaults.Keeb.RotaryRight;

    public KeebGameMode GameMode { get; set; } = new();
    public KeebFirmwareLighting FirmwareLighting { get; set; } = new();
    // Keyed by the GLOBAL firmware macro slot (profile*16 + panel index).
    public Dictionary<int, KeebMacroDocument> Macros { get; set; } = new();

    // Per-cell key remaps, applied over the pristine layer tables below.
    public List<KeebKeyOverride> KeyOverrides { get; set; } = new();
    // Factory 0xF2 tables captured from the device before Nexus's first write,
    // keyed "<layout>|<profile>|<layer>", value = hex of the 520-byte page
    // buffer. The base for every layer write and the reset-to-default source.
    public Dictionary<string, string> PristineLayers { get; set; } = new();
}

public sealed class KeebKeyOverride
{
    public int Profile { get; set; }
    public int Layer { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string Mode { get; set; } = "";
    public string Function { get; set; } = "";
    public int? Input { get; set; }
}

public sealed class KeebGameMode
{
    public bool AltF4 { get; set; }
    public bool AltTab { get; set; }
    public bool ShiftTab { get; set; }
    public bool WindowsKey { get; set; }
}

public sealed class KeebFirmwareLighting
{
    public string AnimationMode { get; set; } = InstallDefaults.Keeb.FirmwareLighting.AnimationMode;
    public string Speed { get; set; } = InstallDefaults.Keeb.FirmwareLighting.Speed;
    public string Direction { get; set; } = InstallDefaults.Keeb.FirmwareLighting.Direction;
    public int Brightness { get; set; } = InstallDefaults.Keeb.FirmwareLighting.Brightness;
    public bool KeyReactive { get; set; } = InstallDefaults.Keeb.FirmwareLighting.KeyReactive;
    public bool KeyReactiveMask { get; set; } = InstallDefaults.Keeb.FirmwareLighting.KeyReactiveMask;
    public string KeyReactiveMode { get; set; } = InstallDefaults.Keeb.FirmwareLighting.KeyReactiveMode;
    public RgbaColor KeyReactiveColor { get; set; } = new();
}

public sealed class KeebMacroDocument
{
    public int Index { get; set; }
    public List<KeebMacroKey> Keys { get; set; } = new();
}

public sealed class KeebMacroKey
{
    public string Key { get; set; } = "";
    public int Duration { get; set; }
    public string Type { get; set; } = "KeyDown";
}

public sealed class CoolingSettings
{
    public double GlobalSpeedModifier { get; set; } = InstallDefaults.Cooling.GlobalSpeedModifier;
    public List<CurveDocument> Curves { get; set; } = new();
    /// <summary>True once first-run preset seeding has run. Distinguishes a
    /// fresh install (seed Silent/Balanced/Turbo) from a profile the user has
    /// since emptied (leave it empty - do not resurrect the presets).</summary>
    public bool CurvesSeeded { get; set; }
    /// <summary>User-defined fan names keyed by channel ID. Only valid while the hardware mapping is unchanged.</summary>
    public Dictionary<string, string> FanNames { get; set; } = new();
    public Dictionary<string, Nexus.Service.Models.Cooling.FanCalibration> FanCalibrations { get; set; } = new();
    /// <summary>Manually-set fan duty percentages keyed by channel ID. Persisted so they survive restarts and profile switches.</summary>
    public Dictionary<string, int> ManualSpeeds { get; set; } = new();
    /// <summary>Per-channel duty offset in points, added to whatever a curve computes for that channel and clamped at the write. Absent entry means 0.</summary>
    public Dictionary<string, int> FanOffsets { get; set; } = new();
    /// <summary>Per-channel lock override keyed by channel ID. A locked channel is exempt from Silent/Balanced/Turbo/Off/Custom preset applies. An absent entry defaults to locked for pumps, unlocked otherwise; see <see cref="Nexus.Service.Cooling.FanProfiles.IsLocked"/>.</summary>
    public Dictionary<string, bool> FanLockOverrides { get; set; } = new();
    /// <summary>Channel ids Nexus stops driving entirely, so the motherboard or a vendor app owns them. Distinct from an unassigned channel, which is merely idle: this survives preset applies. Mirrors <see cref="DevicesSettings.UncontrolledLightingDevices"/>; see <see cref="Nexus.Service.Cooling.FanControlledState"/>.</summary>
    public List<string> UncontrolledFanChannels { get; set; } = new();
    /// <summary>User-assigned display/monitoring-grouping role keyed by raw channel ID: one of <see cref="Nexus.Service.Models.Cooling.FanRoleKind.Cpu"/> / <see cref="Nexus.Service.Models.Cooling.FanRoleKind.Gpu"/>. An absent entry means <see cref="Nexus.Service.Models.Cooling.FanRoleKind.None"/>. Metadata only - does not affect fan control, locking, or preset logic.</summary>
    public Dictionary<string, string> FanRoles { get; set; } = new();
    /// <summary>Active cooling preset: "off" | "silent" | "balanced" | "turbo" | "custom".</summary>
    public string ActivePreset { get; set; } = InstallDefaults.Cooling.ActivePreset;
    /// <summary>Last-known custom mapping of fan channel id -> curve id. Empty entries mean the fan was on BIOS Control. Used to restore custom assignments when leaving Silent/Balanced/Performance/Off.</summary>
    public Dictionary<string, string> CustomFanCurveAssignments { get; set; } = new();
    /// <summary>Snapshot of <see cref="ManualSpeeds"/> taken when leaving the Custom preset, keyed by channel id. Restored (and re-driven) when Custom is re-applied - the manual-fan counterpart of <see cref="CustomFanCurveAssignments"/>, and the only copy that survives the Off preset, which clears the live entries.</summary>
    public Dictionary<string, int> CustomManualSpeeds { get; set; } = new();
    /// <summary>User-saved cooling configurations, selectable from the Cooling page's preset dropdown. Distinct from <see cref="ActivePreset"/>, which is the built-in mode (off/silent/balanced/turbo/custom) the UI calls a mode.</summary>
    public List<CoolingPreset> Presets { get; set; } = new();
    /// <summary>Id of the <see cref="Presets"/> entry currently loaded, or null when none is.</summary>
    public string? ActivePresetId { get; set; }
    /// <summary>User-defined display order for fan channels in the Cooling view. Nullable so a partial POST /preferences that omits this field doesn't clobber the saved order.</summary>
    public List<string>? FanChannelOrder { get; set; }
    /// <summary>User-chosen sensor id for the CPU "temperature" reading shown across the Cooling page, Monitoring dashboard, and Cooling widget. Storage layer: null = auto (UI falls back to its default picker), non-null = pinned sensor id. The patch layer collapses an inbound empty string to null on write so the persisted JSON only ever holds null or a real id.</summary>
    public string? PreferredCpuTempSensorId { get; set; }
    /// <summary>User-chosen sensor id for the GPU "temperature" reading shown across the Cooling page, Monitoring dashboard, and Cooling widget. Same nullable semantics as <see cref="PreferredCpuTempSensorId"/>.</summary>
    public string? PreferredGpuTempSensorId { get; set; }
    /// <summary>User-chosen "primary" GPU (by model name) used wherever a single GPU's sensors are shown: the Monitoring widget, sensors/Detailed view, and the GPU temp display. Keyed by model name (not enumeration index) so the choice survives reboots / driver re-enumeration. Same nullable semantics as the temp prefs: null = auto (client defaults to the first discrete GPU), empty string on PATCH collapses to null.</summary>
    public string? PreferredGpuId { get; set; }
}

/// <summary>One user-saved cooling configuration. Holds fan-to-curve assignments rather than copies of the curves, so the curve library stays shared and editing a curve is visible to every preset that uses it.</summary>
public sealed class CoolingPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Fan channel id -> curve id, in <see cref="CoolingSettings.CustomFanCurveAssignments"/> format. A fan absent from the map falls through to BIOS control on activate.</summary>
    public Dictionary<string, string> FanCurveAssignments { get; set; } = new();
    public Dictionary<string, int> ManualSpeeds { get; set; } = new();
    public Dictionary<string, int> FanOffsets { get; set; } = new();
    public double GlobalSpeedModifier { get; set; } = InstallDefaults.Cooling.GlobalSpeedModifier;
    /// <summary>Built-in mode active when the preset was saved, applied on activate. Same value space as <see cref="CoolingSettings.ActivePreset"/>.</summary>
    public string Mode { get; set; } = "custom";
}

public sealed class CurveDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"Flat", "Linear", "Graph", "Mixed", "Trigger", "Sync", "Auto"</summary>
    public string Type { get; set; } = "Flat";
    public CurveInputDocument Input { get; set; } = new();
    public List<CurveOutputDocument> Outputs { get; set; } = new();
    public FlatCurveData? Flat { get; set; }
    public LinearCurveData? Linear { get; set; }
    public GraphCurveData? Graph { get; set; }
    public MixedCurveData? Mixed { get; set; }
    public TriggerCurveData? Trigger { get; set; }
    public SyncCurveData? Sync { get; set; }
    public AutoCurveData? Auto { get; set; }
    /// <summary>One of "silent" | "balanced" | "turbo" when this curve is the shared preset curve; null for user-authored curves. Independent of Type so a preset curve can be Linear or Graph.</summary>
    public string? Preset { get; set; }
}

public sealed class MixedCurveData
{
    public double ResponseTime { get; set; } = 1.0;
    public List<string> CurveIds { get; set; } = new();
    public string Fn { get; set; } = "max";
}

/// <summary>
/// Two-state latch: hold the current speed until the temperature crosses a
/// threshold for <see cref="ResponseTime"/> seconds, then step to that side's
/// speed. Ported from FanControl's TriggerFanCurve so an imported curve behaves
/// the same here.
/// </summary>
public sealed class TriggerCurveData
{
    /// <summary>Debounce a threshold crossing must hold for, in seconds.</summary>
    public double ResponseTime { get; set; } = 1.0;
    public double IdleTemp { get; set; }
    public double LoadTemp { get; set; }
    public double IdleSpeed { get; set; }
    public double LoadSpeed { get; set; }
}

/// <summary>
/// Mirrors another fan channel's duty. <see cref="Proportional"/> scales by
/// <c>1 + Offset/100</c>; otherwise <see cref="Offset"/> is added in points.
/// </summary>
public sealed class SyncCurveData
{
    /// <summary>Fan channel whose applied duty this curve follows.</summary>
    public string SourceChannelId { get; set; } = "";
    public double Offset { get; set; }
    public bool Proportional { get; set; }
}

/// <summary>
/// Stepping controller that holds a target temperature: ramps linearly between
/// idle and load below the load latch, and steps by <see cref="Step"/> while
/// under load. Ported from FanControl's AutoFanCurve.
/// </summary>
public sealed class AutoCurveData
{
    /// <summary>Trend window and step debounce base, in seconds.</summary>
    public double ResponseTime { get; set; } = 5.0;
    public double IdleTemp { get; set; }
    public double LoadTemp { get; set; }
    public double MinSpeed { get; set; }
    public double MaxSpeed { get; set; }
    /// <summary>Duty points added per step up; a step down is half this.</summary>
    public double Step { get; set; } = 5.0;
    /// <summary>Degrees below <see cref="LoadTemp"/> still treated as "at load".</summary>
    public double Deadband { get; set; } = 2.0;
}

public sealed class CurveInputDocument
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Device { get; set; } = "";
}

public sealed class CurveOutputDocument
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
}

public sealed class FlatCurveData
{
    public int Speed { get; set; }
}

public sealed class LinearCurveData
{
    public double ResponseTime { get; set; } = 1.0;
    public double MinTemp { get; set; }
    public double MaxTemp { get; set; }
    public double MinSpeed { get; set; }
    public double MaxSpeed { get; set; }
}

public sealed class GraphCurveData
{
    public double ResponseTime { get; set; } = 1.0;
    public double SpeedModifier { get; set; } = 1.0;
    public List<GraphPoint> Points { get; set; } = new();
}

public sealed class GraphPoint
{
    public double Temp { get; set; }
    public double Speed { get; set; }
}

public sealed class Y70Settings
{
    /// <summary>"Landscape", "Portrait", "LandscapeFlipped", "PortraitFlipped"</summary>
    public string Orientation { get; set; } = InstallDefaults.Y70.Orientation;
    public int Brightness { get; set; } = InstallDefaults.Y70.Brightness;
    public bool ScreenOff { get; set; } = InstallDefaults.Y70.ScreenOff;
    /// <summary>When true, the effective orientation applied to hardware is
    /// always PortraitFlipped regardless of <see cref="Orientation"/>.</summary>
    public bool ForceOrientation { get; set; } = InstallDefaults.Y70.ForceOrientation;
}

public sealed class QSeriesSettings
{
    /// <summary>"Portrait" or "PortraitFlipped" - the Q60/Q80 panel has no landscape mode.</summary>
    public string Orientation { get; set; } = Nexus.Service.Models.Displays.DisplayOrientations.Portrait;

    /// <summary>0-100, mapped to the 0-255 byte `settings put system screen_brightness` expects.</summary>
    public int Brightness { get; set; } = 100;

    public bool ScreenOff { get; set; }

    /// <summary>When true, the panel screen sleeps when Windows suspends (or
    /// shuts down) and wakes on resume.</summary>
    public bool SleepWithHost { get; set; } = true;

    /// <summary>When true, the panel screen sleeps while the desktop session is
    /// locked and wakes on unlock. Separate from <see cref="SleepWithHost"/>:
    /// the host stays up here, so this is about not lighting a room nobody is
    /// in, not about following the host's power state.</summary>
    public bool SleepWhenLocked { get; set; } = true;
}

/// <summary>Persisted shape of one Tryx overlay item; see
/// <see cref="Nexus.Service.Peripherals.Tryx.Panorama.TryxOverlaySensorItem"/> for the
/// runtime equivalent.</summary>
public sealed class TryxOverlaySensorItemSettings
{
    public string SensorId { get; set; } = "";
    public string Device { get; set; } = "";
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class TryxSettings
{
    public List<TryxOverlaySensorItemSettings> OverlayItems { get; set; } = new();
    public string OverlayColor { get; set; } = "#ffffff";
    /// <summary>"left", "center", or "right".</summary>
    public string OverlayAlign { get; set; } = "left";
    public string? OverlayFilter { get; set; }
    public int OverlayOpacity { get; set; } = 100;
    public string OverlayFont { get; set; } = "roboto-regular";
    public int OverlaySize { get; set; } = 100;
    /// <summary>Web-only UX state; the service never reads this for rendering.</summary>
    public bool OverlayDocked { get; set; }
    /// <summary>Last-selected preset id or custom filename, re-applied on connect.</summary>
    public string CurrentMedia { get; set; } = "";
    public bool CurrentMediaIsCustom { get; set; }
    public int Brightness { get; set; } = 100;
    /// <summary>Cloud material ids installed on the panel; drives the catalog's installed
    /// badge (the panel's media-list push reports only built-in presets, not downloads).</summary>
    public List<int> InstalledCloudIds { get; set; } = new();
}

/// <summary>One NZXT Kraken channel's firmware animation. <see cref="Colors"/> holds
/// "#rrggbb" entries in the order the animation reads them.</summary>
public sealed class KrakenFirmwareLighting
{
    public string Effect { get; set; } = "fixed";
    /// <summary>0 slowest to 4 fastest, matching the cooler's five animation speeds.</summary>
    public int Speed { get; set; } = 2;
    public bool Forward { get; set; } = true;
    public List<string> Colors { get; set; } = new();
}

public sealed class DevicesSettings
{
    public List<string> DisabledLightingDevices { get; set; } = new();
    /// <summary>Ids Nexus stops pushing frames to entirely, so firmware/vendor lighting can take over. Distinct from <see cref="DisabledLightingDevices"/>, which still streams black.</summary>
    public List<string> UncontrolledLightingDevices { get; set; } = new();
    /// <summary>Handler ids the user explicitly opted out of (Nexus Control off). Overrides the brand default; a Hyte/iBUYPOWER handler absent here stays on.</summary>
    public List<string> NexusControlDisabled { get; set; } = new();
    /// <summary>Handler ids the user explicitly opted into (Nexus Control on). Overrides the brand default; a third-party handler absent here stays off.</summary>
    public List<string> NexusControlEnabled { get; set; } = new();
    public Dictionary<string, LightingDevicePreference> LightingDevicePrefs { get; set; } = new();
    /// <summary>
    /// Firmware animation last written to each NZXT Kraken RGB channel, keyed by zone id.
    /// The cooler keeps playing it whenever Nexus is not driving that zone, but offers no
    /// way to read it back, so the last write is remembered here to seed the settings UI.
    /// </summary>
    public Dictionary<string, KrakenFirmwareLighting> KrakenFirmwareLighting { get; set; } = new();
    /// <summary>LEGACY (pre-v6, per-card key). Read only by the one-time schema migration that moves entries into <see cref="DeviceLedOverrides"/>; empty afterward. Do not write.</summary>
    public Dictionary<string, List<LedPositionOverride>> LedMapOverrides { get; set; } = new();
    /// <summary>LEGACY (pre-v6, per-card key). Migration source for <see cref="DeviceAspectRatios"/>; empty afterward. Do not write.</summary>
    public Dictionary<string, float> LedMapAspectRatios { get; set; } = new();
    /// <summary>
    /// User-defined zone partition per partitionable device, keyed by device id
    /// (keeb hub id, OpenRGB stable id). Absent key = the provider's default
    /// partition (today's cards). Slices are segment-local and must satisfy the
    /// index-stability rules in <see cref="Nexus.Service.Lighting.Zones.ZonePartitionValidator"/>.
    /// </summary>
    public Dictionary<string, List<ZoneDef>> ZonePartitions { get; set; } = new();

    /// <summary>
    /// GPU zones on the attached Strimer harness: 6 for the triple 8-pin (the default) or 4
    /// for the dual. The controller reports nothing that distinguishes them, so this is the
    /// user's choice; a wrong value stretches the LED map over a strip that is not there.
    /// </summary>
    public int StrimerGpuZones { get; set; } = Nexus.Service.Peripherals.Strimer.StrimerProtocol.GpuZoneCount;
    /// <summary>
    /// Per-LED user overrides keyed by device id, each local to a hardware
    /// segment so they survive any re-partition. Replaces the per-card
    /// <see cref="LedMapOverrides"/>.
    /// </summary>
    public Dictionary<string, List<SegmentLedOverride>> DeviceLedOverrides { get; set; } = new();
    /// <summary>Editor canvas aspect ratio per device id. Replaces the per-card <see cref="LedMapAspectRatios"/>.</summary>
    public Dictionary<string, float> DeviceAspectRatios { get; set; } = new();
    public List<MotherboardLedChannel> MotherboardLeds { get; set; } = new();
    /// <summary>
    /// User-configured LED count per motherboard ARGB zone, keyed by split device id
    /// ("openrgb-N-Z"). Applied via OpenRGB's RESIZEZONE opcode every time the device
    /// list refreshes so the choice survives subprocess bounces and service restarts.
    /// </summary>
    public Dictionary<string, int> ZoneLedCounts { get; set; } = new();
    public CnvsSettings Cnvs { get; set; } = new();
    public LianLiSettings LianLi { get; set; } = new();
    public LianLiWirelessSettings LianLiWireless { get; set; } = new();
    public LianLiLightingSettings LianLiLighting { get; set; } = new();
    public StrimerLightingSettings StrimerLighting { get; set; } = new();
    public Galahad2LightingSettings Galahad2Lighting { get; set; } = new();
    public CorsairSettings Corsair { get; set; } = new();
    /// <summary>
    /// Per-hub channel composition (mirror ports / combine rings), keyed by hub
    /// id ("lianli", "smarthub:{serial}"). Absent key = the hub's default
    /// composition. Orthogonal to <see cref="ZonePartitions"/>: composition sets
    /// the device set and each device's default partition; the user still
    /// re-zones on top.
    /// </summary>
    public Dictionary<string, HubCompositionSettings> LightingComposition { get; set; } = new();
    /// <summary>
    /// Community / file mapping applied per device, keyed by lighting-device
    /// id. The full artifact is embedded so applied mappings keep working
    /// with the registry unreachable or gone. User deltas
    /// (<see cref="LedMapOverrides"/>, <see cref="LedGroups"/>) layer on top.
    /// </summary>
    public Dictionary<string, Lighting.Mappings.AppliedMappingRef> AppliedMappings { get; set; } = new();
    /// <summary>
    /// Named LED groups per lighting-device id. Presence of a key means the
    /// user edited groups for that device (an empty list = explicitly
    /// cleared); absent = fall back to the applied mapping's groups.
    /// </summary>
    public Dictionary<string, List<Lighting.Mappings.MappingGroup>> LedGroups { get; set; } = new();
    /// <summary>Device ids where the user undid an auto-applied mapping; suppresses future auto-apply for that device.</summary>
    public List<string> MappingAutoApplyDeclined { get; set; } = new();
    /// <summary>Lighting-device ids ever seen on this install. A device not in this list is "new" and eligible for community-mapping auto-match.</summary>
    public List<string> MappingKnownDevices { get; set; } = new();
    /// <summary>When true, the SmartHub's onboard firmware animation drives the ARGB ports and Nexus stops streaming to them.</summary>
    public bool SmartHubFirmwareControl { get; set; }
    /// <summary>
    /// OpenRGB devices excluded from the bundled daemon's detection because the
    /// user turned Nexus Control off for every card they emit. Keyed by the
    /// device's OpenRGB stable id; the value snapshots identity at exclusion
    /// time so the card keeps rendering (and the exclusion can be lifted) while
    /// the hardware is deliberately no longer detected. DetectorName feeds the
    /// detector denylist + placeholder_only list in the daemon's OpenRGB.json;
    /// DeviceName is what the card shows.
    /// </summary>
    public Dictionary<string, OpenRgbDetectorExclusion> OpenRgbDetectorExclusions { get; set; } = new();

    /// <summary>Devices OpenRGB cannot auto-detect and only finds through a user registration in its own config; written into the daemon's OpenRGB.json at launch.</summary>
    public OpenRgbManualDevices OpenRgbManualDevices { get; set; } = new();
}

/// <summary>
/// Registrations the bundled daemon needs because the hardware advertises
/// nothing a detector can match on: a QMK-OpenRGB board answers only on the
/// vid/pid the user flashed, and an E1.31 device is a bare IP. OpenRGB's own
/// GUI stores the same two lists, so <see cref="Nexus.Service.Lighting.Rgb.OpenRgbConfigImport"/>
/// can adopt a user's existing config wholesale.
/// </summary>
public sealed class OpenRgbManualDevices
{
    public List<QmkOpenRgbDeviceEntry> Qmk { get; set; } = new();
    public List<E131DeviceEntry> E131 { get; set; } = new();
}

/// <summary>One QMK-OpenRGB keyboard. Ids are hex strings without a prefix, matching what OpenRGB parses with std::stoi(s, 0, 16).</summary>
public sealed class QmkOpenRgbDeviceEntry
{
    public string Name { get; set; } = "";
    public string UsbVid { get; set; } = "";
    public string UsbPid { get; set; } = "";
}

/// <summary>One E1.31 / WLED device. Field names and defaults mirror E131ControllerDetect.cpp.</summary>
public sealed class E131DeviceEntry
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int NumLeds { get; set; }
    public int StartUniverse { get; set; } = 1;
    public int StartChannel { get; set; } = 1;
    public int KeepaliveTime { get; set; }
    public int UniverseSize { get; set; } = 512;
}

/// <summary>Identity snapshot of an OpenRGB device taken when its detector was excluded.</summary>
public sealed class OpenRgbDetectorExclusion
{
    /// <summary>Name of the detector to denylist in the daemon's OpenRGB.json, resolved through its detector-map.json; not the device name wherever one detector emits several models.</summary>
    public string DetectorName { get; set; } = "";
    /// <summary>OpenRGB device name, for the synthesized card. Empty on snapshots taken before the two were told apart, where DetectorName held it.</summary>
    public string DeviceName { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Location { get; set; } = "";
    public int LedCount { get; set; }
    /// <summary>OpenRGB device type; drives the synthesized card's icon.</summary>
    public uint Type { get; set; }
}

/// <summary>One user-defined zone of a device partition: an ordered run of segment-local slices. One zone = one lighting card = one engine frame.</summary>
public sealed class ZoneDef
{
    public string Name { get; set; } = "";
    public List<ZoneSlice> Slices { get; set; } = new();
}

/// <summary>A run of LEDs local to one hardware segment of a device.</summary>
public sealed class ZoneSlice
{
    public int Segment { get; set; }
    public int Start { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Per-LED user override in the device's stable space: the index is local to
/// a hardware segment, so the entry stays valid no matter how the user
/// re-partitions the device into zones.
/// </summary>
public sealed class SegmentLedOverride
{
    public int Segment { get; set; }
    public int LedIndex { get; set; }
    public float U { get; set; }
    public float V { get; set; }
    /// <summary>Same semantics as <see cref="LedPositionOverride.Disabled"/>.</summary>
    public bool Disabled { get; set; }
}

public sealed class LedPositionOverride
{
    public int LedIndex { get; set; }
    public float U { get; set; }
    public float V { get; set; }
    /// <summary>
    /// When true, this LED is logically removed from the effect mapping: the
    /// render loop writes (0,0,0) instead of sampling the canvas. Frontends
    /// park disabled LEDs below the device frame so the user can drag them
    /// back in to re-enable without re-picking indices. U/V still round-trip
    /// so the last-known position isn't lost across disable/re-enable.
    /// </summary>
    public bool Disabled { get; set; }
}

public sealed class LightingDevicePreference
{
    public int Brightness { get; set; } = InstallDefaults.Lighting.DevicePreference.Brightness;
    public float Hue { get; set; }
    public float Saturation { get; set; } = InstallDefaults.Lighting.DevicePreference.Saturation;
    // Colour-tuning trims, applied to the frame on its way to the hardware.
    // Distinct from Hue/Saturation above, which are a smart light's own colour
    // rather than a correction: these five never change what the canvas shows.
    // Neutral defaults, and DeviceColorAdjust owns the bounds these clamp to.
    public float AdjustRed { get; set; } = 1f;
    public float AdjustGreen { get; set; } = 1f;
    public float AdjustBlue { get; set; } = 1f;
    /// <summary>Negative is cooler, positive warmer.</summary>
    public float AdjustTemperature { get; set; }
    public float AdjustSaturation { get; set; } = 1f;
}

public sealed class MotherboardLedChannel
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public sealed class CnvsSettings
{
    public bool PlayAnimation { get; set; } = InstallDefaults.Cnvs.PlayAnimation;
    public bool PlayWhenPCOff { get; set; } = InstallDefaults.Cnvs.PlayWhenPCOff;
}

public sealed class LianLiSettings
{
    public int Port0Fans { get; set; } = 4;
    public int Port1Fans { get; set; } = 4;
    public int Port2Fans { get; set; } = 4;
    public int Port3Fans { get; set; } = 4;

    public int GetFans(int port) => port switch
    {
        0 => Port0Fans,
        1 => Port1Fans,
        2 => Port2Fans,
        3 => Port3Fans,
        _ => 0,
    };

    public void SetFans(int port, int qty)
    {
        switch (port)
        {
            case 0: Port0Fans = qty; break;
            case 1: Port1Fans = qty; break;
            case 2: Port2Fans = qty; break;
            case 3: Port3Fans = qty; break;
        }
    }
}

public sealed class LianLiWirelessSettings
{
    /// <summary>Per-screen LCD content and display settings, keyed by the SL-LCD Wireless screen's 16-hex serial.</summary>
    public Dictionary<string, LianLiWirelessScreenSettings> Screens { get; set; } = new();
}

public sealed class StreamDeckSettings
{
    /// <summary>Per-deck bindings, keyed by device serial.</summary>
    public Dictionary<string, PhysicalDeckSettings> Decks { get; set; } = new();
}

/// <summary>One physical Stream Deck's persisted name, brightness, key bindings, and uploaded-image references.</summary>
public sealed class PhysicalDeckSettings
{
    public const int DefaultBrightness = 60;

    /// <summary>Empty falls back to the model name.</summary>
    public string Name { get; set; } = "";
    public int Brightness { get; set; } = DefaultBrightness;
    /// <summary>User rotation composed on top of the model's wire Transform, in quarter-turn degree steps. Applied by both the web-rendered key bitmaps and the service's own monitoring tile renders.</summary>
    public int Orientation { get; set; }
    /// <summary>Seconds of no key input before the deck blanks the display. A non-positive value disables sleep-after.</summary>
    public int SleepAfterSeconds { get; set; }
    /// <summary>Last-known StreamDeckModel.ProductId, so a disconnected deck can still report its layout via StreamDeckModels.ByProductId.</summary>
    public int ProductId { get; set; }
    public DeckConfig Deck { get; set; } = new();
    /// <summary>Keyed by "{slotPath}/{state}" (state "0" or "1" for a toggle); value is the cached image's content hash.</summary>
    public Dictionary<string, string> ImageRefs { get; set; } = new();
    // Named snapshots of Deck + ImageRefs. Capped by the route layer.
    public List<DeckPreset> Presets { get; set; } = new();
    // Preset the live Deck was last loaded from; null = none selected.
    public string? ActivePresetId { get; set; }
}

/// <summary>One named snapshot of a deck's config and uploaded-image references, for POST/activate under /streamdeck/decks/{serial}/presets.</summary>
public sealed class DeckPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DeckConfig Deck { get; set; } = new();
    public Dictionary<string, string> ImageRefs { get; set; } = new();
}

/// <summary>One SL-LCD Wireless fan screen's persisted content selection and display settings.</summary>
public sealed class LianLiWirelessScreenSettings
{
    /// <summary>"off" | "image" | "gif" | "video" | "sensor" | "clock" | "animation".</summary>
    public string ContentType { get; set; } = "off";
    public string? MediaId { get; set; }
    /// <summary>0-100.</summary>
    public byte Brightness { get; set; } = 100;
    /// <summary>0 = 0 deg, 1 = 90 deg, 2 = 180 deg, 3 = 270 deg.</summary>
    public byte Rotation { get; set; }
    /// <summary>"cpuLoad" | "cpuTemp" | "gpuLoad" | "gpuTemp" | "memoryUsage" | "vramUsage" | "fanRpm". Used when ContentType is "sensor".</summary>
    public string? SensorSource { get; set; }
    /// <summary>"ring" | "bar". Used when ContentType is "sensor".</summary>
    public string? SensorStyle { get; set; }
    /// <summary>"digital" | "digitalMinimal" | "analogClassic" | "analogMinimal". Used when ContentType is "clock".</summary>
    public string? ClockFace { get; set; }
    /// <summary>"pulse" | "spectrum" | "spin". Used when ContentType is "animation".</summary>
    public string? AnimationId { get; set; }
    /// <summary>Accent hex color "#RRGGBB": gauge fill, clock hands/digits, animation primary color.</summary>
    public string? ColorA { get; set; }
    /// <summary>Secondary hex color "#RRGGBB": gauge/clock text color, animation secondary color.</summary>
    public string? ColorB { get; set; }
    /// <summary>"c" | "f". Display unit for a temperature sensor source.</summary>
    public string? TempUnit { get; set; }
    /// <summary>User-chosen list position (0-based) so the numbered tiles match the physical fans; -1 = not set.</summary>
    public int Order { get; set; } = -1;
}

public sealed class LianLiLightingSettings
{
    public string Mode { get; set; } = "rainbowWave";
    public int Speed { get; set; } = 2;
    public int Direction { get; set; } = 0;
    public int Brightness { get; set; } = 4;
    public List<string> Colors { get; set; } = new();
}

public sealed class StrimerLightingSettings
{
    public string Mode { get; set; } = "rainbow";
    public int Speed { get; set; } = 2;
    public int Direction { get; set; } = 0;
    public int Brightness { get; set; } = 4;
    public List<string> Colors { get; set; } = new();
}

public sealed class Galahad2LightingSettings
{
    public string Mode { get; set; } = "canvas";
    public int Speed { get; set; } = 2;
    public int Direction { get; set; } = 0;
    public int Brightness { get; set; } = 4;
    public string InnerColor { get; set; } = "#FFFFFF";
    public string OuterColor { get; set; } = "#FFFFFF";
    public List<string> Colors { get; set; } = new();
}

public sealed class CorsairSettings
{
    public string? LcdSelectedMediaId { get; set; }
    /// <summary>LCD brightness 0-100. 0 = display off, 100 = maximum.</summary>
    public byte LcdBrightness { get; set; } = 100;
    /// <summary>LCD rotation: 0 = 0 deg, 1 = 90 deg, 2 = 180 deg, 3 = 270 deg.</summary>
    public byte LcdRotation { get; set; }
}

/// <summary>
/// How a multi-channel lighting hub's physical channels collapse into logical
/// devices. <see cref="Mirror"/> broadcasts one device to every active port;
/// <see cref="CombineRings"/> (ring hubs only) makes a port's inner+outer rings
/// one device (its 1-zone default partition) instead of two. Both are starting
/// points - the user re-zones each device on top.
/// </summary>
public sealed class HubCompositionSettings
{
    public bool Mirror { get; set; }
    public bool CombineRings { get; set; }
}

public sealed class AuthSettings
{
    public string Token { get; set; } = "";
    public List<PanelPhoneSessionToken> PanelPhoneSessions { get; set; } = new();
    /// <summary>
    /// Master killswitch for the Pair Remote feature. When false, any
    /// request authenticated via a phone-session cookie/bearer is rejected
    /// with 403 RemoteDisabled and every active phone-session WebSocket is
    /// closed. Paired devices remain in <see cref="PanelPhoneSessions"/> so
    /// they can resume automatically when the switch goes back on. Workstation-level.
    /// </summary>
    public bool RemoteControlEnabled { get; set; } = InstallDefaults.Auth.RemoteControlEnabled;

    /// <summary>
    /// Opt-in cloud-relay transport. When true AND
    /// <see cref="RemoteControlEnabled"/> is also true, the service holds one
    /// outbound relay socket per paired phone session so the panel can connect
    /// when both ends have internet but cannot reach each other on the LAN
    /// (hotel / client-isolated Wi-Fi). Default OFF for cost + privacy: the
    /// relay forwards opaque end-to-end-encrypted frames and never parses
    /// payloads, but holding the socket still costs bandwidth, so the user
    /// turns it on deliberately in the Pair Remote modal. Workstation-level.
    /// </summary>
    public bool RelayEnabled { get; set; }

    /// <summary>
    /// Controls whether the iOS companion app can find this host via
    /// Bonjour / mDNS over Wi-Fi. AirDrop-style three-state preference:
    /// <c>"never"</c>, <c>"always"</c> (default), or <c>"until"</c> with
    /// <see cref="PairBroadcastSettings.UntilUnixSeconds"/> set to the
    /// expiry. The QR + manual pair-code flows are unaffected.
    /// </summary>
    public PairBroadcastSettings PairBroadcast { get; set; } = new();

    /// <summary>Stored Nexus cloud accounts (register/login via /cloud/...). Refresh tokens live here; access tokens are memory-only. Workstation-level.</summary>
    public List<CloudAccountRecord> CloudAccounts { get; set; } = new();

    /// <summary>Id of the currently active cloud account, or null when logged out of all. Must match the <see cref="CloudAccountRecord.AccountId"/> of an entry in <see cref="CloudAccounts"/>.</summary>
    public string? ActiveCloudAccountId { get; set; }
}

/// <summary>
/// One stored Nexus cloud account/session on this machine. The service is the
/// token holder: <see cref="RefreshToken"/> is the only credential persisted
/// (access tokens are 15 min JWTs kept in memory only, re-derived via refresh
/// on restart). <see cref="ProfileSync"/> tracks the last known-synced state
/// per profile so a restart can resume without re-downloading unchanged data
/// or mistaking a stale local copy for a fresh edit.
/// </summary>
public sealed class CloudAccountRecord
{
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string AvatarLarge { get; set; } = "";
    public string AvatarSmall { get; set; } = "";
    public bool IsPrivate { get; set; }
    public bool EmailVerified { get; set; }
    public string RefreshToken { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string LastSyncAt { get; set; } = "";

    /// <summary>Per-profile sync bookkeeping, keyed by profileId (shared between local id and cloud id - pulled profiles adopt the cloud id).</summary>
    public Dictionary<string, CloudProfileSyncRecord> ProfileSync { get; set; } = new();
}

/// <summary>
/// Last known-synced state for one profile under one cloud account.
/// <see cref="LastSyncedHash"/> is the SHA-256 of the exported profile payload
/// at <see cref="Revision"/> - comparing it to the current local export is how
/// <c>CloudSyncDecision</c> tells "clean" (matches) from "dirty" (differs)
/// without persisting the full payload a second time.
/// </summary>
public sealed class CloudProfileSyncRecord
{
    public int Revision { get; set; }
    public string LastSyncedAt { get; set; } = "";
    public string LastSyncedHash { get; set; } = "";
}

public sealed class PairBroadcastSettings
{
    /// <summary>"never" | "always" | "until"</summary>
    public string Mode { get; set; } = "always";

    /// <summary>
    /// Expiry timestamp (Unix seconds, UTC) for the <c>"until"</c> mode.
    /// Ignored for other modes. When <c>Mode == "until"</c> and the
    /// current time exceeds this value, the broadcast is treated as off
    /// and the next setting write should revert <c>Mode</c> to <c>"never"</c>.
    /// </summary>
    public long UntilUnixSeconds { get; set; }
}

public sealed class PanelPhoneSessionToken
{
    public string Id { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// Device class frozen at claim time (e.g. "iPad", "Android tablet"). Unlike
    /// <see cref="Name"/>, it is never overwritten by a user rename, so the
    /// session list can show the original class alongside a custom name. Set from
    /// the client-detected label when present, else the UA descriptor. Empty for
    /// sessions claimed before this field existed - those fall back to the UA.
    /// </summary>
    public string DeviceType { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string DeviceFingerprint { get; set; } = "";

    /// <summary>
    /// Client-provided stable device id (a UUID the phone/app persists across
    /// re-pairings). Used to dedup authorized sessions on the relay path, where
    /// there is no usable client IP/UA and so <see cref="DeviceFingerprint"/> is
    /// empty: re-pairing the same device replaces its prior session instead of
    /// accumulating duplicates. Empty for legacy sessions and for clients that
    /// do not send one - those fall back to fingerprint-only dedup.
    /// </summary>
    public string DeviceId { get; set; } = "";
    public long CreatedAt { get; set; }
    public long LastSeenAt { get; set; }
    /// <summary>
    /// True when the session was claimed over HTTPS (native iOS app, SPKI
    /// pinned). False when claimed over plain HTTP (browser fallback). HTTP
    /// sessions get a shorter idle TTL and a hard IP+UA bind on every
    /// request; HTTPS sessions keep the legacy 30-day idle behavior because
    /// transport-level pinning already covers the threat.
    /// </summary>
    public bool ClaimedOverHttps { get; set; } = true;

    /// <summary>
    /// Base64 (standard, padded) of the relay root key for this session,
    /// derived at claim time from the transient plaintext session token via
    /// HKDF (<c>nexus-relay-root-v1</c>). The token itself is never stored
    /// (only its <see cref="Hash"/>), so this is the one place the relay
    /// client can recover the per-session E2E key without the token. The
    /// browser client re-derives the same root from the token it holds, so the
    /// relay (a dumb byte-forwarder) never sees either. Empty for sessions
    /// claimed before the relay feature shipped - those simply can't relay
    /// until they re-pair, which is the correct fail-closed behavior.
    /// </summary>
    public string RelayKey { get; set; } = "";
}

/// <summary>RGBA color used in firmware lighting + animations. Bytes for RGB, double for A (0..1).</summary>
public sealed class RgbaColor
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public double A { get; set; } = 1.0;
}

/// <summary>
/// OTA self-update preferences. Adding fields is schema-safe; no SchemaVersion bump needed.
/// </summary>
public sealed class UpdateSettings
{
    /// <summary>
    /// Auto-update behavior: "notify" (detect only), "download" (stage but don't install),
    /// "always" (download and install automatically). Default "always".
    /// </summary>
    public string UpdateMode { get; set; } = "always";

    /// <summary>"production" or "beta". Production maps to the GitHub latest-release
    /// endpoint (excludes prereleases); beta picks the newest release regardless of
    /// the prerelease flag.</summary>
    public string UpdateChannel { get; set; } = "production";

    /// <summary>
    /// Version tag the user last dismissed ("Later") the auto-update popup for.
    /// Prevents the popup from re-appearing for the same version after dismissal.
    /// </summary>
    public string LastDismissedUpdateVersion { get; set; } = "";

    /// <summary>
    /// Version string of the build that last ran. Used to detect a new-build
    /// first run (OTA or fresh install) so UpdateChannel can be derived from
    /// the build's prerelease status instead of erasing a manual channel choice
    /// on every restart.
    /// </summary>
    public string LastRunVersion { get; set; } = "";

    /// <summary>
    /// Pre-v7 field. Read during schema migration only; the v7 migration maps
    /// true to UpdateMode "notify" and then this field is dropped on the next
    /// write (WhenWritingNull).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("autoUpdateDisabled")]
    public bool? LegacyAutoUpdateDisabled { get; set; }
}
