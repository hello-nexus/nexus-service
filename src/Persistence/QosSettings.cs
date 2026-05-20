using System.Collections.Generic;
using Qos.Service.Defaults;

namespace Qos.Service.Persistence;

/// <summary>
/// Root settings document persisted to disk. Every controller that needs to remember
/// state across restarts reads/writes through IConfigStore, which mutates this object.
///
/// New fields are SAFE to add — JSON deserialization tolerates missing keys via the
/// default values on each property. Renamed or deleted fields are NOT safe; bump
/// SchemaVersion and write a migration in JsonConfigStore.Load() if you do that.
/// </summary>
public sealed class QosSettings
{
    /// <summary>
    /// Latest persisted-settings schema number. Bump in lockstep when adding
    /// a migration in <c>JsonConfigStore.Load()</c>. Lives as a constant so
    /// tests and tooling can reference "current" without bit-rotting.
    /// </summary>
    public const int CurrentSchemaVersion = 6;

    /// <summary>Persisted profile schema. v2 nests Theme/Panel/Overlay/Monitoring out of UiSettings into matching top-level POCOs that mirror install-defaults.json. v3 drops the <c>{s/n/b}</c> wrapper on per-widget config values; values are raw JSON (string/number/bool/object/array). v4 retires the type-scoped marketplace <c>Widgets</c> bag — every placement keeps its own config under <see cref="Qos.Service.Models.Panel.PanelWidgetDto.Config"/>. v5 renames the <c>performance</c> cooling preset to <c>turbo</c>. v6 splits the key-reactive overlay (<c>KeyReactive</c>, <c>KeyReactiveMask</c>, <c>KeyReactiveMode</c>, <c>KeyReactiveColor</c>) out of <c>Keeb.FirmwareLighting</c> into a separate <c>Keeb.PassiveLighting</c> block to match the two distinct firmware code paths. <see cref="JsonConfigStore"/> migrates v1/v2/v3/v4/v5 (or missing) records on load.</summary>
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
    public DevicesSettings Devices { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public ScreenTimeSettings ScreenTime { get; set; } = new();
    public ObsSettings Obs { get; set; } = new();
    public SteamSettings Steam { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();
    /// <summary>Registered panel devices keyed by opaque deviceId. Each record carries the per-device layout + theme overrides + capabilities. NOT profile-scoped: device identity is hardware-level and survives profile switches. Was previously stored under <c>Ui.PanelDevices</c>; legacy data is migrated on load.</summary>
    public Dictionary<string, Qos.Service.Models.Panel.PanelDeviceRecord> PanelDevices { get; set; } = new();

    /// <summary>User-overridden display name for this host PC. Empty means "fall back to Environment.MachineName". Surfaced in the panel tray header and in the QR/claim payload paired phones see. NOT profile-scoped: a host has one name regardless of which profile is active.</summary>
    public string HostDisplayName { get; set; } = "";

    /// <summary>Profile id designated as the source for any category currently in <c>SharedCategories</c>. When a category is shared, switching profiles still loads its values from this profile, and edits to that category save back here. NOT profile-scoped: this routing decision is workstation-level and survives profile switches. Null means no Primary; shared categories then fall back to the active profile.</summary>
    public string? PrimaryProfileId { get; set; }

    /// <summary>Category ids currently set to Shared. Allowed values: "lighting", "cooling", "theme", "dashboard". Categories not in this list are per-profile (the default). NOT profile-scoped: workstation-level. Hardware-bound state (Keeb, Y70, Devices, panel defaults) always lives at workstation root and is never per-profile, so it never appears here.</summary>
    public List<string> SharedCategories { get; set; } = new();

}

public sealed class ScreenTimeSettings
{
    /// <summary>When false, providers stop writing new focus sessions to the store. Reads of existing history continue to work.</summary>
    public bool TrackingEnabled { get; set; } = InstallDefaults.ScreenTime.TrackingEnabled;
}

public sealed class ObsSettings
{
    public string Host { get; set; } = InstallDefaults.Obs.Host;
    public int Port { get; set; } = InstallDefaults.Obs.Port;
    public string Password { get; set; } = "";
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
}

/// <summary>
/// UI-only residual state that has no install-defaults equivalent. Everything
/// that mirrors install-defaults.json now lives on QosSettings root in
/// <see cref="ThemeSettings"/>, <see cref="MonitoringSettings"/>,
/// <see cref="PanelSettings"/>, and <see cref="OverlaySettings"/>.
/// </summary>
public sealed class UiSettings
{
    public bool DisableConflictAlerts { get; set; }
    /// <summary>Legacy: panel devices used to live here, scoped per profile. Now lives at <c>QosSettings.PanelDevices</c> (top-level, hardware-scoped). Kept nullable so old settings.json / profile files deserialize cleanly; <c>JsonConfigStore.Load</c> + <c>ProfileManager.LoadProfileIntoSettings</c> migrate the entries to the top-level registry and null this out so it stops being written.</summary>
    public Dictionary<string, Qos.Service.Models.Panel.PanelDeviceRecord>? PanelDevices { get; set; }
}

/// <summary>
/// Partial update DTO for POST /preferences. Every field is nullable so the
/// handler can distinguish "client left this out" from "client explicitly sent value".
/// </summary>
public sealed class UiSettingsPatch
{
    public bool? DisableConflictAlerts { get; set; }
}

public sealed class LightingSettings
{
    public string Sync { get; set; } = InstallDefaults.Lighting.Sync;
    public Dictionary<string, float> BrightnessScale { get; set; } = new();
    public bool BrightnessEnabled { get; set; } = InstallDefaults.Lighting.BrightnessEnabled;
    /// <summary>Master multiplier applied to every LED channel before it leaves
    /// the RGB bridge. Combines with per-device <see cref="LightingDevicePreference.Brightness"/>
    /// so the effective brightness for a given LED is <c>global * device / 100</c>.
    /// Range 0..1; default 1.0 (no attenuation).</summary>
    public float GlobalBrightness { get; set; } = 1.0f;
    public Dictionary<string, int> SpeedScale { get; set; } = new();
    public bool SpeedEnabled { get; set; } = InstallDefaults.Lighting.SpeedEnabled;
    public int FrameRate { get; set; } = InstallDefaults.Lighting.FrameRate;
    public double ScaleRatio { get; set; } = InstallDefaults.Lighting.ScaleRatio;
    public Dictionary<string, DeviceLayout> DeviceLayouts { get; set; } = new();
    public string LastMediaId { get; set; } = "";
    public AnimateSettings Animate { get; set; } = new();
    /// <summary>Last static colour the user picked (r,g,b 0..255).</summary>
    public StaticColorSettings StaticColor { get; set; } = new();
    /// <summary>When true, BeatsProvider runs audio capture + spectrum analysis and
    /// publishes to AudioState so shaders react via the u_audio* uniforms.</summary>
    public bool MusicReactive { get; set; } = InstallDefaults.Lighting.MusicReactive;
    /// <summary>Post-process applied to the Screen Mirror frame stream (hue / colorize / saturation / contrast). Persists across sessions so the user's tweak survives a service restart.</summary>
    public PostProcessSettings ScreenEffect { get; set; } = new();
    /// <summary>Post-process applied to Media Library playback frames. Same shape as ScreenEffect but tracked independently - users typically tune media differently from screen capture.</summary>
    public PostProcessSettings MediaEffect { get; set; } = new();
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
    /// <summary>Full slider state keyed by effect name. Each effect remembers its own
    /// speed / hue / colorize / intensity / custom params so switching between them
    /// restores exactly what the user last saw rather than overwriting with defaults.</summary>
    public Dictionary<string, AnimateEffectState> States { get; set; } = new();
    /// <summary>Four pre-tweaked template slots per effect plus the currently-selected
    /// index. Drives the 1/2/3/4 button row in the animate drawer. Templates[effect].Slots[Selected]
    /// mirrors States[effect] for the currently-selected template; the other slots persist
    /// across sessions so the user can round-trip between their own presets.</summary>
    public Dictionary<string, AnimateEffectTemplates> Templates { get; set; } = new();
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
    /// <summary>4 preset slider states for this effect. The user can click any
    /// slot to switch, and edits persist into whichever slot is currently Selected.</summary>
    public List<AnimateEffectState> Slots { get; set; } = new();
}

public sealed class DeviceLayout
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; } = InstallDefaults.Cooling.DeviceLayoutSize.W;
    public float H { get; set; } = InstallDefaults.Cooling.DeviceLayoutSize.H;
    public int Rotation { get; set; }
}

