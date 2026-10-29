using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
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
public sealed partial class LightingProvider : ILightingProvider, IDisposable
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

    private readonly FeatureGates _gates;
    private readonly SensorFeed? _sensorFeed;

    public LightingProvider(IConfigStore store, LightingEngine engine, LightingOutputHub hub, GpuContext gpu, MediaLibrary media, IMonitorEnumerator monitors, IScreenFrameSource? frameSource = null, RgbBridge? rgb = null, IBeatsProvider? beats = null, FeatureGates? gates = null, Nexus.Service.Lighting.CustomEffects.CustomEffectStore? customEffects = null, MultiplexHub? panelHub = null, ScreenPreviewHub? previewHub = null, SensorFeed? sensorFeed = null)
    {
        _panelHub = panelHub;
        _sensorFeed = sensorFeed;
        _gates = gates ?? FeatureGates.AllEnabled;
        _store = store;
        _engine = engine;
        // The engine renders per-device pins but must not know how to build a
        // shader; hand it the same builders the modes use.
        _engine.StaticPinEffectFactory = a => BuildAnimateEffect(
            a.Effect ?? "", 0f, DevicePinLook.Intensity(a), a.Hue ?? 0f, a.Colorize ?? 0f, DevicePinLook.Saturation(a), DevicePinLook.Contrast(a),
            a.Params is null ? null : new System.Collections.Generic.Dictionary<string, float>(a.Params));
        _engine.PinLookTag = PinTag;
        _engine.PinEffectFactory = BuildPinnedEffect;
        _sharedSource = frameSource is null ? null : new SharedScreenFrameSource(frameSource);
        if (previewHub is not null && _sharedSource is not null)
        {
            var shared = _sharedSource;
            previewHub.IsAvailable = () => shared.Available;
            _previewStreamer = new ScreenPreviewStreamer(previewHub, shared.CreateObserver, () => _mirrorMonitor, _screenPP);
        }
        _hub = hub;
        _rgb = rgb;
        _gpu = gpu;
        _media = media;
        _monitors = monitors;
        _frameSource = frameSource;
        _beats = beats;

        var s = _store.Load().Lighting;
        _mirrorMonitor = s.ScreenMonitor ?? "";
        _screenPP.Set(s.ScreenEffect.Hue, s.ScreenEffect.Colorize, s.ScreenEffect.Saturation, s.ScreenEffect.Contrast, s.ScreenEffect.FlipX, s.ScreenEffect.FlipY, s.ScreenEffect.Reactive, s.ScreenEffect.Reactivity, s.ScreenEffect.Intensity);
        _mediaPP.Set(s.MediaEffect.Hue, s.MediaEffect.Colorize, s.MediaEffect.Saturation, s.MediaEffect.Contrast, s.MediaEffect.FlipX, s.MediaEffect.FlipY);

        _engine.OnFrame += frame => _ = _hub.BroadcastBinaryAsync(frame);
        _engine.OnEffectChanged += ReconcileAudioCapture;
        // Any settings write can move a pinned effect's look, the pin set, or the mirrored screen.
        _store.OnChanged += RefreshPinnedLooks;
        RefreshPinnedLooks();
        // A store that was unreadable at boot registers its sources late; a custom effect
        // restored meanwhile is running the rainbow stand-in until this rebuilds it.
        customEffects?.Recovered += () =>
        {
            if (_engine.CurrentEffect is ShaderEffect running && ShaderLibrary.IsCustomKey(running.Name))
            {
                ReloadCustomEffect(running.Name);
            }
            RefreshPinnedLooks();
        };
    }

    /// <summary>
    /// Bring the RGB hardware bridge online whenever a real effect starts. The
    /// bridge is null on platforms without an OpenRGB binary (macOS / Linux),
    /// in which case the call is a no-op.
    /// </summary>
    private void EnsureRgbActive()
    {
        // Only simple mode's sweep set samples every device across the whole
        // canvas; StartAnimate re-asserts it, so a mode start cannot inherit it.
        _engine.FullFrameSampling = false;
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

    public void SetSync(string sync) => _store.Update(s => s.Lighting.Sync = RemovedSyncModes.Normalize(sync));

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
            var music = _store.Load().Lighting.MusicReactive;
            var forLeds = music && (ShaderLibrary.IsAudioEffect(_engine.CurrentEffectName) || _pinnedAudio);
            if (forLeds || Volatile.Read(ref _audioCaptureDemand) > 0 || _studioAudio)
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
        SelectedMonitor = _mirrorMonitor,
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
        var sensors = Nexus.Service.Persistence.SensorBinding.CopyMap(body.Sensors);
        var effectSpeed = name == "pulse" ? speed * 0.5f : speed;
        // A sweep is meant to read end to end on every device at once, so the
        // engine stops treating the canvas as a shared scene for these.
        _engine.FullFrameSampling = ShaderLibrary.IsSweepEffect(name);

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
            cur.SensorOverrides = sensors;
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
            _engine.SetEffect(BuildAnimateEffect(name, speed, intensity, hue, colorize, saturation, contrast, extras, sensors));
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
            Sensors = sensors,
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
                    Sensors = baseline.Sensors,
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
        if (!string.IsNullOrEmpty(body.Color) && !StaticColorHex.TryParse(body.Color, out _, out _, out _))
        {
            throw new System.ArgumentException($"'{body.Color}' is not a #rrggbb color", nameof(body));
        }
        EnsureRgbActive();
        if (StaticColorHex.TryParse(body.Color, out var flatR, out var flatG, out var flatB))
        {
            StartFlatStatic(body, flatR, flatG, flatB);
            return;
        }
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
            s.Lighting.Static.Color = null;
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

    /// <summary>The global Static look as one exact flat color: every unpinned device shows it.</summary>
    private void StartFlatStatic(StaticHeadlessStart body, byte r, byte g, byte b)
    {
        _engine.SetEffect(new FlatColorEffect(r, g, b));
        _engine.SetFrozen(true);
        if (!body.Persist)
        {
            return;
        }
        var hex = $"#{r:x2}{g:x2}{b:x2}";
        _store.Update(s =>
        {
            s.Lighting.Sync = "static";
            s.Lighting.Static.Color = hex;
            s.Lighting.StaticColor.R = r;
            s.Lighting.StaticColor.G = g;
            s.Lighting.StaticColor.B = b;
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

    private ShaderEffect MakeShader(string name, string src, float speed, float intensity, float hue, float colorize, float saturation, float contrast, System.Collections.Generic.Dictionary<string, float>? extras, System.Collections.Generic.IReadOnlyDictionary<string, Nexus.Service.Persistence.SensorBinding>? sensors = null) =>
        new ShaderEffect(name, _gpu, src)
        {
            SensorFeed = _sensorFeed,
            SensorOverrides = sensors,
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
        try
        {
            return CaptureAnimateThumbnailCore(key, slot, skipCache, frozen);
        }
        catch (System.IO.FileNotFoundException)
        {
            // A custom effect deleted mid-request is simply unknown.
            return null;
        }
    }

    // Identity of this exact build (release version plus the executable's size and write time), folded into every disk key so no build serves another build's images.
    private static readonly string ThumbnailDiskVersion = BuildIdentity();

    private static string BuildIdentity()
    {
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(LightingProvider).Assembly)?.InformationalVersion
            ?? typeof(LightingProvider).Assembly.GetName().Version?.ToString()
            ?? "0";
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var info = new System.IO.FileInfo(exe);
                return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{version}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }
        catch
        {
        }
        return version;
    }

    // Thumbnails are fixed-size BMPs, so a stored file of any other length is damaged.
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 90;
    private const int ThumbnailBmpBytes = 54 + (ThumbnailWidth * 3 + (4 - ThumbnailWidth * 3 % 4) % 4) * ThumbnailHeight;
    private const long ThumbnailDiskCapBytes = 64L * 1024 * 1024;

    // Rendered thumbnails survive a restart. Shared by every provider and created on first use, which starts its background init.
    private static readonly Lazy<Engine.Gpu.DiskBlobCache> ThumbnailDiskLazy = new(() => new Engine.Gpu.DiskBlobCache(
        System.IO.Path.Combine(Engine.Gpu.BlobCachePolicy.CacheRoot(), "lighting-thumbnails"),
        ThumbnailDiskCapBytes, Engine.Gpu.BlobCachePolicy.Allowed));

    private (byte[] Bytes, string Tag)? CaptureAnimateThumbnailCore(string key, int slot, bool skipCache, bool frozen)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var name = (key ?? "").ToLowerInvariant();
        if (!ShaderLibrary.IsKnownKey(name))
        {
            return null;
        }
        var defaults = ExtraParamDefaults(name);
        // Render the requested preset slot's effective look (user delta or
        // canonical default). Tag the cache by that look (not the client's ?v
        // token), so a surface that keeps requesting the same token still
        // re-renders once the slot's saved look changes. The shader's own hash
        // rides along: the tag is the HTTP ETag, and without it a browser keeps
        // revalidating to the image of a shader a deploy has since changed.
        var state = ResolveSlotLook(name, slot);
        var tag = ShaderLibrary.SourceTag(name) + ":" + (state is null ? "sig" : HashSlot(state)) + (frozen ? ":f" : "");
        var cacheKey = name + ":" + slot + (frozen ? ":f" : "");
        if (!skipCache && _thumbnailCache.TryGetValue(cacheKey, out var cached) && cached.Tag == tag)
        {
            return (cached.Bytes, tag);
        }
        // Shader thumbnails are GPU-rendered; with no usable GPU the render is a
        // no-op and the frame stays black, so skip it and let the caller 404 -
        // the UI shows a placeholder instead of a grid of black tiles.
        if (!_gpu.Available)
        {
            return null;
        }
        var diskName = Engine.Gpu.DiskBlobCache.ThumbnailName(name, slot, frozen, tag, ThumbnailDiskVersion);
        if (!skipCache && ThumbnailDiskLazy.Value.TryGet(diskName) is { Length: ThumbnailBmpBytes } onDisk)
        {
            _thumbnailCache[cacheKey] = (tag, onDisk);
            Engine.Gpu.GpuContext.Log($"[thumb] {cacheKey} disk {sw.Elapsed.TotalMilliseconds:0}ms");
            return (onDisk, tag);
        }

        var canvas = new Engine.CanvasBuffer(ThumbnailWidth, ThumbnailHeight);
        var generation = _gpu.Generation;
        var effect = BuildThumbnailEffect(name, defaults, state, frozen);
        if (effect is ShaderEffect thumbShader)
        {
            thumbShader.FixedSensorValue = ThumbnailSensorValue;
        }
        // Thumbnails ride the GL thread's background lane so a page full of them cannot stall the live frame.
        using var background = Engine.Gpu.GpuContext.BeginBackground();
        try
        {
            if (!frozen && TimeLapseWindowMs(name) is var (start, span))
            {
                RenderTimeLapse(effect, canvas, start, span);
            }
            else
            {
                effect.RenderFrame(canvas, ThumbnailTimeMs(name, state, frozen));
            }
        }
        finally
        {
            try
            { effect.Dispose(); }
            catch { }
        }
        var bytes = BmpEncoder.Encode(canvas.Pixels, canvas.Width, canvas.Height);
        _thumbnailCache[cacheKey] = (tag, bytes);
        // A failed compile or a lost context leaves a black frame: serve it, but never persist it.
        if (_gpu.Available && _gpu.Generation == generation && effect is not ShaderEffect { HasFailed: true })
        {
            ThumbnailDiskLazy.Value.Put(diskName, bytes);
        }
        Engine.Gpu.GpuContext.Log($"[thumb] {cacheKey} render {sw.Elapsed.TotalMilliseconds:0}ms");
        return (bytes, tag);
    }

    // Effects that paint the whole frame one colour and change only over time.
    // One frame of them is a flat swatch, so their thumbnail lays a stretch of
    // time out left to right instead, picked for its colour at 1x speed on the
    // default look: one whole breath, the blue one, and the cycle's pass
    // through green. The windows follow the shaders' tempos and default colours.
    private static (double StartMs, double SpanMs)? TimeLapseWindowMs(string name) => name switch
    {
        "breathing" or "sweepbreathing" => (14000.0, 4000.0),
        "sweepcycle" => (3170.0, 2000.0),
        _ => null,
    };

    // The lightning thumbnail is taken just after the first bolt strikes, so it shows the bright moment
    // rather than the decay; every other effect uses the shared settled time.
    private static double ThumbnailTimeMs(string name, Nexus.Service.Persistence.AnimateEffectState? state, bool frozen)
    {
        if (name != "lightning" || frozen)
        {
            return ThumbnailFrameTimeMs;
        }
        var rate = 0.8f;
        if (state?.Params is { } p && p.TryGetValue("u_boltRate", out var r))
        {
            rate = r;
        }
        var speed = (state?.Speed ?? 60) / 50f;
        if (speed <= 0.01f || rate <= 0.01f)
        {
            return ThumbnailFrameTimeMs;
        }
        // Bolt zero's cycle position advances at this fixed ratio of the shader clock; land at a bright point of it.
        const double StrikePhase = 0.07;
        const double CyclePerClockSecond = 0.35 * 0.55;
        return StrikePhase / (CyclePerClockSecond * rate * speed) * 1000.0;
    }

    private static void RenderTimeLapse(IEffect effect, Engine.CanvasBuffer canvas, double startMs, double spanMs)
    {
        // The window is timed at 1x, so a fast or reversed preset renders the
        // same stretch rather than several breaths or the wrong colour.
        if (effect is ShaderEffect shader)
        {
            shader.Speed = 1f;
        }
        // One frame per pixel column: coarser slices alias a fast preset's
        // breaths into grey bands.
        var slices = canvas.Width;
        var frame = new Engine.CanvasBuffer(canvas.Width, canvas.Height);
        for (var i = 0; i < slices; i++)
        {
            effect.RenderFrame(frame, startMs + (i + 0.5) * spanMs / slices);
            var x1 = (i + 1) * canvas.Width / slices;
            for (var x = i * canvas.Width / slices; x < x1; x++)
            {
                for (var y = 0; y < canvas.Height; y++)
                {
                    var (r, g, b) = frame.GetPixel(x, y);
                    canvas.SetPixel(x, y, r, g, b);
                }
            }
        }
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

    // Test seam: runs between reading the source and swapping it in.
    internal Action? BeforeCustomSwap;

    // Each lost swap means another thread changed the effect; a bounded retry keeps a
    // pathological stream of concurrent edits from spinning this call forever.
    private const int MaxCustomSwapAttempts = 32;

    public void ReloadCustomEffect(string key)
    {
        ClearThumbnails(key);
        // Devices pinned to it rebuild from the new source.
        RefreshPinnedLooks();
        // A lost swap means another thread changed the effect after we read it (often a
        // concurrent PUT of the same id). Re-read until our swap lands or the running effect
        // is no longer this key, so the engine never keeps older source than the store.
        for (var attempt = 0; attempt < MaxCustomSwapAttempts; attempt++)
        {
            if (_engine.CurrentEffect is not ShaderEffect cur || cur.Name != key || !ShaderLibrary.IsKnownKey(key))
            {
                return;
            }
            var extras = cur.ExtraParams is null
                ? null
                : new System.Collections.Generic.Dictionary<string, float>(cur.ExtraParams);
            ShaderEffect fresh;
            try
            {
                fresh = MakeShader(key, ShaderLibrary.Get(key), cur.Speed, cur.Intensity, cur.Hue, cur.Colorize, cur.Saturation, cur.Contrast, extras, cur.SensorOverrides);
            }
            catch (System.IO.FileNotFoundException)
            {
                return;
            }
            BeforeCustomSwap?.Invoke();
            // Compare-and-swap under the engine lock: a concurrent effect switch wins, and
            // paused, frozen and blackout state survive the swap.
            if (_engine.SwapEffectIfCurrent(cur, fresh))
            {
                return;
            }
            fresh.Dispose();
        }
    }

    public void DropCustomEffect(string key)
    {
        ClearThumbnails(key);
        var running = _engine.CurrentEffect is ShaderEffect cur && cur.Name == key;
        _store.Update(s =>
        {
            s.Lighting.Animate.States.Remove(key);
            s.Lighting.Animate.Templates.Remove(key);
            // Devices pinned to the deleted effect are unpinned rather than left on a look that cannot render.
            foreach (var (deviceId, pin) in s.Lighting.DevicePins.ToArray())
            {
                if (pin?.Kind == Nexus.Service.Persistence.DevicePinKinds.Animate
                    && string.Equals(pin.Effect, key, System.StringComparison.OrdinalIgnoreCase))
                {
                    s.Lighting.DevicePins.Remove(deviceId);
                }
            }
            // A layout preset that captured such a pin would write it back on activation.
            foreach (var preset in s.Lighting.LayoutPresets)
            {
                if (preset.DevicePins is null)
                {
                    continue;
                }
                foreach (var (deviceId, pin) in preset.DevicePins.ToArray())
                {
                    if (pin?.Kind == Nexus.Service.Persistence.DevicePinKinds.Animate
                        && string.Equals(pin.Effect, key, System.StringComparison.OrdinalIgnoreCase))
                    {
                        preset.DevicePins.Remove(deviceId);
                    }
                }
            }
            // Not running (static mode, stopped): the next animate start must not run rainbow under the dead name.
            if (string.Equals(s.Lighting.Animate.Effect, key, System.StringComparison.OrdinalIgnoreCase))
            {
                s.Lighting.Animate.Effect = "rainbow";
            }
            if (string.Equals(s.Lighting.Sync, key, System.StringComparison.OrdinalIgnoreCase))
            {
                s.Lighting.Sync = "rainbow";
            }
        });
        if (!running)
        {
            return;
        }
        var a = _store.Load().Lighting.Animate;
        if (!a.States.TryGetValue("rainbow", out var look) || look is null)
        {
            look = AnimateTemplateDefaults.ResolveSelected(a.Templates, "rainbow") ?? new Nexus.Service.Persistence.AnimateEffectState();
        }
        var list = new List<ShaderParam>();
        foreach (var kv in look.Params)
        {
            list.Add(new ShaderParam { Name = kv.Key, Value = kv.Value });
        }
        StartAnimate(new AnimateHeadlessStart
        {
            Effect = "rainbow",
            Speed = look.Speed,
            Intensity = look.Intensity,
            Hue = look.Hue,
            Colorize = look.Colorize,
            Saturation = look.Saturation,
            Contrast = look.Contrast,
            Params = list,
        });
    }

    private void ClearThumbnails(string key)
    {
        var prefix = key + ":";
        foreach (var k in _thumbnailCache.Keys)
        {
            if (k.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                _thumbnailCache.TryRemove(k, out _);
            }
        }
    }

    // Immutable look per pinned Animate effect, resolved off the engine thread and
    // published by reference swap; the engine only ever reads this.
    private sealed record PinnedLook(string Effect, Nexus.Service.Persistence.AnimateEffectState State, string Tag);
    private volatile System.Collections.Generic.Dictionary<string, PinnedLook> _pinnedLooks = new();
    private volatile bool _pinnedAudio;
    private long _pinnedLooksSeq;

    // The one screen capture. The global Mirror effect and the mirror-pin effect
    // each take a handle, so the capture runs once and closes with its last user.
    private readonly SharedScreenFrameSource? _sharedSource;
    private readonly ScreenPreviewStreamer? _previewStreamer;
    // Monitor and its pin tag change together, so a reader never sees one
    // without the other and the per-tick tag lookup allocates nothing.
    private sealed record MirrorSelection(string Monitor, string Tag);
    private volatile MirrorSelection _mirrorSel = new("", "m|");
    private string _mirrorMonitor
    {
        get => _mirrorSel.Monitor;
        set => _mirrorSel = new MirrorSelection(value, "m|" + value);
    }
    private static readonly string EmptyMediaTag = MediaTag("");
    // The media item pinned devices play, loaded off the engine thread and
    // published by reference swap. Null when no media pin exists.
    private sealed record PinnedMedia(string Id, string Tag, int Width, int Height, int Fps, byte[]? Frames);
    private volatile PinnedMedia? _pinnedMedia;

    private IScreenFrameSource? ScreenSource() => _sharedSource is null ? _frameSource : _sharedSource.CreateHandle();

    internal string? PinTagForTest(string key) => PinTag(key);

    private string? PinTag(string key) =>
        key == LightingEngine.MirrorPinKey
            ? _mirrorSel.Tag
            : key == LightingEngine.MediaPinKey
                ? _pinnedMedia?.Tag ?? EmptyMediaTag
                : _pinnedLooks.TryGetValue(key, out var l) ? l.Tag : null;

    /// <summary>
    /// Re-resolves every pinned Animate effect's look the way the active effect
    /// does (<see cref="ActiveLookTag"/>: the selected preset slot) from a copy
    /// taken under the store lock, then re-evaluates audio capture. Runs on
    /// whichever thread wrote settings, never the engine thread.
    /// </summary>
    private void RefreshPinnedLooks()
    {
        // A profile apply or cloud pull replaces the persisted screen.
        var persisted = _store.Load().Lighting;
        _mirrorMonitor = persisted.ScreenMonitor ?? "";
        var next = new System.Collections.Generic.Dictionary<string, PinnedLook>(System.StringComparer.Ordinal);
        var mediaWanted = false;
        var mediaId = "";
        long mediaSeq = 0;
        _store.Read(s =>
        {
            // Read in the same locked snapshot; the sequence orders snapshots.
            mediaSeq = System.Threading.Interlocked.Increment(ref _mediaSeq);
            mediaId = s.Lighting.LastMediaId ?? "";
            foreach (var mp in s.Lighting.DevicePins.Values)
            {
                if (mp?.Kind == Nexus.Service.Persistence.DevicePinKinds.Media)
                {
                    mediaWanted = true;
                    break;
                }
            }
            foreach (var pin in s.Lighting.DevicePins.Values)
            {
                var key = pin?.Kind == Nexus.Service.Persistence.DevicePinKinds.Animate ? pin.Effect : null;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                // Pins that differ in their sensor picks render apart; the rest of the look is the effect's.
                var renderKey = DevicePinLook.AnimateRenderKey(pin!);
                if (next.ContainsKey(renderKey))
                {
                    continue;
                }
                // A deleted (or not yet loaded) custom effect has nothing to build.
                if (ShaderLibrary.IsCustomKey(key) && !ShaderLibrary.IsKnownKey(key))
                {
                    continue;
                }
                var slot = AnimateTemplateDefaults.ResolveSelected(s.Lighting.Animate.Templates, key);
                if (slot is null && ShaderLibrary.IsSweepEffect(key))
                {
                    // Simple mode's sweeps have no preset bundle: use what simple
                    // mode last persisted for them, else the plain defaults.
                    slot = s.Lighting.Animate.States.TryGetValue(key, out var persisted) && persisted is not null
                        ? persisted
                        : new Nexus.Service.Persistence.AnimateEffectState();
                }
                if (slot is null && (ShaderLibrary.IsCustomKey(key) || ShaderLibrary.IsSensorEffect(key)))
                {
                    // A custom effect with no saved preset runs as a fresh start of it does: its last
                    // activation state, else the install defaults.
                    slot = s.Lighting.Animate.States.TryGetValue(key, out var customState) && customState is not null
                        ? customState
                        : new Nexus.Service.Persistence.AnimateEffectState();
                }
                if (slot is null)
                {
                    continue;
                }
                var copy = new Nexus.Service.Persistence.AnimateEffectState
                {
                    Speed = slot.Speed,
                    Intensity = slot.Intensity,
                    Hue = slot.Hue,
                    Colorize = slot.Colorize,
                    Saturation = slot.Saturation,
                    Contrast = slot.Contrast,
                    Params = slot.Params is null
                        ? new()
                        : new System.Collections.Generic.Dictionary<string, float>(slot.Params),
                    Sensors = Nexus.Service.Persistence.SensorBinding.Merge(slot.Sensors, pin!.Sensors),
                };
                // A custom effect's source can change under the same look, so the tag carries it.
                var tag = HashSlot(copy);
                if (ShaderLibrary.IsCustomKey(key) && ShaderLibrary.IsKnownKey(key))
                {
                    tag += "|" + ShaderLibrary.SourceTag(key);
                }
                next[renderKey] = new PinnedLook(key, copy, tag);
            }
        });
        var audio = false;
        foreach (var look in next.Values)
        {
            audio |= ShaderLibrary.IsAudioEffect(look.Effect);
        }
        bool audioChanged;
        lock (_mediaLoadLock)
        {
            // An older snapshot never publishes over a newer one.
            if (mediaSeq < _pinnedLooksSeq)
            {
                return;
            }
            _pinnedLooksSeq = mediaSeq;
            _pinnedLooks = next;
            // A render key no pin uses any more must not keep its source tag for the life of the process.
            foreach (var builtKey in _pinBuiltSource.Keys)
            {
                if (!next.ContainsKey(builtKey))
                {
                    _pinBuiltSource.TryRemove(builtKey, out _);
                }
            }
            audioChanged = _pinnedAudio != audio;
            _pinnedAudio = audio;
        }
        LoadPinnedMedia(mediaWanted, mediaId, mediaSeq);
        // With no pins, a settings write leaves capture alone unless this cleared it.
        if (next.Count > 0 || audioChanged)
        {
            ReconcileAudioCapture();
        }
    }

    // The loaded flag changes the tag when a retried load succeeds for the same id.
    private static string MediaTag(string id, bool loaded = false) => "md|" + id + (loaded ? "|1" : "");

    private readonly object _mediaLoadLock = new();
    private long _mediaSeq;
    private long _mediaPublishedSeq;

    /// <summary>
    /// Loads the media item media pins play (meta and frame bytes) on the calling
    /// thread, only when a media pin exists and the item changed, so the engine
    /// thread never touches the disk. The inputs come from a locked settings
    /// snapshot taken by the caller; <paramref name="seq"/> orders snapshots so a
    /// slow load of an older one never publishes over a newer one. The snapshot
    /// is kept while global Media is active, so switching away does not rebuild
    /// pins from black. A failed load is retried on the next refresh. Nothing in
    /// here touches the store, so no change notification fires under the lock.
    /// </summary>
    private void LoadPinnedMedia(bool wanted, string id, long seq)
    {
        lock (_mediaLoadLock)
        {
            if (seq < _mediaPublishedSeq)
            {
                return;
            }
            _mediaPublishedSeq = seq;
            if (!wanted)
            {
                _pinnedMedia = null;
                return;
            }
            if (_pinnedMedia is { } current && current.Id == id && (current.Frames is not null || id.Length == 0))
            {
                return;
            }
            var item = id.Length == 0 ? null : _media.GetItem(id);
            byte[]? frames = null;
            if (item is not null)
            {
                try
                {
                    var path = _media.GetFramesBinPath(id);
                    if (System.IO.File.Exists(path))
                    {
                        frames = System.IO.File.ReadAllBytes(path);
                    }
                }
                catch (Exception)
                {
                    frames = null;
                }
            }
            _pinnedMedia = frames is null || item is null
                ? new PinnedMedia(id, MediaTag(id), 0, 0, 0, null)
                : new PinnedMedia(id, MediaTag(id, loaded: true), item.Width, item.Height, item.Fps, frames);
        }
    }

    /// <summary>
    /// Builds, or updates in place, the source a pinned device runs: the shared
    /// screen-capture effect for a mirror pin, the shared Media item for a media
    /// pin, else the pinned Animate shader. In place updates mirror
    /// <see cref="ReapplyActiveLook"/>, so a slider drag does not recompile the
    /// shader.
    /// </summary>
    private IEffect? BuildPinnedEffect(string effect, IEffect? existing)
    {
        if (effect == LightingEngine.MirrorPinKey)
        {
            // A monitor change since the instance was built needs a new capture.
            // One read, so the tag always names the monitor the instance captures.
            var sel = _mirrorSel;
            var monitor = sel.Monitor;
            var mirrorTag = sel.Tag;
            if (existing is not null && mirrorTag == _builtMirrorTag)
            {
                return existing;
            }
            _builtMirrorTag = mirrorTag;
            return new ScreenMirrorEffect(monitor, _screenPP, ScreenSource(), _gpu);
        }
        if (effect == LightingEngine.MediaPinKey)
        {
            return BuildPinnedMedia(existing);
        }
        if (!_pinnedLooks.TryGetValue(effect, out var pinned))
        {
            return null;
        }
        var look = pinned.State;
        // The engine's key may carry the pin's sensor picks; the shader is the effect's own.
        var renderKey = effect;
        effect = pinned.Effect;
        var speed = (float)(look.Speed / 50.0);
        var intensity = look.Intensity > 0 ? look.Intensity : 1f;
        var extras = look.Params is { Count: > 0 }
            ? new System.Collections.Generic.Dictionary<string, float>(look.Params)
            : null;
        // A custom effect updates in place unless its source changed since the instance was built.
        var custom = ShaderLibrary.IsCustomKey(effect);
        var sourceTag = custom ? ShaderLibrary.SourceTag(effect) : null;
        if (existing is ShaderEffect cur && cur.Name == effect
            && (!custom || (_pinBuiltSource.TryGetValue(renderKey, out var builtFrom) && builtFrom == sourceTag)))
        {
            cur.Speed = effect == "pulse" ? speed * 0.5f : speed;
            cur.Intensity = intensity;
            cur.Hue = look.Hue;
            cur.Colorize = look.Colorize;
            cur.Saturation = look.Saturation;
            cur.Contrast = look.Contrast;
            cur.ExtraParams = extras;
            cur.SensorOverrides = look.Sensors;
            return cur;
        }
        if (custom)
        {
            _pinBuiltSource[renderKey] = sourceTag!;
        }
        return BuildAnimateEffect(effect, speed, intensity, look.Hue, look.Colorize, look.Saturation, look.Contrast, extras, look.Sensors);
    }

    // Source tag each pinned custom instance was compiled from; only the engine thread writes it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pinBuiltSource = new(System.StringComparer.Ordinal);

    private string? _builtMirrorTag;
    private string? _builtMediaTag;

    /// <summary>
    /// The one Media source for a media pin: the item global Media mode plays,
    /// through the same post-process. With no playable item it renders what idle
    /// Media does. A new item since the instance was built needs a new one.
    /// </summary>
    private IEffect BuildPinnedMedia(IEffect? existing)
    {
        // One read, so the tag always names the item the instance plays.
        var snapshot = _pinnedMedia;
        var tag = snapshot?.Tag ?? EmptyMediaTag;
        if (existing is not null && tag == _builtMediaTag)
        {
            return existing;
        }
        _builtMediaTag = tag;
        if (snapshot?.Frames is { } frames)
        {
            return new MediaFramesEffect(frames, snapshot.Width, snapshot.Height, snapshot.Fps, _mediaPP);
        }
        return new Engine.Effects.BlackEffect();
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
        cur.SensorOverrides = Nexus.Service.Persistence.SensorBinding.CopyMap(slot.Sensors);
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
        sb.Append(Nexus.Service.Persistence.SensorBinding.MapKey(slot.Sensors));
        uint h = 2166136261;
        foreach (var c in sb.ToString())
        {
            h ^= c;
            h *= 16777619;
        }
        return h.ToString("x8");
    }

    // A thumbnail is one frame with no live reading behind it; sensor uniforms sit at this fill so the tile shows the effect partway.
    private const float ThumbnailSensorValue = 0.65f;

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
        "breathing" => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "clockfill" => new(0.00f, 0.00f, 50f, 1.00f, 1.00f, 1f),
        "fire" => new(0.03f, 0.80f, 70f, 1.10f, 1.05f, 1f),
        "plasma" => new(0.85f, 0.30f, 60f, 1.00f, 1.00f, 1f),
        "spiral" => new(0.00f, 0.00f, 55f, 1.00f, 1.00f, 1f),
        "matrix" => new(0.33f, 0.75f, 80f, 1.10f, 1.10f, 1f),
        "meteor" => new(0.10f, 0.40f, 85f, 1.05f, 1.00f, 1f),
        "ripple" => new(0.55f, 0.30f, 55f, 1.00f, 1.00f, 1f),
        "wave" => new(0.58f, 0.45f, 50f, 1.00f, 1.00f, 1f),
        "gradientwave" => new(0.00f, 0.00f, 40f, 1.00f, 1.00f, 1f),
        "ball" => new(0.06f, 0.70f, 60f, 1.05f, 1.00f, 1f),
        "radar" => new(0.33f, 0.60f, 55f, 1.05f, 1.05f, 1f),
        "pulse" => new(0.55f, 0.45f, 60f, 1.00f, 1.00f, 1f),
        "watercolor" => new(0.50f, 0.30f, 40f, 1.00f, 1.00f, 1f),
        "jellyfish" => new(0.48f, 0.35f, 45f, 1.00f, 1.00f, 1f),
        "aurora" => new(0.00f, 0.00f, 40f, 1.00f, 1.00f, 1f),
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
        "lavaribbons" => new(0.85f, 0.00f, 45f, 1.20f, 1.10f, 1f),
        "lavahaze" => new(0.75f, 0.00f, 40f, 1.20f, 1.05f, 1f),
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

    private IEffect BuildAnimateEffect(string name, float speed, float intensity, float hue, float colorize, float saturation, float contrast, System.Collections.Generic.Dictionary<string, float>? extras, System.Collections.Generic.IReadOnlyDictionary<string, Nexus.Service.Persistence.SensorBinding>? sensors = null)
    {
        var effect = BuildAnimateEffectCore(name, speed, intensity, hue, colorize, saturation, contrast, extras);
        if (effect is ShaderEffect shader)
        {
            shader.SensorOverrides = sensors;
        }
        return effect;
    }

    private IEffect BuildAnimateEffectCore(string name, float speed, float intensity, float hue, float colorize, float saturation, float contrast, System.Collections.Generic.Dictionary<string, float>? extras)
    {
        // Every animate mode is a fragment-only GLSL shader. GPU-only - if
        // the context can't initialise, the canvas stays dark. Pulse gets
        // a 0.5x speed scale to match the legacy feel.
        var effectSpeed = name == "pulse" ? speed * 0.5f : speed;
        // A user-made effect carries its whole source; one the store no longer
        // holds falls back to rainbow like any other unknown key.
        if (ShaderLibrary.IsCustomKey(name))
        {
            string customSrc;
            try
            {
                customSrc = ShaderLibrary.Get(name);
            }
            catch (System.IO.FileNotFoundException)
            {
                customSrc = ShaderLibrary.Rainbow;
            }
            return MakeShader(name, customSrc, effectSpeed, intensity, hue, colorize, saturation, contrast, extras);
        }
        // Simple solid-colour fills all share one cheap shader; the colour is
        // carried by the post-process tint, not the GLSL.
        if (name.StartsWith("simple", System.StringComparison.Ordinal))
        {
            return MakeShader(name, ShaderLibrary.Get(name), effectSpeed, intensity, hue, colorize, saturation, contrast, extras);
        }
        // Simple mode's sweep set loads its own .frag by name, same as the
        // static patterns below - the switch has no arm for them and its
        // rainbow default would render them all identically.
        if (ShaderLibrary.IsSweepEffect(name))
        {
            return MakeShader(name, ShaderLibrary.Get(name), effectSpeed, intensity, hue, colorize, saturation, contrast, extras);
        }
        // A built-in sensor effect is a self-contained baked fragment loaded by its own name; the
        // switch below has no arm for it and would run rainbow in its place.
        if (ShaderLibrary.IsSensorEffect(name))
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
            "lavaribbons" => ShaderLibrary.Get("lavaribbons"),
            "lavahaze" => ShaderLibrary.Get("lavahaze"),
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
            "breathing" => ShaderLibrary.Get("breathing"),
            "clockfill" => ShaderLibrary.Get("clockfill"),
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
        if (body.KeepMode
            && !string.Equals(_store.Load().Lighting.Sync, "screen", StringComparison.OrdinalIgnoreCase))
        {
            // Only the shared capture's screen moves; mirror pins rebuild on the new tag.
            if (body.Monitor is not null)
            {
                _store.Update(s => s.Lighting.ScreenMonitor = body.Monitor);
                _mirrorMonitor = body.Monitor;
            }
            return;
        }
        EnsureRgbActive();
        // Do NOT overwrite _screenPP from body. The post-process holder is the
        // authoritative live state; it was loaded from settings at construction
        // and is mutated only by the /lighting/screen/effect endpoint. If a
        // client calls /start with empty post-process fields before it has
        // fetched the current values, overwriting here would silently clobber
        // the user's saved look back to identity on every mode swap.
        if (body.Monitor is not null)
        {
            // Persist first: the settings-change handler re-reads the monitor from the store.
            _store.Update(s => s.Lighting.ScreenMonitor = body.Monitor);
            _mirrorMonitor = body.Monitor;
        }
        _engine.SetEffect(new ScreenMirrorEffect(_mirrorMonitor, _screenPP, ScreenSource(), _gpu));
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

    public bool StartMedia(string mediaId, bool keepMode)
    {
        if (!keepMode)
        {
            return StartMedia(mediaId);
        }
        if (!_gates.Lighting)
        {
            return false;
        }
        // Global Media is showing the old item, so it follows the change.
        if (string.Equals(_store.Load().Lighting.Sync, "media", StringComparison.OrdinalIgnoreCase))
        {
            return StartMedia(mediaId);
        }
        if (_media.GetItem(mediaId) is null || !System.IO.File.Exists(_media.GetFramesBinPath(mediaId)))
        {
            return false;
        }
        _store.Update(s =>
        {
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
        _studioTimer?.Dispose();
        _previewStreamer?.Dispose();
        _rgb?.Dispose();
        _engine.Dispose();
    }
}
