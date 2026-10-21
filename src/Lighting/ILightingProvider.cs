using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Lighting;

/// <summary>
/// Aggregate read/write surface for the /lighting/* family. The stub backs
/// state with IConfigStore so the SPA can round-trip every property even
/// without a real RGB engine wired in.
/// </summary>
public interface ILightingProvider
{
    string GetSync();
    void SetSync(string sync);
    void StopAll();

    /// <summary>
    /// Suspend the live engine and OpenRGB bridge without touching the
    /// persisted Sync mode, so a later re-enable can replay it. Used by the
    /// Lighting feature toggle; StopAll's Sync="none" persist would erase
    /// what to restore.
    /// </summary>
    void Suspend();

    /// <summary>True when the active effect's rendered frame is frozen.</summary>
    bool IsPaused { get; }

    /// <summary>
    /// Freezes or resumes the active effect's rendered frame. A no-op when no
    /// effect is running. Transient - cleared by SetEffect/Stop, never persisted.
    /// </summary>
    void SetPaused(bool paused);
    void SetBrightness(BrightnessScale scale);
    void SetSpeed(SpeedScale scale);

    AnimateOptions GetAnimateOptions();
    AudioSyncOptions GetAudioSyncOptions();
    ScreenSyncOptions GetScreenSyncOptions();

    void StartAnimate(AnimateHeadlessStart body);

    /// <summary>Runs a catalog effect at speed 0 as a held still frame.</summary>
    void StartStatic(StaticHeadlessStart body);
    void StartMusic(MusicHeadlessStart body);
    void StartScreen(ScreenHeadlessStart body);
    void ReselectScreen();
    bool StartMedia(string mediaId);

    /// <summary>
    /// With <paramref name="keepMode"/> false this is <see cref="StartMedia(string)"/>.
    /// True changes the one shared media item and leaves the global mode alone
    /// (global Media, when active, switches to the new item too).
    /// </summary>
    bool StartMedia(string mediaId, bool keepMode);
    void StartMediaIdle();

    /// <summary>Update the Mirror post-process (hue / colorize / saturation / contrast + flip X/Y + reactive). Persists to settings when persist=true.</summary>
    void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist, bool reactive = false, float reactivity = 0.5f, float intensity = 0.5f);
    /// <summary>Update the Media post-process. Same semantics as UpdateScreenEffect.</summary>
    void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist);

    /// <summary>
    /// Render the given animate effect's preset <paramref name="slot"/> to a
    /// 160x90 BMP for UI previews (presets are universal, so the same render
    /// serves every surface). Returns the BMP bytes plus a content tag (a hash
    /// of the slot's saved look) for the HTTP ETag; the render is re-cached
    /// whenever that tag changes, so an edit is never served stale.
    /// </summary>
    (byte[] Bytes, string Tag)? CaptureAnimateThumbnail(string key, int slot, bool skipCache = false, bool frozen = false);

    /// <summary>
    /// Persist the universal preset templates and, if the saved change altered
    /// the slot currently driving the LEDs, push the new look to the running
    /// shader in place (so the hardware follows a commit from any surface).
    /// </summary>
    void SaveAnimateTemplates(System.Collections.Generic.Dictionary<string, Nexus.Service.Persistence.AnimateEffectTemplates> templates);

    /// <summary>Persist the MusicReactive flag and reconcile audio capture against the active effect.</summary>
    void SetMusicReactive(bool enabled);

    /// <summary>Start or stop audio capture based on the current MusicReactive setting and active effect.</summary>
    void ReconcileAudioCapture();

    /// <summary>
    /// Hold (or release) a demand for audio capture on behalf of a client that
    /// renders an audio-reactive shader itself, so capture does not depend on
    /// the LED engine also running an audio effect.
    /// </summary>
    void SetAudioCaptureDemand(bool demanded);

    /// <summary>
    /// A custom effect's source changed: drop its cached thumbnails and, when it
    /// is the running animate effect, rebuild it live from the new source.
    /// </summary>
    void ReloadCustomEffect(string key) { }

    /// <summary>
    /// A custom effect was deleted: drop its cached thumbnails and saved looks,
    /// and move the running animate effect to rainbow if it was the one.
    /// </summary>
    void DropCustomEffect(string key) { }

    /// <summary>
    /// Shader Studio live preview: paint <paramref name="body"/>'s fragment over every device until
    /// <see cref="StopStudioLive"/> or the lease (LightingProvider.StudioLeaseMs) lapses. It is an engine overlay
    /// and touches no setting. The caller has already validated and compile-checked the fragment.
    /// </summary>
    StudioLiveResult StartStudioLive(StudioLiveBody body) => new(StudioLiveOutcome.Failed, "studio live is not supported", "");

    /// <summary>Ends a live session. A <paramref name="sessionId"/> that is not the running session is a no-op. True when a session was ended.</summary>
    bool StopStudioLive(string? sessionId = null) => false;

    /// <summary>True while a live session runs.</summary>
    bool StudioLiveActive => false;
}

public enum StudioLiveOutcome
{
    Ok,
    /// <summary>Refused: the Lighting feature is off, or a fragment the shader could not be built from.</summary>
    Failed,
    /// <summary>The session the caller names is over (lease lapse, another tab).</summary>
    Ended,
}

/// <summary>Outcome of <see cref="ILightingProvider.StartStudioLive"/>.</summary>
public readonly record struct StudioLiveResult(StudioLiveOutcome Outcome, string Message, string SessionId, bool LightingOff = false)
{
    public bool Ok => Outcome == StudioLiveOutcome.Ok;
}