public sealed class KeebSettings
{
    public string RotaryLeft { get; set; } = InstallDefaults.Keeb.RotaryLeft;
    public string RotaryRight { get; set; } = InstallDefaults.Keeb.RotaryRight;
    public List<KeebRotaryAppOverride> RotaryApps { get; set; } = new();
    public string RotarySensitivity { get; set; } = InstallDefaults.Keeb.RotarySensitivity;

    public KeebGameMode GameMode { get; set; } = new();
    public KeebFirmwareLighting FirmwareLighting { get; set; } = new();
    public KeebPassiveLighting PassiveLighting { get; set; } = new();
    public Dictionary<int, KeebMacroDocument> Macros { get; set; } = new();
    /// <summary>Cached snapshot of the firmware key map per layer (0..3). Source of truth is firmware; this is for offline UI rendering and as a rollback target if a hot-swap fails mid-write.</summary>
    public Dictionary<int, KeebLayerSnapshot> Layers { get; set; } = new();
}

public sealed class KeebRotaryAppOverride
{
    public string TargetId { get; set; } = "";
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
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
    public bool KeyIndicator { get; set; }
}

/// <summary>Key-reactive overlay rendered alongside (or as a mask over) the firmware lighting effect. Separate code path on the device, so it's split from <see cref="KeebFirmwareLighting"/> here too.</summary>
public sealed class KeebPassiveLighting
{
    public bool KeyReactive { get; set; } = InstallDefaults.Keeb.PassiveLighting.KeyReactive;
    public bool KeyReactiveMask { get; set; } = InstallDefaults.Keeb.PassiveLighting.KeyReactiveMask;
    public string KeyReactiveMode { get; set; } = InstallDefaults.Keeb.PassiveLighting.KeyReactiveMode;
    public RgbaColor KeyReactiveColor { get; set; } = new() { R = 255, G = 255, B = 255, A = 255 };
}

