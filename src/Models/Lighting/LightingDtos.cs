using System.Collections.Generic;
using Nexus.Service.Models.Common;

namespace Nexus.Service.Models.Lighting;

public sealed class AudioStateSnapshot
{
    public float Level { get; set; }
    public float Bass { get; set; }
    public float Mid { get; set; }
    public float High { get; set; }
    public float Beat { get; set; }
    public List<float> Spectrum { get; set; } = new();
    public List<float> Spectrum64 { get; set; } = new();
}

public sealed class ShaderSourceResponse
{
    public string Frag { get; set; } = "";
    /// <summary>The sensor uniforms the source declares with their authored default bindings; empty for an effect with none.</summary>
    public List<SensorUniformInfo> Sensors { get; set; } = new();
}

/// <summary>One sensor uniform of a shader: its GLSL name and the binding the author picked.</summary>
public sealed class SensorUniformInfo
{
    public string Uniform { get; set; } = "";
    public Nexus.Service.Persistence.SensorBinding Binding { get; set; } = new();
}

public class CurrentSyncResponse : ApiResponse
{
    /// <summary>One of: none, animate, music, screen, gif.</summary>
    public string Sync { get; set; } = "none";
    public bool Paused { get; set; }

    /// <summary>True while a Shader Studio live session drives the LEDs. Sync and Paused then report the state before the session, which is what a client should keep and save.</summary>
    public bool StudioLive { get; set; }

    /// <summary>True on an install upgraded from a version that had Game Sync, which is gone. Drives a one-release notice.</summary>
    public bool GameSyncRemovedNotice { get; set; }
}

/// <summary>POST body for /lighting/pause. Freezes the active effect's rendered
/// frame in place; transient, never persisted to settings.</summary>
public sealed class LightingPauseBody
{
    public bool Paused { get; set; }
}

/// <summary>Response for /lighting/pause: the paused state after the request,
/// which may differ from the requested value when there was no active effect.</summary>
public sealed class LightingPauseResponse
{
    public bool Paused { get; set; }
}

public class BrightnessScale
{
    public Dictionary<string, float> Scale { get; set; } = new();
    public bool Enabled { get; set; }
}

/// <summary>Master brightness cap applied to every LED channel before it leaves
/// the RGB bridge: a device never renders brighter than this. 0..1 (UI slider is
/// 0..100% and divides client-side). Both the /lighting/global-brightness GET
/// response and POST body share this shape.</summary>
public class GlobalBrightnessBody
{
    public float Value { get; set; } = 1.0f;
}

/// <summary>GET response / POST body for /lighting/brightness-schedule: the
/// time-of-day cap on master brightness. Points are whole hours 0..23 holding
/// 0..100%. The GET also carries the out-of-box curve so the editor's reset
/// needs no second copy of it; a POST leaves it null. SunShift swaps the curve
/// for one generated from today's sunrise and sunset. On a POST an absent
/// SunShift or Location keeps the stored value and a null Location selects the
/// IP location; <see cref="LocationSpecified"/> tells the two nulls apart.</summary>
public sealed class BrightnessScheduleBody
{
    private SunLocationDto? _location;

