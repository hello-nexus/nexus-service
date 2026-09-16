using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.GameSync;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Capture;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Rendering;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting;

/// <summary>
/// Real lighting provider. Wraps the cross-platform LightingEngine and bridges
/// engine frames to the LightingOutputHub WebSocket so connected SPAs see live
/// colors. State is mirrored to disk via IConfigStore so the SPA can reload and
/// know what was running.
///
/// Each /lighting/{name}/headless-start route maps to one IEffect implementation.
/// New effect modes plug in by adding an interface method, a route, and a Start*
/// implementation that calls _engine.SetEffect.
/// </summary>
public sealed class LightingProvider : ILightingProvider, IDisposable
{
    /// <summary>Cap on the stop-path OpenRGB push. Bounds the per-device queueing only: a socket send already in flight does not observe cancellation, so a daemon that stops draining can still overrun this.</summary>
    private static readonly TimeSpan StopBlackoutBudget = TimeSpan.FromSeconds(2);

    /// <summary>Budget for the engine to publish the black frame, shared with the suspend path.</summary>
    private static readonly TimeSpan EnginePublishBudget = SleepBlackoutCoordinator.EnginePublishBudget;

    /// <summary>
    /// Grace for OpenRGB to apply the black before the subprocess is killed.
    /// UpdateLEDs only raises CallFlag_UpdateLEDs; DeviceUpdateLEDs - the SMBus
    /// transaction - runs on a per-controller thread polling at 1ms
    /// (openrgb-headless RGBController.cpp:2131) and the SDK carries no
    /// completion ack, so there is no signal to wait on and the kill is the only
    /// lever. Sized for two ENE DRAM modules behind the SMBus mutex, which is
    /// the slowest controller here; NEXUS_STOP_SETTLE_MS overrides it for bench
    /// measurement.
    /// </summary>
    private static TimeSpan SettleWindow =>
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_STOP_SETTLE_MS"), out var ms) && ms >= 0
            ? TimeSpan.FromMilliseconds(Math.Min(ms, 5000))
            : TimeSpan.FromMilliseconds(300);

    private readonly IConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly LightingOutputHub _hub;
    private readonly RgbBridge? _rgb;
    private readonly GpuContext _gpu;
    private readonly MediaLibrary _media;
    private readonly IMonitorEnumerator _monitors;
    private readonly IScreenFrameSource? _frameSource;
    private readonly GameSyncGameScanner? _scanner;
    private readonly IBeatsProvider? _beats;

    // Serializes audio-capture reconcile so two near-simultaneous effect
    // transitions can't interleave the read of the live effect with the
    // start/stop; the last reconcile to acquire reads the committed effect
    // and wins.
    private readonly object _audioCaptureLock = new();
    // Refcount of clients rendering an audio shader themselves; see SetAudioCaptureDemand.
    private int _audioCaptureDemand;

    // Live-reactive post-process holders shared between the effect and the
    // /lighting/{mode}/effect endpoint. The endpoint mutates the fields; the
    // effect reads them each frame. Kept here so values survive across
    // start/stop cycles - the first frame after a mode restart uses whatever
    // the user had previously set.
    private readonly PostProcessState _screenPP = new();
    private readonly PostProcessState _mediaPP = new();

    // Shared GameSyncEffect instance. Kept alive across StartGameSync calls so
    // frames that arrive while the effect is already running are not dropped.
    private GameSyncEffect? _gameSyncEffect;

    private readonly FeatureGates _gates;

    public LightingProvider(IConfigStore store, LightingEngine engine, LightingOutputHub hub, GpuContext gpu, MediaLibrary media, IMonitorEnumerator monitors, IScreenFrameSource? frameSource = null, RgbBridge? rgb = null, GameSyncGameScanner? scanner = null, IBeatsProvider? beats = null, FeatureGates? gates = null)
    {
        _gates = gates ?? FeatureGates.AllEnabled;
        _store = store;
        _engine = engine;
        // The engine renders per-device Static assignments but must not know how
        // to build a shader; hand it the same builder StartStatic uses.
        _engine.StaticEffectFactory = a => BuildAnimateEffect(
            a.Effect, 0f, a.Intensity, a.Hue, a.Colorize, a.Saturation, a.Contrast,
            a.Params is null ? null : new System.Collections.Generic.Dictionary<string, float>(a.Params));
        _hub = hub;
        _rgb = rgb;
        _gpu = gpu;
        _media = media;
        _monitors = monitors;
        _frameSource = frameSource;
        _scanner = scanner;
        _beats = beats;

        var s = _store.Load().Lighting;
        _screenPP.Set(s.ScreenEffect.Hue, s.ScreenEffect.Colorize, s.ScreenEffect.Saturation, s.ScreenEffect.Contrast, s.ScreenEffect.FlipX, s.ScreenEffect.FlipY, s.ScreenEffect.Reactive, s.ScreenEffect.Reactivity, s.ScreenEffect.Intensity);
        _mediaPP.Set(s.MediaEffect.Hue, s.MediaEffect.Colorize, s.MediaEffect.Saturation, s.MediaEffect.Contrast, s.MediaEffect.FlipX, s.MediaEffect.FlipY);

        _engine.OnFrame += frame => _ = _hub.BroadcastBinaryAsync(frame);
        _engine.OnEffectChanged += ReconcileAudioCapture;
        _scanner?.OnScanComplete = OnGameScanComplete;
    }

    /// <summary>
    /// Bring the RGB hardware bridge online whenever a real effect starts. The
    /// bridge is null on platforms without an OpenRGB binary (macOS / Linux),
    /// in which case the call is a no-op.
    /// </summary>
    // Every mode start routes through here, so clearing the Static ownership
    // flag in one place means a new mode can never inherit per-device colours
    // (locked looks excepted - the tracker keeps honouring those);
    // StartStatic re-asserts it immediately after.
    private void EnsureRgbActive()
    {
        _engine.StaticEffects?.Enabled = false;
        _rgb?.Activate();
    }

    // A running effect reports itself, except in Static: there the engine name is
    // the catalog shader driving the held frame, while the mode is what callers
    // classify on.
    public string GetSync()
    {
        if (_engine.CurrentEffectName == "none")
        {
            return _store.Load().Lighting.Sync;
        }
        return _engine.Frozen ? "static" : _engine.CurrentEffectName;
    }

    public void SetSync(string sync) => _store.Update(s => s.Lighting.Sync = sync);

    public bool IsPaused => _engine.Paused;

    public void SetPaused(bool paused) => _engine.SetPaused(paused);

    // Audio capture runs while Music Reactive is on and the live engine effect
    // is audio-reactive, OR while a client is watching the "audio" topic. The
    // effect is read inside the lock (not from a captured argument) so
    // concurrent transitions resolve to the last committed effect; the engine
    // is already "none" during StopAll, so capture stops without consulting the
    // not-yet-persisted Sync.
    public void ReconcileAudioCapture()
    {
        if (_beats is null)
        {
            return;
        }
        lock (_audioCaptureLock)
        {
            var forLeds = _store.Load().Lighting.MusicReactive && ShaderLibrary.IsAudioEffect(_engine.CurrentEffectName);
            if (forLeds || Volatile.Read(ref _audioCaptureDemand) > 0)
            {
                _beats.Start();
            }
            else
            {
                _beats.Stop();
            }
        }
    }