/// <summary>Per-layer cached snapshot of the firmware key map. Indexed by `[row][col]` matching the UI layout's physical key positions; each entry mirrors a <see cref="Qos.Service.Models.Peripherals.Keeb.KeebKey"/> wire shape.</summary>
public sealed class KeebLayerSnapshot
{
    public List<List<KeebLayerKey>> Keys { get; set; } = new();
}

public sealed class KeebLayerKey
{
    public string Mode { get; set; } = "";
    public string Function { get; set; } = "";
    public int? Input { get; set; }
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
    public string Category { get; set; } = "";
    public bool Meta { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
}

public sealed class CoolingSettings
{
    public double GlobalSpeedModifier { get; set; } = InstallDefaults.Cooling.GlobalSpeedModifier;
    public List<CurveDocument> Curves { get; set; } = new();
    public MiniHubLayout MiniHubLayout { get; set; } = new();
    /// <summary>User-defined fan names keyed by channel ID. Only valid while the hardware mapping is unchanged.</summary>
    public Dictionary<string, string> FanNames { get; set; } = new();
    public Dictionary<string, Qos.Service.Models.Cooling.FanCalibration> FanCalibrations { get; set; } = new();
    /// <summary>Manually-set fan duty percentages keyed by channel ID. Persisted so they survive restarts and profile switches.</summary>
    public Dictionary<string, int> ManualSpeeds { get; set; } = new();
    /// <summary>Active cooling preset: "off" | "silent" | "balanced" | "turbo" | "custom". "custom" lets existing installs upgrade cleanly.</summary>
    public string ActivePreset { get; set; } = InstallDefaults.Cooling.ActivePreset;
    /// <summary>Last-known custom mapping of fan channel id -> curve id. Empty entries mean the fan was on BIOS Control. Used to restore custom assignments when leaving Silent/Balanced/Performance/Off.</summary>
    public Dictionary<string, string> CustomFanCurveAssignments { get; set; } = new();
    /// <summary>User-defined display order for fan channels in the Cooling view. Nullable so a partial POST /preferences that omits this field doesn't clobber the saved order.</summary>
    public List<string>? FanChannelOrder { get; set; }
    /// <summary>User-chosen sensor id for the CPU "temperature" reading shown across the Cooling page, Monitoring dashboard, and Cooling widget. Storage layer: null = auto (UI falls back to its default picker), non-null = pinned sensor id. The patch layer collapses an inbound empty string to null on write so the persisted JSON only ever holds null or a real id.</summary>
    public string? PreferredCpuTempSensorId { get; set; }
    /// <summary>User-chosen sensor id for the GPU "temperature" reading shown across the Cooling page, Monitoring dashboard, and Cooling widget. Same nullable semantics as <see cref="PreferredCpuTempSensorId"/>.</summary>
    public string? PreferredGpuTempSensorId { get; set; }
}

public sealed class CurveDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"Flat", "Linear", "Graph", "Mixed"</summary>
    public string Type { get; set; } = "Flat";
    public CurveInputDocument Input { get; set; } = new();
    public List<CurveOutputDocument> Outputs { get; set; } = new();
    public FlatCurveData? Flat { get; set; }
    public LinearCurveData? Linear { get; set; }
    public GraphCurveData? Graph { get; set; }
    public MixedCurveData? Mixed { get; set; }
    /// <summary>One of "silent" | "balanced" | "turbo" when this curve is the shared preset curve; null for user-authored curves. Independent of Type so a preset curve can be Linear or Graph.</summary>
    public string? Preset { get; set; }
}

