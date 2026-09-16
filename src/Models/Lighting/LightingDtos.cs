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
}

public class CurrentSyncResponse : ApiResponse
{
    /// <summary>One of: none, animate, music, screen, gif.</summary>
    public string Sync { get; set; } = "none";
    public bool Paused { get; set; }
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

/// <summary>GET response / POST body for /lighting/render-gpu. "auto" or a GPU
/// model name (matches GpuReadout.Name). Restart-to-apply.</summary>
public class RenderGpuBody
{
    public string Value { get; set; } = "auto";
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
    public string Monitor { get; set; } = "";
    public string Effect { get; set; } = "";
    public float Saturation { get; set; }
    public float Contrast { get; set; }
    public float Blur { get; set; }
    /// <summary>0..1 hue rotation fraction. Maps to the same palette-ring hue the animate mode uses.</summary>
    public float Hue { get; set; }
    /// <summary>0 = pure hue rotate, 1 = grayscale + accent-tint. Matches the animate colorize semantics.</summary>
    public float Colorize { get; set; }
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

/// <summary>
/// Per-device Chroma frame posted by the native shim to
/// /lighting/game-sync/frame. One POST per device per rendered frame.
///
/// effect: CHROMA_NONE | CHROMA_STATIC | CHROMA_CUSTOM | CHROMA_CUSTOM2 |
///         CHROMA_CUSTOM_KEY
/// colors: packed COLORREF values (0x00BBGGRR). For CHROMA_STATIC exactly
///         one entry. For CHROMA_CUSTOM rows*cols entries, row-major. Empty
///         for CHROMA_NONE.
/// rows/cols: grid dimensions implied by the effect but carried explicitly so
///            the receiver does not need to infer them from effect alone.
/// </summary>
public sealed class GameSyncFrameBody
{
    /// <summary>Device type string from the shim: keyboard | mouse | mousepad | headset | keypad | chromalink.</summary>
    public string Device { get; set; } = "";
    /// <summary>Chroma effect name as decoded by the shim.</summary>
    public string Effect { get; set; } = "";
    /// <summary>Grid row count (1 for non-grid effects).</summary>
    public int Rows { get; set; }
    /// <summary>Grid column count (1 for STATIC, 0 for NONE).</summary>
    public int Cols { get; set; }
    /// <summary>COLORREF values, row-major. Each entry is 0x00BBGGRR.</summary>
    public int[] Colors { get; set; } = System.Array.Empty<int>();
    /// <summary>Source application title reported by the shim. Empty when the game calls Init() without InitSDK().</summary>
    public string App { get; set; } = "";
}

/// <summary>One device entry in the Game Sync state response.</summary>
public sealed class GameSyncDeviceInfo
{
    public string Name { get; set; } = "";
    /// <summary>Archetype from DeviceFrame: keyboard/mouse/mousepad/headset/keypad/chromalink, or "ambient" when the frame has no archetype (strips, fans, RAM).</summary>
    public string Archetype { get; set; } = "";
    public int LedCount { get; set; }
}

public sealed class GameSyncStateResponse
{
    /// <summary>True when Game Sync is the current lighting mode.</summary>
    public bool Active { get; set; }

    /// <summary>Both Chroma shim pairs are installed in System32/SysWOW64 and are ours.</summary>
    public bool ProviderInstalled { get; set; }

    /// <summary>A real Razer Chroma SDK DLL was found; our shim was not installed.</summary>
    public bool SynapseConflict { get; set; }

    /// <summary>The user set a vendor SDK aside (<c>*.nexus-bak</c>) so our shim holds its slot.</summary>
    public bool VendorOverride { get; set; }

    public List<GameSyncDeviceInfo> Devices { get; set; } = new();

    /// <summary>Unix epoch milliseconds of the most recently ingested Chroma frame. Null when no frame has been received this session.</summary>
    public long? LastFrameAt { get; set; }

    /// <summary>Source application title from the most recent shim frame that carried one. Null when unknown.</summary>
    public string? ActiveApp { get; set; }
}

public sealed class GameSyncVendorOverrideBody
{
    public bool Enabled { get; set; }
}

public sealed class DetectedGame
{
    public string Name { get; set; } = "";
    public string Store { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public string AppId { get; set; } = "";
    public bool EmitsChroma { get; set; }
    public bool EmitsGsi { get; set; }
    public int ScannedFiles { get; set; }
    public int SkippedFiles { get; set; }
}

/// <summary>On-disk mirror of the last completed scan. Machine-local cache, not profile state.</summary>
public sealed class GameSyncScanCache
{
    public long ScannedAt { get; set; }
    public List<DetectedGame> Games { get; set; } = new();
}

public sealed class GameSyncGamesResponse
{
    public bool Scanning { get; set; }
    public long? ScannedAt { get; set; }
    public List<DetectedGame> Games { get; set; } = new();
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

    /// <summary>
    /// How the user chooses which screen to mirror. "app" - the client picks a
    /// monitor from <see cref="Monitors"/> (Windows/macOS, DXGI/AVFoundation).
    /// "system" - the OS screen picker chooses (Linux/Wayland portal); the client
    /// shows a "Change screen" action that re-opens that picker instead of a list.
    /// </summary>
    public string SelectionMode { get; set; } = "app";
}