    /// <summary>Hold capture for a client rendering an audio shader itself; without it capture is gated on the LED engine running an audio effect.</summary>
    public void SetAudioCaptureDemand(bool demanded)
    {
        if (demanded)
        {
            Interlocked.Increment(ref _audioCaptureDemand);
        }
        else if (Interlocked.Decrement(ref _audioCaptureDemand) < 0)
        {
            // An unmatched release must not drive the count negative; that would suppress every later demand.
            Interlocked.Exchange(ref _audioCaptureDemand, 0);
        }
        ReconcileAudioCapture();
    }

    public void SetMusicReactive(bool enabled)
    {
        _store.Update(s => s.Lighting.MusicReactive = enabled);
        ReconcileAudioCapture();
    }

    public void StopAll()
    {
        // Before Stop: the render loop is what publishes the black frame, and
        // every writer needs that frame to blank its own hardware.
        BlackoutBeforeRelinquish();
        _engine.Stop();
        _store.Update(s =>
        {
            s.Lighting.Sync = "none";
            LightingPresetLooks.CaptureIntoActive(s);
        });
        _rgb?.Deactivate();
        _rgb?.AwaitShutdown();
    }

    /// <summary>
    /// Drives the final black to hardware before <c>Deactivate</c> hard-kills
    /// OpenRGB, mirroring the sleep path: hold the engine at level 0 so the
    /// still-running loop publishes black to every writer, then push the
    /// OpenRGB devices directly and await each write. Bench-measured on ENE
    /// DRAM over SMBus: publishing through the loop is what makes the black
    /// stick - the same push issued after <c>Stop</c> does not.
    /// </summary>
    private void BlackoutBeforeRelinquish()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Not gated on the bridge: the loop-published frame is what blanks the
        // NP50 / Lian Li / Keeb / AW5 writers, which exist without OpenRGB.
        _engine.SetBlackout(true);
        var published = _engine.WaitForBlackout(EnginePublishBudget);
        var publishedMs = sw.ElapsedMilliseconds;

        var pushed = "n/a";
        var pushedCount = 0;
        if (_rgb is not null)
        {
            using var cts = new CancellationTokenSource(StopBlackoutBudget);
            try
            {
                pushedCount = _rgb.BlackoutAsync(cts.Token).GetAwaiter().GetResult();
                pushed = pushedCount + " device(s)";
            }
            catch (OperationCanceledException) { pushed = "timeout"; }
            catch (Exception ex) { pushed = $"failed:{ex.GetType().Name}"; }
        }
        var pushedMs = sw.ElapsedMilliseconds - publishedMs;