public sealed class MixedCurveData
{
    public double ResponseTime { get; set; } = 1.0;
    public List<string> CurveIds { get; set; } = new();
    public string Fn { get; set; } = "max";
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

public sealed class MiniHubLayout
{
    public int Port1 { get; set; }
    public int Port2 { get; set; }
    public int Port3 { get; set; }
    public int Port4 { get; set; }
}

public sealed class Y70Settings
{
    /// <summary>"Landscape", "Portrait", "LandscapeFlipped", "PortraitFlipped"</summary>
    public string Orientation { get; set; } = InstallDefaults.Y70.Orientation;
    public int Brightness { get; set; } = InstallDefaults.Y70.Brightness;
    public bool ScreenOff { get; set; } = InstallDefaults.Y70.ScreenOff;
}

public sealed class DevicesSettings
{
    public List<string> DisabledLightingDevices { get; set; } = new();
    public Dictionary<string, LightingDevicePreference> LightingDevicePrefs { get; set; } = new();
    public Dictionary<string, List<LedPositionOverride>> LedMapOverrides { get; set; } = new();
    public Dictionary<string, float> LedMapAspectRatios { get; set; } = new();
    public List<MotherboardLedChannel> MotherboardLeds { get; set; } = new();
    /// <summary>
    /// User-configured LED count per motherboard ARGB zone, keyed by split device id
    /// ("openrgb-N-Z"). Applied via OpenRGB's RESIZEZONE opcode every time the device
    /// list refreshes so the choice survives subprocess bounces and service restarts.
    /// </summary>
    public Dictionary<string, int> ZoneLedCounts { get; set; } = new();
    public CnvsSettings Cnvs { get; set; } = new();
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
    /// Controls whether the iOS companion app can find this host via
    /// Bonjour / mDNS over Wi-Fi. AirDrop-style three-state preference:
    /// <c>"never"</c>, <c>"always"</c> (default), or <c>"until"</c> with
    /// <see cref="PairBroadcastSettings.UntilUnixSeconds"/> set to the
    /// expiry. The QR + manual pair-code flows are unaffected.
    /// </summary>
    public PairBroadcastSettings PairBroadcast { get; set; } = new();
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
    public string UserAgent { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string DeviceFingerprint { get; set; } = "";
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
}

/// <summary>RGBA color used in firmware lighting + animations. Bytes for RGB, double for A (0..1).</summary>
public sealed class RgbaColor
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public double A { get; set; } = 1.0;
}