    public bool Enabled { get; set; }
    public List<Nexus.Service.Persistence.BrightnessSchedulePoint> Points { get; set; } = new();
    public List<Nexus.Service.Persistence.BrightnessSchedulePoint>? Defaults { get; set; }
    public bool? SunShift { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public SunLocationDto? Location
    {
        get => _location;
        set
        {
            _location = value;
            LocationSpecified = true;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool LocationSpecified { get; private set; }

    public BrightnessScheduleSun? Sun { get; set; }
}

/// <summary>Today's sun times for the schedule's place, PC-local minutes since
/// midnight. Status is ok, polar-day, polar-night or no-location; SunPoints is
/// the generated curve and is empty unless the status is ok.</summary>
public sealed class BrightnessScheduleSun
{
    public string Status { get; set; } = "no-location";
    public string Date { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public double? SunriseMin { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public double? SunsetMin { get; set; }

    public List<BrightnessSunPoint> SunPoints { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public SunLocationDto? ResolvedLocation { get; set; }

    /// <summary>The IP-resolved place whenever known, whichever place is chosen.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public SunLocationDto? AutoLocation { get; set; }
}

/// <summary>A place on the schedule's wire. Lat and Lon are nullable so a POST that omits one can be refused instead of reading as 0.</summary>
public sealed class SunLocationDto
{
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string? Label { get; set; }
    public string? Cc { get; set; }
}

public sealed class BrightnessSunPoint
{
    public double Min { get; set; }
    public double Brightness { get; set; }
}

/// <summary>POST body for /lighting/idle-dim. TimeoutSeconds 0 means "when my
/// screen turns off"; otherwise within the controller's timeout range. Level is a percent, clamped.</summary>
public sealed class IdleDimBody
{
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; }
    public int Level { get; set; } = Nexus.Service.Persistence.IdleDimSettings.DefaultLevel;
}

/// <summary>GET response for /lighting/idle-dim. OsScreenOffSeconds is the OS
/// screen-off timeout for the active power source: 0 is Never, null unreadable.</summary>
public sealed class IdleDimStatus
{
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; }
    public int Level { get; set; }
    public bool Supported { get; set; }
    public bool ScreenOffSupported { get; set; }
    public int? OsScreenOffSeconds { get; set; }
}

/// <summary>GET response / POST body for /lighting/render-gpu. "auto" or a GPU
/// model name (matches GpuReadout.Name). Restart-to-apply.</summary>
public class RenderGpuBody
{
    public string Value { get; set; } = "auto";
}

/// <summary>GET response for /lighting/render-gpu.</summary>
public class RenderGpuInfo
{
    public string Value { get; set; } = "auto";
    /// <summary>True when "software" (CPU rendering) is a valid Value, i.e. the ANGLE backend is in use.</summary>
    public bool SoftwareRenderingAvailable { get; set; }
}

public class SpeedScale
{
    public Dictionary<string, int> Scale { get; set; } = new();
    public bool Enabled { get; set; }
}

/// <summary>
/// Toggle for the Music Reactive mode. When true, BeatsProvider is started
/// (audio capture + spectrum analysis), so every shader that reads the
/// u_audio* uniforms animates to the music. When false, capture stops and
/// AudioState is zeroed so shaders run their idle animations.
/// </summary>
public sealed class MusicReactiveBody
{
    public bool Enabled { get; set; }
}

/// <summary>GET/POST body for /lighting/audio-output: the endpoint audio-reactive
/// capture listens to. Empty id follows the system default of the direction.</summary>
public sealed class AudioOutputBody
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    /// <summary>"output" (loopback of a render endpoint) or "input" (microphone / line-in). Null or empty means "output".</summary>
    public string? Direction { get; set; }
}

/// <summary>GET/POST body for /lighting/sleep-blackout: blank lighting while
/// the host sleeps.</summary>
public sealed class SleepBlackoutBody
{
    public bool Enabled { get; set; }
}

/// <summary>GET/POST body for /lighting/lock-blackout: blank lighting while
/// the session is locked.</summary>
public sealed class LockBlackoutBody
{
    public bool Enabled { get; set; }
}

/// <summary>Replaces the persisted AnimateSettings.Templates dictionary in one shot.
/// The frontend holds the authoritative set of per-effect templates + selected slot
/// and posts the whole map every time the user clicks a slot or edits one. Backend
/// is a dumb store here - it never constructs templates itself.</summary>
public class SetAnimateTemplatesBody
{
    public Dictionary<string, Nexus.Service.Persistence.AnimateEffectTemplates> Templates { get; set; } = new();
}

public class AnimateHeadlessStart
{
    public string Filter { get; set; } = "";
    public float Intensity { get; set; }
    public string Effect { get; set; } = "";
    public int Speed { get; set; }
    public float Noise { get; set; }
    public List<RGBA> Scheme { get; set; } = new();
    public float Hue { get; set; }
    public float Sat { get; set; }
    /// <summary>0 = pure hue shifter, 1 = pure grayscale colorizer, blended in between.</summary>
    public float Colorize { get; set; }
    /// <summary>0 = grayscale, 1 = unchanged, 2 = oversaturated.</summary>
    public float Saturation { get; set; } = 1f;
    /// <summary>0 = flat middle gray, 1 = unchanged, 2 = hard contrast.</summary>
    public float Contrast { get; set; } = 1f;
    /// <summary>Per-effect uniforms keyed by GLSL name (e.g. u_zoom, u_warp).</summary>
    public List<ShaderParam> Params { get; set; } = new();
    /// <summary>Per sensor uniform, the user's pick over the shader's default binding. Null or absent keeps the defaults.</summary>
    public Dictionary<string, Nexus.Service.Persistence.SensorBinding>? Sensors { get; set; }
    /// <summary>
    /// False while the user is actively dragging a slider -- updates the live
    /// engine uniforms but skips the settings.json write. True (default) on
    /// slider release or explicit mode change, which also persists the state.
    /// </summary>
    public bool Persist { get; set; } = true;
}

public class ShaderParam
{
    public string Name { get; set; } = "";
    public float Value { get; set; }
}

/// <summary>
/// POST body for /lighting/static/headless-start. Same look controls as
/// <see cref="AnimateHeadlessStart"/> minus speed: the shader runs at speed 0,
/// so its output is a still frame the engine renders once and holds.
/// </summary>
public class StaticHeadlessStart
{
    public string Effect { get; set; } = "";
    /// <summary>"#rrggbb". When set the global static output is exactly that flat color for every unpinned device and the fields below are inert. Empty keeps the effect look.</summary>
    public string? Color { get; set; }
    public float Intensity { get; set; }
    public float Hue { get; set; }
    public float Colorize { get; set; }
    /// <summary>0 = grayscale, 1 = unchanged, 2 = oversaturated.</summary>
    public float Saturation { get; set; } = 1f;
    /// <summary>0 = flat middle gray, 1 = unchanged, 2 = hard contrast.</summary>
    public float Contrast { get; set; } = 1f;
    public List<ShaderParam> Params { get; set; } = new();
    /// <summary>False while the user drags a slider: updates the live uniforms but skips the settings write.</summary>
    public bool Persist { get; set; } = true;
}

public class MusicHeadlessStart
{
    public string Effect { get; set; } = "CircleRamp";
    public string Source { get; set; } = "default";
}

public class ScreenHeadlessStart
{
    /// <summary>Screen to mirror. Null keeps the persisted choice (a restore or Deck start); "" selects the default screen.</summary>
    public string? Monitor { get; set; }
    public string Effect { get; set; } = "";
    public float Saturation { get; set; }
    public float Contrast { get; set; }
    public float Blur { get; set; }
    /// <summary>0..1 hue rotation fraction. Maps to the same palette-ring hue the animate mode uses.</summary>
    public float Hue { get; set; }
    /// <summary>0 = pure hue rotate, 1 = grayscale + accent-tint. Matches the animate colorize semantics.</summary>
    public float Colorize { get; set; }
    /// <summary>True applies the monitor to the shared capture without switching the global mode (unless it is already Mirror).</summary>
    public bool KeepMode { get; set; }
}

/// <summary>
/// Post-process params applied to Screen Mirror and Media frames after capture /
/// playback. Shared shape so the right-pane Effect tab can drive either mode
/// with one control set.
/// </summary>
public sealed class PostProcessBody
{
    public float Hue { get; set; }
    public float Colorize { get; set; }
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    /// <summary>False while the user drags a slider. True on release or programmatic change.</summary>
    public bool Persist { get; set; } = true;
    public bool Reactive { get; set; }
    public float Reactivity { get; set; } = 0.5f;
    public float Intensity { get; set; } = 0.5f;
}

// On-connect WS payloads
public class AnimateOptions
{
    public string[] Effects { get; set; } = System.Array.Empty<string>();
    public string[] Filters { get; set; } = System.Array.Empty<string>();
}

public class AudioSyncOptions
{
    public string[] Effects { get; set; } = System.Array.Empty<string>();
    public string[] Sources { get; set; } = System.Array.Empty<string>();
}

public class ScreenSyncMonitor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ScreenSyncOptions
{
    public string[] Effects { get; set; } = System.Array.Empty<string>();
    public List<ScreenSyncMonitor> Monitors { get; set; } = new();
    /// <summary>The persisted screen the global Mirror and every mirror pin capture; empty is the default screen.</summary>
    public string SelectedMonitor { get; set; } = "";

    /// <summary>
    /// How the user chooses which screen to mirror. "app" - the client picks a
    /// monitor from <see cref="Monitors"/> (Windows/macOS, DXGI/AVFoundation).
    /// "system" - the OS screen picker chooses (Linux/Wayland portal); the client
    /// shows a "Change screen" action that re-opens that picker instead of a list.
    /// </summary>
    public string SelectionMode { get; set; } = "app";
}