        // Nothing reached the wire means Deactivate has nothing to kill either, so
        // the wait would only block the caller's request thread.
        var settle = pushedCount > 0 ? SettleWindow : TimeSpan.Zero;
        if (settle > TimeSpan.Zero)
        {
            Thread.Sleep(settle);
        }
        ServiceLog.Info(
            $"[lighting-stop] blackout engine={(published ? "published" : "timeout")}/{publishedMs}ms " +
            $"openrgb={pushed}/{pushedMs}ms settle={(int)settle.TotalMilliseconds}ms total={sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Tear down without blacking out here: the only caller
    /// (FeatureReconciler's Lighting ON-&gt;OFF transition) runs
    /// SleepBlackoutCoordinator.BlankOutForFeatureOff first, while the loop is
    /// still publishing. Repeating it after <c>Stop</c> would push into a
    /// torn-down loop and leave the hold latched with no release path.
    /// </summary>
    public void Suspend()
    {
        _engine.Stop();
        _rgb?.Deactivate();
        _rgb?.AwaitShutdown();
    }

    public void SetBrightness(BrightnessScale scale) => _store.Update(s =>
    {
        s.Lighting.BrightnessScale = scale.Scale;
        s.Lighting.BrightnessEnabled = scale.Enabled;
    });

    public void SetSpeed(SpeedScale scale) => _store.Update(s =>
    {
        s.Lighting.SpeedScale = scale.Scale;
        s.Lighting.SpeedEnabled = scale.Enabled;
    });

    public AnimateOptions GetAnimateOptions() => new()
    {
        Effects = new[] { "rainbow", "pulse", "wave" },
        Filters = new[] { "none" },
    };

    public AudioSyncOptions GetAudioSyncOptions() => new()
    {
        Effects = new[] { "circleramp" },
        Sources = new[] { "default" },
    };

    public ScreenSyncOptions GetScreenSyncOptions() => new()
    {
        Effects = new[] { "average" },
        Monitors = _monitors.Enumerate(),
        // On Linux the Wayland portal owns screen selection (the app can't pick a
        // monitor for the user), so the client offers a "Change screen" action
        // that re-opens the system picker rather than a monitor dropdown.
        SelectionMode = OperatingSystem.IsLinux() ? "system" : "app",
    };

    public void StartAnimate(AnimateHeadlessStart body)
    {
        if (!_gates.Lighting)
        {
            return;
        }
        EnsureRgbActive();
        var name = (body.Effect ?? "rainbow").ToLowerInvariant();
        // The static catalog lives in Static mode now. Callers that predate it -
        // restored settings written before the split, Stream Deck rgbEffect
        // Deck rgbEffect buttons, MCP scenarios, set_static_color - still name one
        // here, and must
        // land in Static so the UI reflects where the look actually lives.
        if (StaticEffectCatalog.Contains(name))
        {
            StartStatic(new StaticHeadlessStart
            {
                Effect = name,
                Intensity = body.Intensity,
                Hue = body.Hue,
                Colorize = body.Colorize,
                Saturation = body.Saturation,
                Contrast = body.Contrast,
                Params = body.Params,
                Persist = body.Persist,
            });
            return;
        }
        // Speed is bipolar: negative values run the effect in reverse. 50 = 1x
        // forward, -50 = 1x reverse, 100 = 2x forward, 0 = frozen.
        var speed = (float)(body.Speed / 50.0);
        var intensity = body.Intensity > 0 ? body.Intensity : 1f;
        var hue = body.Hue;
        var colorize = body.Colorize;
        // Do NOT coerce 0 to 1 - saturation=0 (grayscale) and contrast=0
        // (flat mid-gray) are valid user-selected states. The DTO
        // already defaults to 1 when the field is absent from the payload,
        // so trust the value straight through and let the shader clamp.
        var saturation = body.Saturation;
        var contrast = body.Contrast;
        var extras = ParamsToDict(body.Params);
        var effectSpeed = name == "pulse" ? speed * 0.5f : speed;

        // Fast path: if the currently running effect is the same shader, just
        // update its uniforms in place. Creating a new ShaderEffect every
        // slider drag leaks ~86KB of readback/flip buffers per cycle until GC
        // catches up, and re-triggers a full shader compile. In-place updates
        // are zero-alloc after the dict allocation for extras.
        if (_engine.CurrentEffect is ShaderEffect cur && cur.Name == name)
        {
            cur.Speed = effectSpeed;
            cur.Intensity = intensity;
            cur.Hue = hue;
            cur.Colorize = colorize;
            cur.Saturation = saturation;
            cur.Contrast = contrast;
            cur.ExtraParams = extras;
            // Reusing the running shader skips SetEffect, which is what clears
            // the hold. Leaving it set after a static->animate switch freezes
            // the animation on its first frame and keeps GetSync reporting
            // static, so the UI snaps back.
            _engine.SetFrozen(false);
        }
        else
        {
            // BuildAnimateEffect applies its own pulse scaling, so pass the
            // pre-scaling value here to avoid scaling twice.
            _engine.SetEffect(BuildAnimateEffect(name, speed, intensity, hue, colorize, saturation, contrast, extras));
        }
        // Skip the settings write while the user is still dragging a slider.
        // The UI sends Persist=false during drag (updates go to the engine
        // in-place above) and Persist=true on release, which is the only
        // moment we need to hit disk.
        if (!body.Persist)
        {
            return;
        }
        var incoming = new Nexus.Service.Persistence.AnimateEffectState
        {
            Speed = body.Speed,
            Intensity = intensity,
            Hue = hue,
            Colorize = colorize,
            Saturation = saturation,
            Contrast = contrast,
            Params = extras is not null
                ? new System.Collections.Generic.Dictionary<string, float>(extras)
                : new(),
        };
        _store.Update(s =>
        {
            s.Lighting.Sync = name;
            s.Lighting.Animate.Effect = name;
            // States holds only deltas from the effect's selected preset look:
            // merely activating an effect (the UI replays the resolved slot
            // verbatim) must not grow settings.json by a full dense state.
            // Other effects' saved states are untouched so switching back
            // restores exactly what the user last set for each one.
            var baseline = AnimateTemplateDefaults.ResolveSelected(s.Lighting.Animate.Templates, name);
            // incoming.Intensity was coerced (<= 0 becomes 1) above; compare
            // against the same coercion of the baseline or a slot saved at
            // intensity 0 could never match and would pin a dense entry.
            if (baseline is not null && baseline.Intensity <= 0)
            {
                baseline = new Nexus.Service.Persistence.AnimateEffectState
                {
                    Speed = baseline.Speed,
                    Intensity = 1f,
                    Hue = baseline.Hue,
                    Colorize = baseline.Colorize,
                    Saturation = baseline.Saturation,
                    Contrast = baseline.Contrast,
                    Params = baseline.Params,
                };
            }
            if (baseline is not null && AnimateTemplateDefaults.StateEquals(incoming, baseline))
            {
                s.Lighting.Animate.States.Remove(name);
            }
            else
            {
                s.Lighting.Animate.States[name] = incoming;
            }
            LightingPresetLooks.CaptureIntoActive(s);
        });
    }

    public void StartStatic(StaticHeadlessStart body)
    {
        if (!_gates.Lighting)
        {
            return;
        }
        EnsureRgbActive();
        _engine.StaticEffects?.Enabled = true;
        var name = (body.Effect ?? "").ToLowerInvariant();
        if (!StaticEffectCatalog.Contains(name))
        {
            // Coercing a wrong key to a fill hid caller bugs behind a 200 and
            // persisted the wrong selection; an empty key still gets the default.
            if (name.Length > 0)
            {
                throw new System.ArgumentException($"'{name}' is not a static effect", nameof(body));
            }
            name = StaticEffectCatalog.DefaultEffect;
        }
        var intensity = body.Intensity > 0 ? body.Intensity : 1f;
        var extras = ParamsToDict(body.Params);

        // Speed 0 for the fills, which share the animate tint pipeline; the
        // patterns read no clock at all.
        if (_engine.CurrentEffect is ShaderEffect cur && cur.Name == name)
        {
            cur.Speed = 0f;
            cur.Intensity = intensity;
            cur.Hue = body.Hue;
            cur.Colorize = body.Colorize;
            cur.Saturation = body.Saturation;
            cur.Contrast = body.Contrast;
            cur.ExtraParams = extras;
        }
        else
        {
            _engine.SetEffect(BuildAnimateEffect(name, 0f, intensity, body.Hue, body.Colorize, body.Saturation, body.Contrast, extras));
        }
        _engine.SetFrozen(true);

        if (!body.Persist)
        {
            return;
        }
        var incoming = new Nexus.Service.Persistence.AnimateEffectState
        {
            Speed = 0,
            Intensity = intensity,
            Hue = body.Hue,
            Colorize = body.Colorize,
            Saturation = body.Saturation,
            Contrast = body.Contrast,
            Params = extras is not null
                ? new System.Collections.Generic.Dictionary<string, float>(extras)
                : new(),
        };
        _store.Update(s =>
        {
            s.Lighting.Sync = "static";
            s.Lighting.Static.Effect = name;
            var slot = AnimateTemplateDefaults.ResolveSelected(s.Lighting.Animate.Templates, name);
            // The slot carries an animate speed and may carry intensity 0; both
            // are coerced here so an untouched look still compares equal and
            // stays out of settings.json.
            var baseline = slot is null ? null : new Nexus.Service.Persistence.AnimateEffectState
            {
                Speed = 0,
                Intensity = slot.Intensity <= 0 ? 1f : slot.Intensity,
                Hue = slot.Hue,
                Colorize = slot.Colorize,
                Saturation = slot.Saturation,
                Contrast = slot.Contrast,
                Params = slot.Params,
            };
            if (baseline is not null && AnimateTemplateDefaults.StateEquals(incoming, baseline))
            {
                s.Lighting.Static.States.Remove(name);
            }
            else
            {
                s.Lighting.Static.States[name] = incoming;
            }
            LightingPresetLooks.CaptureIntoActive(s);
        });
    }

    private static System.Collections.Generic.Dictionary<string, float>? ParamsToDict(List<ShaderParam>? list)
    {
        if (list is null || list.Count == 0)
        {
            return null;
        }
        var d = new System.Collections.Generic.Dictionary<string, float>(list.Count);
        foreach (var p in list)
        {
            if (!string.IsNullOrEmpty(p.Name))
            {
                d[p.Name] = p.Value;
            }
        }
        return d;
    }

    private ShaderEffect MakeShader(string name, string src, float speed, float intensity, float hue, float colorize, float saturation, float contrast, System.Collections.Generic.Dictionary<string, float>? extras) =>
        new ShaderEffect(name, _gpu, src)
        {
            Speed = speed,
            Intensity = intensity,
            Hue = hue,
            Colorize = colorize,
            Saturation = saturation,
            Contrast = contrast,
            ExtraParams = extras,
        };

    // Cached thumbnail BMP per (effect, slot), stamped with a content tag derived
    // from the saved look it was rendered for. Presets are universal, so this is
    // bounded to 4 slots x effect count. A request re-renders whenever the slot's
    // tag differs from the cached one - so an edit is never served stale.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Tag, byte[] Bytes)> _thumbnailCache = new();

    // A thumbnail is a single frame captured this far into the animation. The
    // shaders are stateless - output is a pure function of u_time - so one
    // render at a settled time matches stepping there frame by frame, while
    // t=0 would leave ramp-up effects (fire, starfield, matrix) cold or black.
    private const double ThumbnailFrameTimeMs = 957.0;

    public (byte[] Bytes, string Tag)? CaptureAnimateThumbnail(string key, int slot, bool skipCache = false, bool frozen = false)
    {
        // Shader thumbnails are GPU-rendered; with no usable GPU the render is a
        // no-op and the frame stays black, so skip it and let the caller 404 -
        // the UI shows a placeholder instead of a grid of black tiles.
        if (!_gpu.Available)
        {
            return null;
        }
        var name = (key ?? "").ToLowerInvariant();
        if (!ShaderLibrary.AllEffectKeys.Contains(name))
        {
            return null;
        }
        var defaults = ExtraParamDefaults(name);
        // Render the requested preset slot's effective look (user delta or
        // canonical default). Tag the cache by that look (not the client's ?v
        // token), so a surface that keeps requesting the same token still
        // re-renders once the slot's saved look changes.
        var state = ResolveSlotLook(name, slot);
        var tag = (state is null ? "sig" : HashSlot(state)) + (frozen ? ":f" : "");
        var cacheKey = name + ":" + slot + (frozen ? ":f" : "");
        if (!skipCache && _thumbnailCache.TryGetValue(cacheKey, out var cached) && cached.Tag == tag)
        {
            return (cached.Bytes, tag);
        }

        var canvas = new Engine.CanvasBuffer(160, 90);
        var effect = BuildThumbnailEffect(name, defaults, state, frozen);
        try
        {
            effect.RenderFrame(canvas, ThumbnailFrameTimeMs);
        }
        finally
        {
            try
            { effect.Dispose(); }
            catch { }
        }
        var bytes = BmpEncoder.Encode(canvas.Pixels, canvas.Width, canvas.Height);
        _thumbnailCache[cacheKey] = (tag, bytes);
        return (bytes, tag);
    }

    /// <summary>
    /// Effective look for an effect's preset slot: the stored user delta when
    /// present, else the canonical default bundle slot. Null when the effect is
    /// unknown to both the store and the defaults table (render the signature
    /// look). Slot index is clamped to the 4-slot bundle.
    /// </summary>
    private Nexus.Service.Persistence.AnimateEffectState? ResolveSlotLook(string name, int slot)
        => AnimateTemplateDefaults.ResolveSlot(_store.Load().Lighting.Animate.Templates, name, slot);

    /// <summary>
    /// Persist the universal preset templates, pruned to user deltas: slots
    /// equal to their canonical default are dropped and readers resolve them
    /// back through <see cref="AnimateTemplateDefaults"/>. If the change
    /// altered the slot currently driving the LEDs, push the new look to the
    /// running shader in place so the hardware follows the edit (commit-time,
    /// from any surface). Edits to other slots/effects leave the live LEDs
    /// untouched.
    /// </summary>
    public void SaveAnimateTemplates(System.Collections.Generic.Dictionary<string, Nexus.Service.Persistence.AnimateEffectTemplates> templates)
    {
        var before = ActiveLookTag();
        var incoming = templates ?? new();
        foreach (var (effect, bundle) in incoming)
        {
            if (bundle?.Slots is null)
            {
                continue;
            }
            foreach (var slot in bundle.Slots)
            {
                if (slot is not null)
                {
                    ShaderParamSpec.Sanitize(effect, slot);
                }
            }
        }
        var pruned = AnimateTemplateDefaults.Prune(incoming);
        _store.Update(s =>
        {
            s.Lighting.Animate.Templates = pruned;
            // Templates carry the per-effect selected slot, so a slot edit moves
            // the live look; without this the active preset keeps the pre-edit
            // snapshot and re-activating it reverts the user's edit.
            LightingPresetLooks.CaptureIntoActive(s);
        });
        if (before != ActiveLookTag())
        {
            ReapplyActiveLook();
        }
    }

    /// <summary>Content tag of the slot currently driving the LEDs, or null when no animate effect is active.</summary>
    private string? ActiveLookTag()
    {
        var a = _store.Load().Lighting.Animate;
        var effect = (a.Effect ?? "").ToLowerInvariant();
        var selected = a.Templates.TryGetValue(effect, out var b) ? b.Selected : 0;
        var slot = ResolveSlotLook(effect, selected);
        return slot is null ? null : HashSlot(slot);
    }

    /// <summary>
    /// Re-push the active effect's selected-slot uniforms onto the running shader
    /// in place (no restart, no flicker). Mirrors the StartAnimate fast path.
    /// </summary>
    private void ReapplyActiveLook()
    {
        var a = _store.Load().Lighting.Animate;
        var effect = (a.Effect ?? "").ToLowerInvariant();
        if (_engine.CurrentEffect is not ShaderEffect cur || cur.Name != effect)
        {
            return;
        }
        var selected = a.Templates.TryGetValue(effect, out var b) ? b.Selected : 0;
        var slot = ResolveSlotLook(effect, selected);
        if (slot is null)
        {
            return;
        }
        var speed = (float)(slot.Speed / 50.0);
        cur.Speed = effect == "pulse" ? speed * 0.5f : speed;
        cur.Intensity = slot.Intensity > 0 ? slot.Intensity : 1f;
        cur.Hue = slot.Hue;
        cur.Colorize = slot.Colorize;
        cur.Saturation = slot.Saturation;
        cur.Contrast = slot.Contrast;
        cur.ExtraParams = slot.Params is not null && slot.Params.Count > 0
            ? new System.Collections.Generic.Dictionary<string, float>(slot.Params)
            : null;
    }

    /// <summary>Stable content tag over a slot's render-affecting fields (FNV-1a, base16).</summary>
    private static string HashSlot(Nexus.Service.Persistence.AnimateEffectState slot)
    {
        static long R(float f) => (long)Math.Round(f * 1000f);
        var sb = new System.Text.StringBuilder();
        sb.Append(slot.Speed).Append(',').Append(R(slot.Intensity)).Append(',')
          .Append(R(slot.Hue)).Append(',').Append(R(slot.Colorize)).Append(',')
          .Append(R(slot.Saturation)).Append(',').Append(R(slot.Contrast));
        if (slot.Params is not null)
        {
            var keys = new System.Collections.Generic.List<string>(slot.Params.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            foreach (var k in keys)
            {
                sb.Append(';').Append(k).Append('=').Append(R(slot.Params[k]));
            }
        }
        uint h = 2166136261;
        foreach (var c in sb.ToString())
        {
            h ^= c;
            h *= 16777619;
        }
        return h.ToString("x8");
    }

    /// <summary>
    /// Build the effect whose single frame becomes the thumbnail. Renders the
    /// saved selected-slot look when one exists (so the preview matches what
    /// playing the effect shows), else the signature look. Both paths mirror the
    /// live <see cref="StartAnimate"/> uniform mapping (speed/50, params over defaults).
    /// </summary>
    private IEffect BuildThumbnailEffect(string name, System.Collections.Generic.Dictionary<string, float> defaults, Nexus.Service.Persistence.AnimateEffectState? slot, bool frozen = false)
    {
        if (slot is not null)
        {
            var extras = new System.Collections.Generic.Dictionary<string, float>(defaults);
            if (slot.Params is not null)
            {
                foreach (var kv in slot.Params)
                {
                    extras[kv.Key] = kv.Value;
                }
            }
            return BuildAnimateEffect(name, frozen ? 0f : slot.Speed / 50f, slot.Intensity, slot.Hue, slot.Colorize, slot.Saturation, slot.Contrast, extras);
        }

        // Signature hue + colorize + speed give the iconic look (fire orange,
        // matrix green, nebula purple) instead of a generic rainbow; the
        // signature table mirrors SIGNATURES in
        // nexus-web/src/types/lightingTemplates.ts.
        var sig = SignatureFor(name);
        // Static mode renders at speed 0, so its tile must too or the grid
        // advertises a frame the LEDs never show.
        var thumbSpeed = frozen ? 0f : sig.Speed / 50f;
        if (name == "pulse")
        {
            thumbSpeed *= 0.5f;
        }
        return BuildAnimateEffect(name, thumbSpeed, sig.Intensity, sig.Hue, sig.Colorize, sig.Saturation, sig.Contrast, defaults);
    }

    /// <summary>
    /// Signature colour / speed / sat / contrast for each effect's slot 0.
    /// Drives the thumbnail render so the grid shows the iconic look of every
    /// effect. Mirrors the SIGNATURES table in lightingTemplates.ts; if the
    /// two ever drift the thumbnail will stop matching the drawer.
    /// </summary>
    private readonly record struct Signature(float Hue, float Colorize, float Speed, float Saturation, float Contrast, float Intensity);

    private static Signature SignatureFor(string name) => name switch
    {
        // Simple solid-colour fills. Slot 0 of the simple-fill feels in
        // lightingTemplates.ts: colorize is unused (the shader owns the tint)
        // and saturation is HSV S (1 = full colour, 0 = white). The fill is
        // static, so speed is irrelevant to the thumbnail.
        "simplewhite"     => new(0.00f, 0.00f, 50f, 0.00f, 1.00f, 1f),
        "simplesoftpink"  => new(0.94f, 0.00f, 50f, 0.40f, 1.00f, 1f),
        "simplepink"      => new(0.92f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "simplered"       => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "simpleorange"    => new(0.07f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "simpleyellow"    => new(0.14f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "simplegreen"     => new(0.33f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "simpledarkgreen" => new(0.35f, 0.00f, 50f, 1.00f, 1.00f, 0.55f),
        "simplecyan"      => new(0.50f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "simpleblue"      => new(0.62f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "simpleviolet"    => new(0.75f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        // Static patterns paint their own colours; a neutral signature keeps the
        // tint post-process from recolouring the thumbnail.
        "gradientlinear" or "gradientradial" or "gradienttri" or "gradientconic"
            or "mirror" or "corners"
            or "splitsharp" or "stripes" or "checker" or "border"
            or "rings" or "dots" or "wedges"
            or "spectrumramp" or "spectrumbands" or "huewheel"
            => new(0.00f, 0.00f, 0f, 1.00f, 1.00f, 1f),
        "rainbow" => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "sharplines" => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "fire" => new(0.03f, 0.80f, 70f, 1.10f, 1.05f, 1f),
        "plasma" => new(0.85f, 0.30f, 60f, 1.00f, 1.00f, 1f),
        "spiral" => new(0.00f, 0.00f, 55f, 1.00f, 1.00f, 1f),
        "matrix" => new(0.33f, 0.75f, 80f, 1.10f, 1.10f, 1f),
        "meteor" => new(0.10f, 0.40f, 85f, 1.05f, 1.00f, 1f),
        "ripple" => new(0.55f, 0.30f, 55f, 1.00f, 1.00f, 1f),
        "wave" => new(0.58f, 0.45f, 50f, 1.00f, 1.00f, 1f),
        "gradientwave" => new(0.00f, 0.00f, 40f, 1.00f, 1.00f, 1f),
        "ball" => new(0.12f, 0.25f, 60f, 1.05f, 1.00f, 1f),
        "radar" => new(0.33f, 0.60f, 55f, 1.05f, 1.05f, 1f),
        "pulse" => new(0.55f, 0.45f, 60f, 1.00f, 1.00f, 1f),
        "watercolor" => new(0.50f, 0.30f, 40f, 1.00f, 1.00f, 1f),
        "jellyfish" => new(0.48f, 0.35f, 45f, 1.00f, 1.00f, 1f),
        "aurora" => new(0.33f, 0.35f, 45f, 1.05f, 1.00f, 1f),
        "lavalamp" => new(0.85f, 0.50f, 35f, 1.00f, 1.00f, 1f),
        "starfield" => new(0.60f, 0.25f, 55f, 1.00f, 1.00f, 1f),
        "voronoi" => new(0.40f, 0.50f, 40f, 1.05f, 1.00f, 1f),
        "neonrain" => new(0.88f, 0.50f, 85f, 1.10f, 1.05f, 1f),
        "nebula" => new(0.72f, 0.35f, 30f, 1.00f, 1.00f, 1f),
        "bursts" => new(0.08f, 0.50f, 65f, 1.05f, 1.00f, 1f),
        "lavafissure" => new(0.03f, 0.45f, 40f, 1.05f, 1.05f, 1f),
        "kaleidoscope" => new(0.00f, 0.00f, 55f, 1.00f, 1.00f, 1f),
        "wormhole" => new(0.78f, 0.40f, 70f, 1.00f, 1.00f, 1f),
        "interference" => new(0.60f, 0.55f, 65f, 1.25f, 1.15f, 1f),
        "sacredgeometry" => new(0.80f, 0.30f, 50f, 1.00f, 1.00f, 1f),
        "tessellation" => new(0.55f, 0.45f, 55f, 1.00f, 1.00f, 1f),
        "domainwarp" => new(0.70f, 0.50f, 45f, 1.15f, 1.10f, 1f),
        "inkbloom" => new(0.72f, 0.50f, 50f, 1.00f, 1.00f, 1f),
        "cosmicdust" => new(0.75f, 0.35f, 35f, 1.00f, 1.00f, 1f),
        "chromaspiral" => new(0.00f, 0.00f, 60f, 1.00f, 1.00f, 1f),
        "neongrid" => new(0.70f, 0.85f, 70f, 1.50f, 1.10f, 1f),
        "oilslick" => new(0.00f, 0.00f, 40f, 1.25f, 1.05f, 1f),
        "caustics" => new(0.55f, 0.30f, 35f, 1.20f, 1.10f, 1f),
        "galaxy" => new(0.72f, 0.20f, 45f, 1.20f, 1.10f, 1f),
        "starpath" => new(0.62f, 0.20f, 40f, 1.10f, 1.10f, 1f),
        "plasmaglobe" => new(0.78f, 0.30f, 60f, 1.20f, 1.10f, 1f),
        "lightning" => new(0.60f, 0.30f, 60f, 1.15f, 1.20f, 1f),
        "flowfield" => new(0.00f, 0.00f, 50f, 1.20f, 1.05f, 1f),
        "ferrofluid" => new(0.78f, 0.30f, 50f, 1.15f, 1.10f, 1f),
        "liquidchrome" => new(0.60f, 0.20f, 45f, 1.15f, 1.20f, 1f),
        "hextunnel" => new(0.55f, 0.40f, 65f, 1.20f, 1.10f, 1f),
        "mandelbrot" => new(0.00f, 0.00f, 55f, 1.10f, 1.10f, 1f),
        "circuit" => new(0.40f, 0.50f, 60f, 1.20f, 1.10f, 1f),
        "bokeh" => new(0.55f, 0.20f, 55f, 1.15f, 1.05f, 1f),
        "sandstorm" => new(0.07f, 0.55f, 50f, 1.20f, 1.10f, 1f),
        "dotmatrix" => new(0.00f, 0.00f, 55f, 1.20f, 1.10f, 1f),
        // Additional frag shaders.
        "bubbles" => new(0.55f, 0.15f, 40f, 1.15f, 1.10f, 1f),
        "silkwave" => new(0.82f, 0.30f, 50f, 1.20f, 1.10f, 1f),
        // prismwave ships as monochrome high-contrast (saturation 0) to match
        // the "stark black-and-white" look the brief calls for; slot 1 in the
        // template generator surfaces the rainbow variant.
        "prismwave" => new(0.00f, 0.00f, 55f, 0.00f, 1.65f, 1f),
        "crystaltunnel" => new(0.62f, 0.30f, 60f, 1.20f, 1.15f, 1f),
        "ribbonflow" => new(0.05f, 0.40f, 65f, 1.15f, 1.05f, 1f),
        // Audio-reactive effects. Signature values drive the thumbnail
        // render (which runs with AudioState all-zero unless a debug
        // payload is injected), so they reflect the idle look.
        "spectrumbars" => new(0.00f, 0.00f, 55f, 1.15f, 1.10f, 1f),
        "spectrumradial" => new(0.00f, 0.00f, 55f, 1.15f, 1.10f, 1f),
        "scope" => new(0.55f, 0.35f, 60f, 1.10f, 1.10f, 1f),
        "basspulse" => new(0.78f, 0.40f, 50f, 1.15f, 1.15f, 1f),
        "beatstrobe" => new(0.00f, 0.00f, 65f, 1.20f, 1.15f, 1f),
        "harmonicstar" => new(0.60f, 0.30f, 55f, 1.15f, 1.10f, 1f),
        "audiotunnel" => new(0.45f, 0.35f, 60f, 1.15f, 1.10f, 1f),
        "bassbloom" => new(0.85f, 0.35f, 45f, 1.15f, 1.10f, 1f),
        "beatbuilder" => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        // Fullscreen-first audio set.
        "spectrumaurora" => new(0.28f, 0.00f, 45f, 1.20f, 1.05f, 1f),
        "neonwaveform" => new(0.62f, 0.15f, 55f, 1.25f, 1.10f, 1f),
        "liquidbeat" => new(0.78f, 0.20f, 40f, 1.25f, 1.10f, 1f),
        "beatburst" => new(0.00f, 0.00f, 60f, 1.25f, 1.10f, 1f),
        // Tunnels + flowy + abstract backgrounds. Mirror SIGNATURES in lightingTemplates.ts.
        "ringtunnel" => new(0.50f, 0.40f, 65f, 1.20f, 1.10f, 1f),
        "vortextunnel" => new(0.72f, 0.30f, 55f, 1.15f, 1.10f, 1f),
        "helixtunnel" => new(0.45f, 0.35f, 60f, 1.15f, 1.10f, 1f),
        "boxtunnel" => new(0.80f, 0.35f, 60f, 1.20f, 1.15f, 1f),
        "meshgradient" => new(0.00f, 0.00f, 45f, 1.00f, 1.00f, 1f),
        "tide" => new(0.58f, 0.40f, 50f, 1.05f, 1.00f, 1f),
        "vapor" => new(0.60f, 0.45f, 45f, 0.95f, 1.05f, 1f),
        "satinflow" => new(0.88f, 0.30f, 50f, 1.15f, 1.10f, 1f),
        "ridgeline" => new(0.55f, 0.30f, 50f, 1.10f, 1.10f, 1f),
        "chevron" => new(0.08f, 0.45f, 60f, 1.20f, 1.10f, 1f),
        "terrace" => new(0.40f, 0.35f, 45f, 1.15f, 1.10f, 1f),
        "harlequin" => new(0.92f, 0.40f, 55f, 1.20f, 1.10f, 1f),
        "mosaic" => new(0.55f, 0.30f, 55f, 1.20f, 1.10f, 1f),
        // Constellation mesh plus the Nexus 2 theme set.
        "constellation" => new(0.55f, 0.20f, 45f, 1.10f, 1.05f, 1f),
        "cybertunnel" => new(0.85f, 0.30f, 60f, 1.20f, 1.10f, 1f),
        "hyperspace" => new(0.75f, 0.25f, 70f, 1.20f, 1.10f, 1f),
        "synthwave" => new(0.88f, 0.30f, 50f, 1.25f, 1.10f, 1f),
        "retropetals" => new(0.08f, 0.35f, 40f, 1.15f, 1.05f, 1f),
        // contourbands ships desaturated: the theme it comes from is ink on
        // paper, and the saturation slider is what turns it colour.
        "contourbands" => new(0.00f, 0.00f, 40f, 0.00f, 1.25f, 1f),
        _ => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
    };

    /// <summary>
    /// Declared per-effect uniform defaults, from the shader's own hint_range
    /// annotations, excluding the six base names ShaderEffect binds through
    /// dedicated fields rather than ExtraParams. Drives the thumbnail render.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, float> ExtraParamDefaults(string name)
    {
        var result = new System.Collections.Generic.Dictionary<string, float>();
        foreach (var (paramName, spec) in ShaderLibrary.Params(name))
        {
            if (spec.Declared && !ShaderEffect.BaseParamNames.Contains(paramName))
            {
                result[paramName] = spec.Default;
            }
        }
        return result;
    }

    private IEffect BuildAnimateEffect(string name, float speed, float intensity, float hue, float colorize, float saturation, float contrast, System.Collections.Generic.Dictionary<string, float>? extras)
    {
        // Every animate mode is a fragment-only GLSL shader. GPU-only - if
        // the context can't initialise, the canvas stays dark. Pulse gets
        // a 0.5x speed scale to match the legacy feel.
        var effectSpeed = name == "pulse" ? speed * 0.5f : speed;
        // Simple solid-colour fills all share one cheap shader; the colour is
        // carried by the post-process tint, not the GLSL.
        if (name.StartsWith("simple", System.StringComparison.Ordinal))
        {
            return MakeShader(name, ShaderLibrary.Get(name), effectSpeed, intensity, hue, colorize, saturation, contrast, extras);
        }
        // Static patterns load their own .frag by name. Without this they fall
        // through the switch below to its rainbow default and every one of them
        // renders as rainbow, which no gate would catch.
        if (StaticEffectCatalog.Contains(name))
        {
            return MakeShader(name, ShaderLibrary.Get(name), effectSpeed, intensity, hue, colorize, saturation, contrast, extras);
        }
        var src = name switch
        {
            "pulse" => ShaderLibrary.Pulse,
            "fire" => ShaderLibrary.Fire,
            "plasma" => ShaderLibrary.Plasma,
            "spiral" => ShaderLibrary.Spiral,
            "matrix" => ShaderLibrary.Matrix,
            "meteor" => ShaderLibrary.Meteor,
            "ripple" => ShaderLibrary.Ripple,
            "wave" => ShaderLibrary.Wave,
            "gradientwave" => ShaderLibrary.GradientWave,
            "ball" => ShaderLibrary.Ball,
            "radar" => ShaderLibrary.Radar,
            "watercolor" => ShaderLibrary.Watercolor,
            "jellyfish" => ShaderLibrary.Jellyfish,
            "aurora" => ShaderLibrary.Aurora,
            "lavalamp" => ShaderLibrary.LavaLamp,
            "starfield" => ShaderLibrary.Starfield,
            "voronoi" => ShaderLibrary.VoronoiCells,
            "neonrain" => ShaderLibrary.NeonRain,
            "bursts" => ShaderLibrary.Bursts,
            "nebula" => ShaderLibrary.Nebula,
            "lavafissure" => ShaderLibrary.LavaFissure,
            "kaleidoscope" => ShaderLibrary.Kaleidoscope,
            "wormhole" => ShaderLibrary.Wormhole,
            "interference" => ShaderLibrary.Interference,
            "sacredgeometry" => ShaderLibrary.SacredGeometry,
            "tessellation" => ShaderLibrary.Tessellation,
            "domainwarp" => ShaderLibrary.DomainWarp,
            "inkbloom" => ShaderLibrary.InkBloom,
            "cosmicdust" => ShaderLibrary.CosmicDust,
            "chromaspiral" => ShaderLibrary.ChromaSpiral,
            "neongrid" => ShaderLibrary.NeonGrid,
            "oilslick" => ShaderLibrary.OilSlick,
            "caustics" => ShaderLibrary.Get("caustics"),
            "galaxy" => ShaderLibrary.Get("galaxy"),
            "starpath" => ShaderLibrary.Get("starpath"),
            "plasmaglobe" => ShaderLibrary.Get("plasmaglobe"),
            "lightning" => ShaderLibrary.Get("lightning"),
            "flowfield" => ShaderLibrary.Get("flowfield"),
            "ferrofluid" => ShaderLibrary.Get("ferrofluid"),
            "liquidchrome" => ShaderLibrary.Get("liquidchrome"),
            "hextunnel" => ShaderLibrary.Get("hextunnel"),
            "mandelbrot" => ShaderLibrary.Get("mandelbrot"),
            "circuit" => ShaderLibrary.Get("circuit"),
            "bokeh" => ShaderLibrary.Get("bokeh"),
            "sandstorm" => ShaderLibrary.Get("sandstorm"),
            "dotmatrix" => ShaderLibrary.Get("dotmatrix"),
            "spectrumbars" => ShaderLibrary.Get("spectrumbars"),
            "spectrumradial" => ShaderLibrary.Get("spectrumradial"),
            "scope" => ShaderLibrary.Get("scope"),
            "basspulse" => ShaderLibrary.Get("basspulse"),
            "beatstrobe" => ShaderLibrary.Get("beatstrobe"),
            "harmonicstar" => ShaderLibrary.Get("harmonicstar"),
            "audiotunnel" => ShaderLibrary.Get("audiotunnel"),
            "bassbloom" => ShaderLibrary.Get("bassbloom"),
            "beatbuilder" => ShaderLibrary.BeatBuilder,
            "spectrumaurora" => ShaderLibrary.Get("spectrumaurora"),
            "neonwaveform" => ShaderLibrary.Get("neonwaveform"),
            "liquidbeat" => ShaderLibrary.Get("liquidbeat"),
            "beatburst" => ShaderLibrary.Get("beatburst"),
            "bubbles" => ShaderLibrary.Bubbles,
            "silkwave" => ShaderLibrary.SilkWave,
            "prismwave" => ShaderLibrary.PrismWave,
            "crystaltunnel" => ShaderLibrary.CrystalTunnel,
            "ribbonflow" => ShaderLibrary.RibbonFlow,
            "ringtunnel" => ShaderLibrary.Get("ringtunnel"),
            "vortextunnel" => ShaderLibrary.Get("vortextunnel"),
            "helixtunnel" => ShaderLibrary.Get("helixtunnel"),
            "boxtunnel" => ShaderLibrary.Get("boxtunnel"),
            "meshgradient" => ShaderLibrary.Get("meshgradient"),
            "tide" => ShaderLibrary.Get("tide"),
            "vapor" => ShaderLibrary.Get("vapor"),
            "satinflow" => ShaderLibrary.Get("satinflow"),
            "ridgeline" => ShaderLibrary.Get("ridgeline"),
            "chevron" => ShaderLibrary.Get("chevron"),
            "terrace" => ShaderLibrary.Get("terrace"),
            "harlequin" => ShaderLibrary.Get("harlequin"),
            "mosaic" => ShaderLibrary.Get("mosaic"),
            "sharplines" => ShaderLibrary.Get("sharplines"),
            "constellation" => ShaderLibrary.Get("constellation"),
            "cybertunnel" => ShaderLibrary.Get("cybertunnel"),
            "hyperspace" => ShaderLibrary.Get("hyperspace"),
            "synthwave" => ShaderLibrary.Get("synthwave"),
            "retropetals" => ShaderLibrary.Get("retropetals"),
            "contourbands" => ShaderLibrary.Get("contourbands"),
            _ => ShaderLibrary.Rainbow,
        };
        return MakeShader(name, src, effectSpeed, intensity, hue, colorize, saturation, contrast, extras);
    }

    public void StartMusic(MusicHeadlessStart body)
    {
        if (!_gates.Lighting)
        {
            return;
        }
        // No audio capture impl yet. Persist intent so the SPA can reflect it,
        // but the engine doesn't render anything.
        _store.Update(s =>
        {
            s.Lighting.Sync = "music";
            LightingPresetLooks.CaptureIntoActive(s);
        });
    }

    public void StartScreen(ScreenHeadlessStart body)
    {
        if (!_gates.Lighting)
        {
            return;
        }
        EnsureRgbActive();
        _engine.FrameIntervalMs = 16;
        // Do NOT overwrite _screenPP from body. The post-process holder is the
        // authoritative live state; it was loaded from settings at construction
        // and is mutated only by the /lighting/screen/effect endpoint. If a
        // client calls /start with empty post-process fields before it has
        // fetched the current values, overwriting here would silently clobber
        // the user's saved look back to identity on every mode swap.
        _engine.SetEffect(new ScreenMirrorEffect(body.Monitor, _screenPP, _frameSource, _gpu));
        _store.Update(s =>
        {
            s.Lighting.Sync = "screen";
            LightingPresetLooks.CaptureIntoActive(s);
        });
    }

    /// <summary>
    /// Re-open the OS screen picker to change which screen is mirrored (Linux/
    /// Wayland). Drops the saved grant + current capture via the frame source,
    /// then re-applies screen mode so the next frame spawns a fresh handshake and
    /// the picker appears. No-op where the frame source picks directly.
    /// </summary>
    public void ReselectScreen()
    {
        _frameSource?.Reselect();
        StartScreen(new ScreenHeadlessStart());
    }

    /// <summary>
    /// Returns the live Screen Mirror post-process. Aliased reference so the
    /// endpoint can write through it without a subsequent /start call.
    /// </summary>
    public PostProcessState ScreenPostProcess => _screenPP;

    /// <summary>Same alias for the Media post-process holder.</summary>
    public PostProcessState MediaPostProcess => _mediaPP;

    public void UpdateScreenEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist, bool reactive = false, float reactivity = 0.5f, float intensity = 0.5f)
    {
        _screenPP.Set(hue, colorize, saturation, contrast, flipX, flipY, reactive, reactivity, intensity);
        if (!persist)
        {
            return;
        }
        _store.Update(s =>
        {
            s.Lighting.ScreenEffect.Hue = hue;
            s.Lighting.ScreenEffect.Colorize = colorize;
            s.Lighting.ScreenEffect.Saturation = saturation;
            s.Lighting.ScreenEffect.Contrast = contrast;
            s.Lighting.ScreenEffect.FlipX = flipX;
            s.Lighting.ScreenEffect.FlipY = flipY;
            s.Lighting.ScreenEffect.Reactive = reactive;
            s.Lighting.ScreenEffect.Reactivity = reactivity;
            s.Lighting.ScreenEffect.Intensity = intensity;
            LightingPresetLooks.CaptureIntoActive(s);
        });
    }

    public void UpdateMediaEffect(float hue, float colorize, float saturation, float contrast, bool flipX, bool flipY, bool persist)
    {
        _mediaPP.Set(hue, colorize, saturation, contrast, flipX, flipY);
        if (!persist)
            return;
        _store.Update(s =>
        {
            s.Lighting.MediaEffect.Hue = hue;
            s.Lighting.MediaEffect.Colorize = colorize;
            s.Lighting.MediaEffect.Saturation = saturation;
            s.Lighting.MediaEffect.Contrast = contrast;
            s.Lighting.MediaEffect.FlipX = flipX;
            s.Lighting.MediaEffect.FlipY = flipY;
            LightingPresetLooks.CaptureIntoActive(s);
        });
    }

    // Fires on the scanner's background thread after each scan completes.
    // Ensures the GSI cfg is present when Game Sync is active and a supported
    // GSI game was found, without requiring the user to re-toggle.
    private void OnGameScanComplete(IReadOnlyList<DetectedGame> games)
    {
        if (!string.Equals(GetSync(), "gamesync", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hasGsiGame = false;
        foreach (var g in games)
        {
            if (g.EmitsGsi)
            {
                hasGsiGame = true;
                break;
            }
        }

        if (!hasGsiGame)
        {
            return;
        }

        var token = _store.Load().Auth?.Token ?? "";
        if (token.Length > 0)
        {
            GsiConfigInstaller.EnsureInstalled(token);
        }
    }

    public void StartGameSync()
    {
        if (!_gates.Lighting)
        {
            return;
        }
        EnsureRgbActive();
        // Reuse the existing effect instance so frames ingested before the
        // mode selection round-trip arrives are not lost.
        if (_gameSyncEffect is null || _engine.CurrentEffect != _gameSyncEffect)
        {
            _gameSyncEffect = new GameSyncEffect();
            _engine.SetEffect(_gameSyncEffect);
        }
        // Clear per-session signal so a stale app name or last-seen time
        // from a previous Game Sync session does not bleed into the new one.
        _gameSyncEffect.Reset();
        _store.Update(s =>
        {
            s.Lighting.Sync = "gamesync";
            LightingPresetLooks.CaptureIntoActive(s);
        });

        // Deploy shim DLLs into System32/SysWOW64. Idempotent and guarded by
        // an elevation check; skipped on macOS/Linux (NotApplicable).
        var shimResult = GameSyncShimInstaller.EnsureInstalled();
        switch (shimResult)
        {
            case ChromaShimInstallResult.Installed:
                ServiceLog.Info("[chroma-shim] shim DLLs installed");
                break;
            case ChromaShimInstallResult.AlreadyCurrent:
                ServiceLog.Info("[chroma-shim] shim DLLs already current");
                break;
            case ChromaShimInstallResult.SynapseConflict:
                ServiceLog.Warn("[chroma-shim] Razer Chroma SDK present; shim not installed");
                break;
            case ChromaShimInstallResult.NotElevated:
                ServiceLog.Warn("[chroma-shim] not elevated; shim install skipped");
                break;
            case ChromaShimInstallResult.BundleMissing:
                ServiceLog.Info("[chroma-shim] bundled shim DLLs not found; Game Sync stays inactive");
                break;
            case ChromaShimInstallResult.Failed:
                ServiceLog.Error("[chroma-shim] shim install failed");
                break;
        }

        var token = _store.Load().Auth?.Token ?? "";
        if (token.Length > 0)
        {
            GsiConfigInstaller.EnsureInstalled(token);
        }
    }

    public GameSyncEffect? ActiveGameSyncEffect()
    {
        if (_engine.CurrentEffect is GameSyncEffect eff)
        {
            return eff;
        }
        return null;
    }

    public bool StartMedia(string mediaId)
    {
        if (!_gates.Lighting)
        {
            return false;
        }
        var item = _media.GetItem(mediaId);
        if (item is null)
        {
            return false;
        }
        var framesPath = _media.GetFramesBinPath(mediaId);
        if (!System.IO.File.Exists(framesPath))
        {
            return false;
        }

        EnsureRgbActive();
        _engine.SetEffect(new MediaFramesEffect(framesPath, item.Width, item.Height, item.Fps, _mediaPP));
        _store.Update(s =>
        {
            s.Lighting.Sync = "media";
            s.Lighting.LastMediaId = mediaId;
            LightingPresetLooks.CaptureIntoActive(s);
        });
        return true;
    }

    public void StartMediaIdle()
    {
        if (!_gates.Lighting)
        {
            return;
        }
        EnsureRgbActive();
        _engine.SetEffect(new Engine.Effects.BlackEffect());
        _store.Update(s =>
        {
            s.Lighting.Sync = "media";
            LightingPresetLooks.CaptureIntoActive(s);
        });
    }

    public void Dispose()
    {
        _rgb?.Dispose();
        _engine.Dispose();
    }
}
