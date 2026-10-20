using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine.Effects;

namespace Nexus.Service.Lighting.Engine;

public sealed partial class LightingEngine : IDisposable
{
    private readonly CanvasBuffer _canvas;
    private readonly CanvasBuffer _aheadCanvas;
    // The canvas the device sampler reads: the live one, or the look-ahead one
    // while a future frame is sampled. Engine thread only.
    private CanvasBuffer _sampleCanvas;
    private readonly System.Collections.Concurrent.ConcurrentQueue<AheadRequest> _aheadRequests = new();
    private AheadRequest? _aheadActive;

    // Look-ahead render time per tick: a whole window at once pushed the next
    // live tick past its deadline for every device.
    private const double AheadBudgetMs = 12;

    // Look-ahead waits this long after a live shader param change (a slider
    // drag), so a window never mixes old and new params and drags stream live.
    private const int AheadParamSettleMs = 300;
    private int _aheadParamsSig;
    private long _aheadParamsChangedMs;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile IEffect? _currentEffect;
    private volatile DeviceFrame[] _devices = Array.Empty<DeviceFrame>();
    private volatile bool _paused;
    private volatile bool _frozen;
    private volatile bool _blackout;
    // Scale applied to the blackout baseline while the hold is engaged: 1 = the
    // frame the devices were showing when it engaged, 0 = black. Only read while
    // _blackout is true.
    private volatile float _blackoutLevel = 1f;
    // Fade window. The level is derived from this clock by whoever publishes the
    // frame, never pushed in from another thread: a setter on its own timer beats
    // against the render loop's, and each collision republishes a level, which
    // reads as the ramp pausing partway down.
    private long _fadeStartedMs;
    private volatile int _fadeDurationMs;
    // Release ramp, and deliberately not the same clock as the fade above: it
    // scales the LIVE frame - the loop renders again while it runs - instead of
    // the captured baseline, so reaching level 1 lands on exactly what the
    // effect is already publishing and the release cannot step.
    private long _wakeStartedMs;
    private volatile int _wakeDurationMs;
    // What each device was publishing when the hold engaged. Levels scale THIS,
    // not the live frame, so steps cannot compound. Replaced wholesale and never
    // mutated after, so the render loop reads it without taking _lock.
    private Dictionary<DeviceFrame, byte[]>? _blackoutBaseline;
    // Set by whichever path actually publishes the black frame, so a caller on
    // the OS suspend path can wait for the hardware-visible state rather than
    // for a flag it just set itself.
    private readonly ManualResetEventSlim _blackoutApplied = new(false);
    private bool _disposed;
    // Bumped by every invalidate; the loop latches it before rendering and only
    // marks the frame current if no invalidate landed mid-render.
    private int _frozenEpoch;
    private int _frozenRenderedEpoch = -1;
    private byte[] _frameBuffer = Array.Empty<byte>();

    /// <summary>Logical canvas size that device frames and the 3D scene camera are laid out in.</summary>
    public const float CanvasUnitsW = 1000f;
    public const float CanvasUnitsH = 600f;

    public LightingEngine()
    {
        _canvas = new CanvasBuffer(CanvasBuffer.DefaultWidth, CanvasBuffer.DefaultHeight);
        _aheadCanvas = new CanvasBuffer(_canvas.Width, _canvas.Height);
        _sampleCanvas = _canvas;
    }

    public int FrameIntervalMs { get; set; } = 33;

    /// <summary>
    /// Side of the sampling spot at an LED, as a fraction of the canvas width.
    /// An LED reads the plain mean of a spot this size centered on its sample position, clamped at the canvas edges and
    /// never larger than the LED's own cell.
    /// </summary>
    internal const float SpotSideOfCanvasWidth = 3f / 160f;

    /// <summary>
    /// Every device samples the WHOLE canvas instead of the rect its layout
    /// gives it, so each one shows the entire pattern rather than the slice its
    /// frame covers. The saved layout is untouched - only this pass ignores it.
    /// Written from the request thread, read by the render thread.
    /// </summary>
    public volatile bool FullFrameSampling;

    /// <summary>Fraction of the canvas height a linear strip integrates under
    /// <see cref="FullFrameSampling"/>; wide enough to reject single-pixel
    /// noise, narrow enough to keep a band's edge.</summary>
    internal const float FullFrameStripBreadth = 0.08f;

    // Shader Studio live preview: painted over every device, above pins and the running effect, below the
    // LED mapper's highlight and test pattern and a blackout hold. Memory only.
    private volatile IEffect? _studioOverlay;
    private CanvasBuffer? _studioCanvas;

    /// <summary>True while a studio overlay is set.</summary>
    public bool StudioOverlayActive => _studioOverlay is not null;

    /// <summary>Sets (or with null clears) the studio overlay and returns the previous one, which the caller disposes.</summary>
    public IEffect? SetStudioOverlay(IEffect? overlay)
    {
        var old = Interlocked.Exchange(ref _studioOverlay, overlay);
        if (overlay is null && old is not null)
        {
            // A paused effect publishes nothing on its own: the devices (and a hold's baseline) still carry the overlay's frame.
            _resampleOnce = true;
        }
        return old;
    }

    // Set when the overlay clears; the next paused tick outside a hold repaints the effect's own frame once.
    private volatile bool _resampleOnce;

    // Tick and effect of the last live render, so a paused repaint shows the frame the pause held. Loop thread only.
    private long _lastRenderMs;
    private IEffect? _lastRenderEffect;

    public event Action<ReadOnlyMemory<byte>>? OnFrame;
    public event Action? OnEffectChanged;

    /// <summary>Raised on the engine thread once per tick, after every device frame is published; handlers must not block.</summary>
    public event Action? FramePublished;
    public string CurrentEffectName => _currentEffect?.Name ?? "none";
    public IEffect? CurrentEffect => _currentEffect;
    public DeviceFrame[] Devices => _devices;
    public bool Paused => _paused;
    public bool Frozen => _frozen;
    public bool Blackout => _blackout;

    /// <summary>True while a hold is ramping back up, so a caller can tell a
    /// blackout that is on its way out from one that is parked at black.</summary>
    public bool BlackoutReleasing => _wakeDurationMs > 0;

    /// <summary>
    /// True when the render loop is publishing frames. A ramp is only visible
    /// while this holds: the loop is what paints it and what feeds
    /// <see cref="OnFrame"/>.
    /// </summary>
    public bool LoopPublishing
    {
        get
        {
            lock (_lock)
            {
                return _currentEffect is not null && _loopTask is not null && !_loopTask.IsCompleted;
            }
        }
    }
    public void UpdateDevices(DeviceFrame[] devices) { _devices = devices; }

    // Devices placed in the 3D scene and the camera that sees them; null means
    // every device samples its 2D frame. Replaced wholesale, never mutated.
    private volatile Nexus.Service.Lighting.Scene.SceneProjection? _scene;
    public Nexus.Service.Lighting.Scene.SceneProjection? Scene => _scene;
    public void SetScene(Nexus.Service.Lighting.Scene.SceneProjection? scene) => _scene = scene;

    // Which part of its frame each stacked device samples; null or a miss
    // means the whole frame. Replaced wholesale on every stack save.
    private volatile Dictionary<string, Nexus.Service.Lighting.StackSlots.Slot>? _stackSlots;
    public void SetStackSlots(IReadOnlyList<Nexus.Service.Persistence.DeviceGroup> stacks)
    {
        _stackSlots = Nexus.Service.Lighting.StackSlots.Index(stacks);
    }

    /// <summary>Per-LED colour locks, painted after every other pass. Null in tests.</summary>
    public Nexus.Service.Lighting.LedColorLockTracker? LedColorLocks { get; set; }
    /// <summary>Lights keyboards as keys are pressed; painted after the canvas sample, under the colour locks.</summary>
    public Nexus.Service.Lighting.KeyReactive.KeyReactiveOverlay? KeyReactive { get; set; }

    /// <summary>
    /// Per-device pins. Set once at wire-up; null in tests and any host that
    /// never pins one, in which case sampling is unchanged.
    /// </summary>
    public Nexus.Service.Lighting.DevicePinTracker? Pins { get; set; }

    /// <summary>
    /// Builds the IEffect for a static pin. Supplied by the provider, which
    /// owns shader construction - the engine only renders what it is handed.
    /// </summary>
    public Func<Nexus.Service.Persistence.DevicePin, IEffect?>? StaticPinEffectFactory { get; set; }

    /// <summary>
    /// Content tag of a pinned source's resolved current look, so the engine can
    /// tell when a pinned effect needs updating. The key is an Animate shader key
    /// or <see cref="MirrorPinKey"/>. Supplied by the provider, which owns where
    /// that look lives.
    /// </summary>
    public Func<string, string?>? PinLookTag { get; set; }

    /// <summary>
    /// Builds a pinned effect at its resolved current look. Given the instance
    /// built before (or null), it may update that one in place and return it,
    /// which avoids a shader recompile per slider drag; any other return value
    /// replaces it and the engine disposes the old one.
    /// </summary>
    public Func<string, IEffect?, IEffect?>? PinEffectFactory { get; set; }

    /// <summary>Render key of the screen capture shared by every mirror pin.</summary>
    public const string MirrorPinKey = "@mirror";

    /// <summary>Render key of the one Media source shared by every media pin.</summary>
    public const string MediaPinKey = "@media";

    /// <summary>Name of the global Media effect; a media pin shares its canvas while it runs.</summary>
    public const string MediaEffectName = "media";

    /// <summary>Name of the global Screen Mirror effect; a mirror pin shares its canvas while it runs.</summary>
    public const string ScreenEffectName = "screen";

    // One live instance per distinct pinned source (Animate shader or the mirror capture). Engine thread only: the
    // GL context lives there.
    private sealed class PinRender
    {
        public IEffect? Effect;
        public CanvasBuffer Canvas = null!;
        public string? Tag;
        public bool Failed;
        public bool Rendered;
        public long UsedTick = -1;
        public long RetryAtMs;
    }
    private readonly Dictionary<string, PinRender> _pinRenders = new(StringComparer.Ordinal);
    // Device id to the canvas it samples this tick; a null canvas means the
    // main one. Absent means no animate/mirror pin (or its source failed to render).
    private readonly Dictionary<string, CanvasBuffer?> _pinnedDevices = new(StringComparer.Ordinal);
    // Devices pinned to a Simple-mode sweep: they read the whole canvas, as every
    // device does globally while a sweep is the active effect.
    private readonly HashSet<string> _pinFullFrame = new(StringComparer.Ordinal);
    private long _pinTick;

    // One assignment renders once into this scratch canvas and every device
    // wearing that look samples it. Static is a held frame, so a look is
    // rendered only when its key changes, not per frame.
    private CanvasBuffer? _assignCanvas;
    private readonly Dictionary<string, byte[]> _assignRenders = new(StringComparer.Ordinal);
    // Static-pin shaders whose first frame has not been drawn, kept alive so the compile can finish. Loop thread only.
    private readonly Dictionary<string, IEffect> _assignPending = new(StringComparer.Ordinal);
    // Failed builds per look: when to try again and how many attempts so far.
    private readonly Dictionary<string, (long At, int Attempts)> _assignRetry = new(StringComparer.Ordinal);
    // The last finished look each device painted, held while its next look compiles.
    private readonly Dictionary<string, byte[]> _assignLastGood = new(StringComparer.Ordinal);
    private readonly HashSet<string> _assignRefKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _assignRefDevices = new(StringComparer.Ordinal);
    private int _assignPruneVersion = -1;
    private DeviceFrame[]? _assignPruneDevices;
    internal const long AssignRetryBaseMs = 1000;
    internal const long AssignRetryMaxMs = 60000;
    // The longest build is compile, fallback compile, then link (three phases), each forced after DefaultForceAfterMs; four is deliberate headroom.
    private const int AssignBuildPhases = 4;
    private const long AssignBudgetMarginMs = 5000;
    /// <summary>A compile that has drawn nothing for this long is abandoned and handed to the retry backoff; it outlasts the build's own force timeouts.</summary>
    internal static readonly long DefaultAssignPendingBudgetMs = (long)Gpu.AsyncProgramBuild.DefaultForceAfterMs * AssignBuildPhases + AssignBudgetMarginMs;
    internal long AssignPendingBudgetMs { get; set; } = DefaultAssignPendingBudgetMs;
    /// <summary>A gap between polls longer than this means nothing was polling (blackout, sleep, pause), so the budget starts over.</summary>
    internal long AssignPollGapMs { get; set; } = 2000;
    private readonly Dictionary<string, (long Since, long Polled)> _assignPendingSince = new(StringComparer.Ordinal);
    // Reused per-frame scratch: which devices ApplyTestOverlays painted. Valid
    // for the rest of the tick after SampleDevicesFromCanvas returns.
    private bool[] _overlaid = Array.Empty<bool>();
    // The subset of _overlaid that key reactions must not paint over. A static pin
    // owns its device's colors yet is the base a reaction composes onto.
    private bool[] _reactionBlocked = Array.Empty<bool>();

    /// <summary>
    /// Freezes or resumes the render loop for the current effect; a no-op with
    /// no active effect. Shares _lock with SetEffect/Stop so a pause request
    /// racing an effect transition cannot set the flag after that transition
    /// already cleared it.
    /// </summary>
    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (_currentEffect is null)
            {
                return;
            }
            _paused = paused;
        }
    }

    /// <summary>
    /// Holds every device frame at black without disturbing the active effect,
    /// so the host-sleep blackout releases straight back into whatever was
    /// running. Distinct from <see cref="SetPaused"/> on both counts that
    /// matter here: pause holds the last LIT frame, and pause is a no-op with
    /// no effect running - blackout has to reach devices either way, because
    /// what it exists to fix is hardware that keeps showing its last frame.
    ///
    /// Nothing here is persisted, so a crash or power loss while blacked out
    /// comes back lit rather than stranding the user in the dark.
    /// </summary>
    public void SetBlackout(bool blackout)
    {
        var applyInline = false;
        lock (_lock)
        {
            if (!blackout)
            {
                if (!_blackout)
                {
                    return;
                }
                ReleaseBlackoutState();
                // A frozen effect renders once per epoch; without this it would
                // hold the blacked-out canvas after release instead of repainting.
                Interlocked.Increment(ref _frozenEpoch);
                return;
            }

            if (!_blackout)
            {
                _blackoutBaseline = CaptureBlackoutBaseline();
                _blackout = true;
            }
            // Ends any ramp: this is the cut, and it is also what the fade path
            // calls once its window is spent.
            _fadeDurationMs = 0;
            _wakeDurationMs = 0;
            _blackoutLevel = 0f;
            Interlocked.Increment(ref _frozenEpoch);
            // With the loop running it owns the device buffers; writing them
            // from this thread would race its paint and could publish a torn
            // frame. Without it, nothing else writes them, so apply here - and
            // that is the path that matters, since a device holding its last
            // frame is exactly the case where no effect is running.
            applyInline = _currentEffect is null || _loopTask is null || _loopTask.IsCompleted;
        }
        if (applyInline)
        {
            ApplyBlackout();
        }
    }

    /// <summary>
    /// Engages the hold and ramps to black over <paramref name="duration"/>, the
    /// level recomputed on every published frame. False when a hold is already
    /// engaged, changing nothing: a second suspend notification mid-ramp must
    /// not restart it bright. Wait on <see cref="WaitForBlackout"/> for the end.
    /// </summary>
    public bool BeginBlackoutFade(TimeSpan duration)
    {
        var ms = (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue);
        if (ms <= 0)
        {
            // No window to ramp over; a hold at level 1 would park every device
            // at full brightness with nothing to move it.
            SetBlackout(true);
            return true;
        }
        lock (_lock)
        {
            if (_blackout && _wakeDurationMs <= 0)
            {
                return false;
            }
            // A hold already ramping back up re-arms downwards from where it
            // is: the baseline re-capture below reads the dimmed frame the
            // release ramp last published, so a lock landing mid-release fades
            // down from there instead of jumping to full brightness first.
            _wakeDurationMs = 0;
            _blackoutBaseline = CaptureBlackoutBaseline();
            Volatile.Write(ref _fadeStartedMs, Environment.TickCount64);
            _fadeDurationMs = ms;
            _blackoutLevel = 1f;
            // The ramp has not reached black; a signal left set by an earlier
            // hold would tell a waiter it already had.
            ResetBlackoutSignal();
            _blackout = true;
            Interlocked.Increment(ref _frozenEpoch);
        }
        return true;
    }

    /// <summary>
    /// Level for right now: derived from the ramp's clock while one is running,
    /// otherwise whatever level was set explicitly. Derived and never written
    /// back - a write-back from the render thread can land after
    /// <see cref="SetBlackout"/> has set the cut under _lock, restoring a dim
    /// level that then sticks because the ramp is over.
    /// </summary>
    private float CurrentFadeLevel()
    {
        var durationMs = _fadeDurationMs;
        if (durationMs <= 0)
        {
            return _blackoutLevel;
        }
        var elapsed = Environment.TickCount64 - Volatile.Read(ref _fadeStartedMs);
        if (elapsed >= durationMs)
        {
            return 0f;
        }
        // Monotone because elapsed only grows. Squared: the byte we write is
        // roughly linear in emitted light, perception is not, so a linear ramp
        // reads as a hard drop that then crawls.
        var t = 1f - (float)elapsed / durationMs;
        return t * t;
    }

    /// <summary>
    /// Releases the hold by ramping the live effect back up from black over
    /// <paramref name="duration"/>, instead of cutting straight to it. For the
    /// lock path, where the host stays up and nothing is racing a teardown, so
    /// the ramp is free to be as long as it looks good.
    ///
    /// False when no hold is engaged or one is already ramping up. Falls back
    /// to the cut when nothing would paint the ramp - no effect, no loop, or no
    /// window - which is what a release has always done.
    /// </summary>
    public bool BeginBlackoutRelease(TimeSpan duration)
    {
        var ms = (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue);
        lock (_lock)
        {
            if (!_blackout)
            {
                return false;
            }
            if (_wakeDurationMs > 0)
            {
                // Already on its way up; a second unlock notification must not
                // restart the ramp from black.
                return true;
            }
            if (ms <= 0 || _currentEffect is null || _loopTask is null || _loopTask.IsCompleted)
            {
                // Nothing will paint the ramp, and nothing will repaint after
                // the drop either, so put the captured frame back by hand.
                RestoreBlackoutBaseline();
                SetBlackout(false);
                return true;
            }
            // Start the ramp at whatever level is on the devices now, not at 0:
            // unlocking mid-fade-down would otherwise drop the lights the rest
            // of the way to black before bringing them up. Inverting the level
            // curve (t squared) gives the elapsed offset that starts there.
            var level = Math.Clamp(CurrentFadeLevel(), 0f, 1f);
            var offsetMs = (long)(Math.Sqrt(level) * ms);
            Volatile.Write(ref _wakeStartedMs, Environment.TickCount64 - offsetMs);
            _wakeDurationMs = ms;
        }
        return true;
    }

    /// <summary>
    /// Drops the hold once the release ramp has reached full. Re-checked under
    /// the lock rather than from the level the loop just published: a lock
    /// landing mid-release cancels the ramp, and acting on the pre-cancel read
    /// would release the hold the lock just re-armed - lights back on, locked
    /// machine.
    /// </summary>
    private void CompleteReleaseIfDone()
    {
        lock (_lock)
        {
            if (_wakeDurationMs > 0 && CurrentWakeLevel() >= 1f)
            {
                SetBlackout(false);
            }
        }
    }

    /// <summary>
    /// Level for the release ramp, on the same derive-never-write-back terms as
    /// <see cref="CurrentFadeLevel"/>. 1 whenever no ramp is running, so the
    /// loop can scale by it unconditionally.
    /// </summary>
    private float CurrentWakeLevel() => CurrentWakeLevel(_wakeDurationMs);

    /// <summary>
    /// Overload taking a duration the caller has already read. The loop uses it
    /// so the level and the "is a ramp running" decision come from ONE read: a
    /// lock cancelling the ramp between two reads would otherwise return 1 to a
    /// caller that had decided a ramp was running, publishing one frame at full
    /// brightness on the way down.
    /// </summary>
    private float CurrentWakeLevel(int durationMs)
    {
        if (durationMs <= 0)
        {
            return 1f;
        }
        var elapsed = Environment.TickCount64 - Volatile.Read(ref _wakeStartedMs);
        if (elapsed >= durationMs)
        {
            return 1f;
        }
        if (elapsed <= 0)
        {
            return 0f;
        }
        // Squared, mirroring the fade-out curve for the same reason: the byte is
        // roughly linear in emitted light and perception is not.
        var t = (float)elapsed / durationMs;
        return t * t;
    }

    /// <summary>
    /// Blocks until an all-black frame has actually been published to the
    /// device buffers. False on timeout, in which case the caller should assume
    /// the hardware is still lit.
    /// </summary>
    public bool WaitForBlackout(TimeSpan timeout) => _blackoutApplied.Wait(timeout);

    private void ResetBlackoutSignal()
    {
        try { _blackoutApplied.Reset(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Republishes the frame the hold captured, at full level. For the paths
    /// where the hold is being dropped and NOTHING will repaint - paused, or no
    /// effect running - which would otherwise leave the devices sitting on the
    /// black frame the hold published.
    /// </summary>
    private void RestoreBlackoutBaseline()
    {
        var baseline = Volatile.Read(ref _blackoutBaseline);
        if (baseline is null)
        {
            return;
        }
        foreach (var dev in _devices)
        {
            if (!baseline.TryGetValue(dev, out var frame))
            {
                continue;
            }
            for (int i = 0, led = 0; i + 2 < frame.Length; i += 3, led++)
            {
                dev.SetLed(led, frame[i], frame[i + 1], frame[i + 2]);
            }
            dev.Publish();
        }
    }

    /// <summary>Call under <see cref="_lock"/>. Drops the hold and everything it captured.</summary>
    private void ReleaseBlackoutState()
    {
        _blackout = false;
        _blackoutLevel = 1f;
        _fadeDurationMs = 0;
        _wakeDurationMs = 0;
        Volatile.Write(ref _blackoutBaseline, null);
        ResetBlackoutSignal();
    }

    private Dictionary<DeviceFrame, byte[]> CaptureBlackoutBaseline()
    {
        var devices = _devices;
        var baseline = new Dictionary<DeviceFrame, byte[]>(devices.Length);
        foreach (var dev in devices)
        {
            baseline[dev] = dev.LedBytes.ToArray();
        }
        return baseline;
    }

    /// <summary>
    /// Publishes the hold's current level to every device. Re-run every tick, so
    /// the ramp advances and hardware arriving mid-hold is caught too.
    /// </summary>
    private void ApplyBlackout()
    {
        var level = CurrentFadeLevel();
        var baseline = Volatile.Read(ref _blackoutBaseline);
        if (level <= 0f || baseline is null)
        {
            _canvas.Clear();
            foreach (var dev in _devices)
            {
                dev.Clear();
            }
            // Only full black satisfies a WaitForBlackout caller: it is waiting
            // to know the hardware is dark, not that a fade is under way.
            _blackoutApplied.Set();
            return;
        }
        foreach (var dev in _devices)
        {
            if (!baseline.TryGetValue(dev, out var frame))
            {
                // Arrived after the hold engaged, so it was never part of the
                // fade: black it outright rather than lighting it up mid-suspend.
                dev.Clear();
                continue;
            }
            for (int i = 0, led = 0; i + 2 < frame.Length; i += 3, led++)
            {
                dev.SetLed(led, (byte)(frame[i] * level), (byte)(frame[i + 1] * level), (byte)(frame[i + 2] * level));
            }
            dev.Publish();
        }
    }

    /// <summary>
    /// Marks the current effect as time-invariant, so the loop renders one frame
    /// and then reuses the canvas instead of re-running the shader every tick.
    /// Device sampling and broadcast continue, so layout, brightness and
    /// device-arrival changes still reach hardware while frozen. Cleared by
    /// <see cref="SetEffect"/>; callers that mutate uniforms in place must call
    /// <see cref="InvalidateFrozenFrame"/>.
    /// </summary>
    public void SetFrozen(bool frozen)
    {
        lock (_lock)
        {
            if (_currentEffect is null)
            {
                return;
            }
            _frozen = frozen;
            Interlocked.Increment(ref _frozenEpoch);
        }
    }

    public void InvalidateFrozenFrame() => Interlocked.Increment(ref _frozenEpoch);

    public void SetEffect(IEffect effect)
    {
        lock (_lock)
        {
            var old = _currentEffect;
            // Queued before the new effect is published, so the render thread never sees it without its handover.
            var retired = RetireOutgoing(effect, old);
            _currentEffect = effect;
            _paused = false;
            _frozen = false;
            // The user picking a mode is the escape hatch if a resume event
            // never lands: it always ends in a lit device, never a dark one.
            ReleaseBlackoutState();
            Interlocked.Increment(ref _frozenEpoch);
            try
            {
                if (!retired)
                {
                    old?.Dispose();
                }
            }
            catch { }
            if (_loopTask is null || _loopTask.IsCompleted)
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                // Dedicated thread: on Windows the loop blocks on its high-resolution timer instead of awaiting.
                _loopTask = Task.Factory.StartNew(
                    () => RunLoopAsync(token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            }
        }
        // Handler may start/stop an audio-capture subprocess; must not run under the engine lock.
        OnEffectChanged?.Invoke();
    }

    /// <summary>
    /// Replace <paramref name="expected"/> with <paramref name="replacement"/> only if it is
    /// still the running effect, under the engine lock. Paused, frozen and blackout state
    /// are left as they are, unlike <see cref="SetEffect"/>. Returns false (replacement
    /// untouched) when another effect took over meanwhile.
    /// </summary>
    public bool SwapEffectIfCurrent(IEffect expected, IEffect replacement)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_currentEffect, expected))
            {
                return false;
            }
            var retired = RetireOutgoing(replacement, expected);
            _currentEffect = replacement;
            try
            {
                if (!retired)
                {
                    expected.Dispose();
                }
            }
            catch { }
        }
        OnEffectChanged?.Invoke();
        return true;
    }

    // Shader effects leaving the stage, with the effect replacing each. The render thread settles these at the top of a
    // tick: the incoming shader takes over drawing the outgoing one until its own program lands (shader to shader, so a
    // switch or a custom-shader save keeps animating), otherwise the outgoing one is disposed. Doing it there, not on the
    // calling thread, keeps adoption ordered against the frames that read both effects. An outgoing effect that is not a
    // shader is disposed at once: the incoming shader holds the canvas's last frame until it lands, because such an
    // effect may own capture sources that stop with the switch.
    private readonly Queue<(IEffect Incoming, Gpu.ShaderEffect Outgoing)> _handovers = new();

    private bool RetireOutgoing(IEffect incoming, IEffect? outgoing)
    {
        if (outgoing is not Gpu.ShaderEffect shader)
        {
            return false;
        }
        lock (_handovers)
        {
            _handovers.Enqueue((incoming, shader));
        }
        return true;
    }

    private bool HandoversPending
    {
        get
        {
            lock (_handovers)
            {
                return _handovers.Count > 0;
            }
        }
    }

    private void SettleHandovers()
    {
        while (true)
        {
            (IEffect Incoming, Gpu.ShaderEffect Outgoing) item;
            lock (_handovers)
            {
                if (!_handovers.TryDequeue(out item))
                {
                    return;
                }
            }
            try
            {
                if (!(item.Incoming is Gpu.ShaderEffect incoming && incoming.AdoptPredecessor(item.Outgoing)))
                {
                    item.Outgoing.Dispose();
                }
            }
            catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            var old = _currentEffect;
            _currentEffect = null;
            _paused = false;
            _frozen = false;
            ReleaseBlackoutState();
            Interlocked.Increment(ref _frozenEpoch);
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try
            { old?.Dispose(); }
            catch { }
            lock (_handovers)
            {
                while (_handovers.TryDequeue(out var pending))
                {
                    try
                    { pending.Outgoing.Dispose(); }
                    catch { }
                }
            }
            _canvas.Clear();
            foreach (var dev in _devices)
            {
                dev.Clear();
            }

            try
            { SerializeAndBroadcast(); }
            catch { }
        }
        // Handler may start/stop an audio-capture subprocess; must not run under the engine lock.
        OnEffectChanged?.Invoke();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Windows paces on the high-resolution ticker; elsewhere PeriodicTimer
        // allocates once per loop vs Task.Delay allocating a fresh Task every
        // frame. Period is reloaded each tick so live FrameIntervalMs changes
        // propagate without restarting the loop.
        var periodMs = Math.Max(1, FrameIntervalMs);
        using var hiRes = OperatingSystem.IsWindows() ? HighResolutionTicker.TryCreate(ct) : null;
        using var timer = hiRes is null ? new PeriodicTimer(TimeSpan.FromMilliseconds(periodMs)) : null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SettleHandovers();
                var effect = _currentEffect;
                // A switch that landed between the settle and the read has its handover queued already.
                if (HandoversPending)
                {
                    SettleHandovers();
                }
                DeviceFrame[]? aheadDevices = null;
                if (effect is null)
                {
                    break;
                }

                try
                {
                    // Paused holds the last rendered canvas/device buffers untouched
                    // and still broadcasts them every tick, so hardware and preview
                    // keep receiving frames without the effect clock advancing.
                    // A hold that is ramping back up takes the render path
                    // below instead: the ramp scales frames the effect is
                    // painting live, not the captured baseline.
                    var wakeMs = _wakeDurationMs;
                    if (_blackout && wakeMs <= 0)
                    {
                        // Re-applied every tick, not once: UpdateDevices can swap
                        // in frames for hardware that arrived mid-blackout.
                        ApplyBlackout();
                    }
                    else if (!_paused)
                    {
                        // A frozen effect paints the same canvas every tick, so
                        // the shader render and its readback run once; sampling
                        // still runs so device changes reach hardware.
                        _resampleOnce = false;
                        var epoch = Volatile.Read(ref _frozenEpoch);
                        // One snapshot for the sample and the publish:
                        // UpdateDevices swaps the array lock-free.
                        var devices = _devices;
                        // The main effect and the pinned looks read back together, closed before any canvas is sampled.
                        var liveBatch = effect is Gpu.ShaderEffect liveShader ? Gpu.ReadbackBatch.TryBegin(liveShader.Context) : null;
                        var markRendered = false;
                        try
                        {
                            if (!_frozen || Volatile.Read(ref _frozenRenderedEpoch) != epoch)
                            {
                                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                _lastRenderMs = nowMs;
                                _lastRenderEffect = effect;
                                effect.RenderFrame(_canvas, _lastRenderMs);
                                // A shader still compiling drew nothing of its own: keep asking until it does.
                                if (effect is not Gpu.ShaderEffect { IsRenderPending: true })
                                {
                                    markRendered = true;
                                }
                            }
                            RenderPins(effect, devices);
                        }
                        finally
                        {
                            liveBatch?.Dispose();
                        }
                        // Only once the readback landed: a reset context leaves the old canvas, which must be drawn again.
                        if (markRendered && liveBatch?.Lost != true)
                        {
                            Volatile.Write(ref _frozenRenderedEpoch, epoch);
                        }
                        // After RenderPins, which updates pinned looks in place.
                        TrackAheadParams(effect);
                        TrackPinAhead(effect);
                        SampleDevicesFromCanvas(devices);
                        ApplyLedColorLocks(devices);
                        if (KeyReactive?.Apply(devices, _reactionBlocked, Environment.TickCount64) == true)
                        {
                            ApplyLedColorLocks(devices);
                        }
                        // Dim as part of publishing, so the ramp is in the one
                        // frame readers see rather than a second write on top of
                        // a published one.
                        var wakeLevel = CurrentWakeLevel(wakeMs);
                        // One publish per tick, after every pass that paints:
                        // readers never see a frame with the canvas sample on
                        // some LEDs and an override on the rest.
                        foreach (var dev in devices)
                        {
                            if (wakeLevel < 1f)
                            {
                                dev.PublishScaled(wakeLevel);
                            }
                            else
                            {
                                dev.Publish();
                            }
                        }
                        if (wakeMs > 0)
                        {
                            CompleteReleaseIfDone();
                        }
                        aheadDevices = devices;
                    }
                    else if ((_studioOverlay is not null || _resampleOnce || AnyAssignPollable()) && wakeMs <= 0)
                    {
                        // Paused: the effect holds, the studio overlay keeps animating over it, and once it clears the effect's paused frame is repainted onto the devices.
                        var devices = _devices;
                        if (_studioOverlay is not null)
                        {
                            ApplyTestOverlays(devices);
                        }
                        else if (!_resampleOnce)
                        {
                            // Only a pending static look holds this branch open: the main effect is not rendered again (input-driven
                            // effects must not animate while paused), only the pins are polled and painted.
                            ApplyTestOverlays(devices);
                            ApplyLedColorLocks(devices);
                        }
                        else
                        {
                            _resampleOnce = false;
                            // Repaint the effect's own frame: the canvas may be stale or cleared by a hold, and the devices may carry overlay pixels.
                            effect.RenderFrame(_canvas, ReferenceEquals(effect, _lastRenderEffect) ? _lastRenderMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            SampleDevicesFromCanvas(devices);
                            ApplyLedColorLocks(devices);
                        }
                        foreach (var dev in devices)
                        {
                            dev.Publish();
                        }
                    }
                    else if (wakeMs > 0)
                    {
                        // Paused mid-ramp: nothing repaints, so the ramp cannot
                        // advance and pause would hold the black frame the hold
                        // published. Put the captured frame back first, then
                        // drop the hold, so pause resumes holding what it held.
                        RestoreBlackoutBaseline();
                        SetBlackout(false);
                    }
                    if (_paused || _blackout)
                    {
                        PrunePinRenders(_devices);
                    }
                    SerializeAndBroadcast();
                    FramePublished?.Invoke();
                    // After the publish, so look-ahead renders never delay this tick's frame.
                    if (aheadDevices is not null)
                    {
                        ServiceAheadRequests(effect, aheadDevices);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[lighting-engine] {effect.Name} threw: {ex.Message}"); }

                var nextPeriodMs = Math.Max(1, FrameIntervalMs);
                if (nextPeriodMs != periodMs)
                {
                    periodMs = nextPeriodMs;
                    timer?.Period = TimeSpan.FromMilliseconds(periodMs);
                }
                if (hiRes is not null)
                {
                    if (!hiRes.WaitForNextTick(periodMs))
                    {
                        break;
                    }
                    continue;
                }
                try
                {
                    if (!await timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            DisposePinRenders();
            DisposeAssignState();
            _canvas.Clear();
            foreach (var dev in _devices)
            {
                dev.Clear();
            }
        }
    }

    private void DisposePinRenders()
    {
        foreach (var r in _pinRenders.Values)
        {
            try { r.Effect?.Dispose(); } catch { /* best-effort */ }
        }
        _pinRenders.Clear();
        _aheadPinCanvases.Clear();
        _pinnedDevices.Clear();
    }

    private static string? PinRenderKey(Nexus.Service.Persistence.DevicePin pin)
    {
        if (pin.Kind == Nexus.Service.Persistence.DevicePinKinds.Animate && !string.IsNullOrEmpty(pin.Effect))
        {
            return Nexus.Service.Lighting.DevicePinLook.AnimateRenderKey(pin);
        }
        if (pin.Kind == Nexus.Service.Persistence.DevicePinKinds.Mirror)
        {
            return MirrorPinKey;
        }
        if (pin.Kind == Nexus.Service.Persistence.DevicePinKinds.Media)
        {
            return MediaPinKey;
        }
        return null;
    }

    /// <summary>
    /// While paused or held black no pin renders, but a render whose pin no
    /// longer exists is disposed so a cleared mirror pin stops screen capture.
    /// Existing pins keep their held frame. Engine thread.
    /// </summary>
    private int _prunedPinsVersion = -1;
    private DeviceFrame[]? _prunedDevices;

    private void PrunePinRenders(DeviceFrame[] devices)
    {
        if (_pinRenders.Count == 0)
        {
            return;
        }
        var tracker = Pins;
        if (tracker is null || !tracker.Any)
        {
            DisposePinRenders();
            return;
        }
        // A held pause costs nothing until the pins or the device set change.
        if (tracker.Version == _prunedPinsVersion && ReferenceEquals(devices, _prunedDevices))
        {
            return;
        }
        _prunedPinsVersion = tracker.Version;
        _prunedDevices = devices;
        var pins = tracker.Snapshot();
        HashSet<string> live = new(StringComparer.Ordinal);
        foreach (var dev in devices)
        {
            if (pins.TryGetValue(dev.Id, out var pin) && PinRenderKey(pin) is { } k)
            {
                live.Add(k);
            }
        }
        List<string>? stale = null;
        foreach (var key in _pinRenders.Keys)
        {
            if (!live.Contains(key)) (stale ??= new List<string>()).Add(key);
        }
        if (stale is not null)
        {
            foreach (var key in stale)
            {
                try { _pinRenders[key].Effect?.Dispose(); } catch { /* best-effort */ }
                RemovePinRender(key);
            }
        }
    }

    private void RemovePinRender(string key)
    {
        _pinRenders.Remove(key);
        _aheadPinCanvases.Remove(key);
    }

    /// <summary>
    /// Renders each distinct pinned source (an Animate shader, the one screen
    /// capture every mirror pin shares, or the one Media item every media pin
    /// shares) that is not the active effect onto its own canvas and records
    /// which canvas every pinned device samples. A device pinned to the active
    /// effect keeps the main canvas. Only pins whose device is present render, so
    /// the mirror capture and the media source run while any pinned device
    /// exists and are disposed when none remain. Engine thread.
    /// </summary>
    private void RenderPins(IEffect active, DeviceFrame[] devices)
    {
        var tracker = Pins;
        _pinnedDevices.Clear();
        _pinFullFrame.Clear();
        if (tracker is null || !tracker.Any)
        {
            if (_pinRenders.Count > 0)
            {
                DisposePinRenders();
            }
            return;
        }
        var factory = PinEffectFactory;
        if (factory is null)
        {
            return;
        }
        var pins = tracker.Snapshot();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tick = ++_pinTick;
        var used = 0;
        var activeName = active.Name;
        foreach (var dev in devices)
        {
            if (!pins.TryGetValue(dev.Id, out var pin))
            {
                continue;
            }
            if (PinRenderKey(pin) is not { } key)
            {
                continue;
            }
            var sweep = key != MirrorPinKey && key != MediaPinKey && Gpu.ShaderLibrary.IsSweepEffect(key);
            // Pinned to what is already running globally: share its canvas.
            var shared = key == MirrorPinKey ? ScreenEffectName : key == MediaPinKey ? MediaEffectName : key;
            if (string.Equals(activeName, shared, StringComparison.Ordinal))
            {
                _pinnedDevices[dev.Id] = null;
                if (sweep) _pinFullFrame.Add(dev.Id);
                continue;
            }
            if (!_pinRenders.TryGetValue(key, out var render))
            {
                render = new PinRender { Canvas = new CanvasBuffer(_canvas.Width, _canvas.Height) };
                _pinRenders[key] = render;
            }
            if (render.UsedTick != tick)
            {
                render.UsedTick = tick;
                used++;
                UpdatePinRender(key, render, factory, nowMs);
                if (!render.Failed && render.Effect is not null)
                {
                    try
                    {
                        render.Effect.RenderFrame(render.Canvas, nowMs);
                        // Still compiling with nothing to stand in: leave the devices on the global canvas instead of a blank one.
                        // A shader that failed outright stays pinned (black) instead of recompiling on every retry.
                        render.Rendered = render.Effect is not Gpu.ShaderEffect { IsRenderPending: true, CanPaint: false };
                    }
                    catch (Exception ex)
                    {
                        // A pinned effect that cannot render must not take the
                        // frame down; its devices fall through to the canvas.
                        render.Failed = true;
                        render.Rendered = false;
                        render.RetryAtMs = nowMs + PinRetryMs;
                        Gpu.GpuContext.Log($"[pin] render failed for {key}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    render.Rendered = false;
                }
            }
            if (render.Rendered)
            {
                _pinnedDevices[dev.Id] = render.Canvas;
                if (sweep) _pinFullFrame.Add(dev.Id);
            }
        }
        if (used < _pinRenders.Count)
        {
            // Drop instances nothing present references any more.
            List<string>? stale = null;
            foreach (var (key, r) in _pinRenders)
            {
                if (r.UsedTick != tick) (stale ??= new List<string>()).Add(key);
            }
            if (stale is not null)
            {
                foreach (var key in stale)
                {
                    try { _pinRenders[key].Effect?.Dispose(); } catch { /* best-effort */ }
                    RemovePinRender(key);
                }
            }
        }
    }

    private const long PinRetryMs = 1000;

    private void UpdatePinRender(string key, PinRender render, Func<string, IEffect?, IEffect?> factory, long nowMs)
    {
        try
        {
            var tag = PinLookTag?.Invoke(key);
            if (render.Effect is not null && !render.Failed && tag is not null && tag == render.Tag)
            {
                return;
            }
            // A failed build or render retries on a changed look, or after a pause.
            if (render.Failed && tag is not null && tag == render.Tag && nowMs < render.RetryAtMs)
            {
                return;
            }
            var previous = render.Failed ? null : render.Effect;
            var built = factory(key, previous);
            if (!ReferenceEquals(built, render.Effect))
            {
                // A shader that has to compile keeps the pin showing the outgoing look until it lands.
                var handedOver = built is Gpu.ShaderEffect incoming && render.Effect is Gpu.ShaderEffect outgoing && incoming.AdoptPredecessor(outgoing);
                if (!handedOver)
                {
                    try { render.Effect?.Dispose(); } catch { /* best-effort */ }
                }
            }
            render.Effect = built;
            render.Tag = tag;
            render.Failed = built is null;
            render.RetryAtMs = nowMs + PinRetryMs;
        }
        catch (Exception ex)
        {
            try { render.Effect?.Dispose(); } catch { /* best-effort */ }
            render.Effect = null;
            render.Failed = true;
            render.RetryAtMs = nowMs + PinRetryMs;
            Gpu.GpuContext.Log($"[pin] build failed for {key}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True when every requested zone shows a pure function of time: its own static pin, its own animate pin on a
    /// shader that reads no audio, or a time-pure global effect when it follows the global. A zone that follows a
    /// global that reads audio, mirrors the screen or plays media, or is pinned to such a source, forces the live
    /// path. Also false while paused, frozen, blacked out, ramping, in an LED editor overlay, key-reactive, or inside
    /// the settle window of a change to anything the window shows.
    /// </summary>
    public bool CanRenderAhead(IReadOnlyList<string> deviceIds)
    {
        var current = _currentEffect;
        if (current is null || _studioOverlay is not null || _paused || _frozen || _blackout || _wakeDurationMs > 0)
        {
            return false;
        }
        var pinState = _pinAhead;
        var needsGlobal = false;
        var animates = false;
        var settledAfter = 0L;
        foreach (var d in _devices)
        {
            if (!deviceIds.Contains(d.Id))
            {
                continue;
            }
            var editing = d.HighlightLeds is { Count: > 0 } || d.TestPattern is not null
                || d.PreviewLayout is not null || d.PreviewLedCount is not null;
            if (editing)
            {
                return false;
            }
            if (pinState.TryGetValue(d.Id, out var pin))
            {
                if (!pin.Ok)
                {
                    return false;
                }
                needsGlobal |= pin.UsesGlobal;
                animates |= pin.Animates;
                settledAfter = Math.Max(settledAfter, pin.ChangedMs);
            }
            else
            {
                needsGlobal = true;
            }
            // Reactions follow live key presses, which a pre-rendered loop cannot know.
            if (KeyReactive?.IsEnabled(Nexus.Service.Lighting.KeyReactive.KeyReactiveOverlay.ConfigKey(d)) == true)
            {
                return false;
            }
        }
        if (needsGlobal)
        {
            if (!IsTimePure(current))
            {
                return false;
            }
            settledAfter = Math.Max(settledAfter, Volatile.Read(ref _aheadParamsChangedMs));
        }
        // Without a shader or an animating pin the content is constant, and the live path sends nothing for it.
        if (!animates && !(needsGlobal && current is Gpu.ShaderEffect))
        {
            return false;
        }
        return Environment.TickCount64 - settledAfter >= AheadParamSettleMs;
    }

    // Shaders that read no audio and the clock-only effects depend on the clock and their settings alone.
    // A shader that failed leaves its canvas as it was, so it is not a source to pre-render.
    private static bool IsTimePure(IEffect? effect) =>
        effect is Effects.IClockOnlyEffect or Gpu.ShaderEffect { ReadsAudio: false, ReadsSensors: false, HasFailed: false };

    // A pinned source that failed to build or render shows the base canvas, as it does live. A shader that failed
    // without throwing keeps its stale canvas live, so IsTimePure rejects it and the chain stays live.
    private static bool PinSourceRendering(PinRender render, out IEffect fx)
    {
        fx = render.Effect!;
        return !render.Failed && render.Rendered && render.Effect is not null;
    }

    // What a render-ahead window needs to know about one pinned device. Sig changes with the pin or its resolved
    // source; ChangedMs restarts that device's settle window; UsesGlobal says the zone shows the global canvas.
    private sealed record PinAheadState(int Sig, long ChangedMs, bool Ok, bool Animates, bool UsesGlobal);
    private volatile Dictionary<string, PinAheadState> _pinAhead = new(StringComparer.Ordinal);
    private bool _pinAheadTombstones;
    private int _pinAheadVersion = -1;
    // The pin version the published state was computed from, to reject a window rendered against a newer pin.
    private int _pinAheadSeenVersion = -1;
    private readonly Dictionary<string, int> _pinHashes = new(StringComparer.Ordinal);

    private (int Sig, bool Ok, bool Animates, bool UsesGlobal) ClassifyPinAhead(
        string id, Nexus.Service.Persistence.DevicePin pin, IEffect active)
    {
        var ok = true;
        var animates = false;
        var usesGlobal = false;
        var renderSig = 0;
        switch (pin.Kind)
        {
            case Nexus.Service.Persistence.DevicePinKinds.Animate when string.IsNullOrEmpty(pin.Effect):
                usesGlobal = true;
                break;
            case Nexus.Service.Persistence.DevicePinKinds.Animate:
                if (string.Equals(active.Name, PinRenderKey(pin), StringComparison.Ordinal))
                {
                    usesGlobal = true;
                }
                else if (PinRenderKey(pin) is { } renderKey && _pinRenders.TryGetValue(renderKey, out var render) && PinSourceRendering(render, out var fx))
                {
                    ok = IsTimePure(fx);
                    animates = ok;
                    renderSig = HashCode.Combine(render.Tag, ok);
                }
                else
                {
                    usesGlobal = true;
                }
                break;
            case Nexus.Service.Persistence.DevicePinKinds.Mirror:
            case Nexus.Service.Persistence.DevicePinKinds.Media:
                ok = false;
                break;
        }
        return (HashCode.Combine(_pinHashes.GetValueOrDefault(id), renderSig, ok, animates, usesGlobal), ok, animates, usesGlobal);
    }

    /// <summary>
    /// Publishes per pinned device whether its source can be rendered ahead, using the same instances
    /// <see cref="RenderAheadPins"/> renders. Allocates only when something changed. Engine thread, after
    /// <see cref="RenderPins"/>.
    /// </summary>
    private void TrackPinAhead(IEffect active)
    {
        var previous = _pinAhead;
        var tracker = Pins;
        // Read before Snapshot(): SetMany swaps the pins first and bumps the version after.
        var version = tracker?.Version ?? -1;
        _pinAheadSeenVersion = version;
        var hasPins = tracker is not null && tracker.Any;
        if (!hasPins && previous.Count == 0)
        {
            return;
        }
        var now = Environment.TickCount64;
        var pins = hasPins ? tracker!.Snapshot() : null;
        var changed = false;
        if (hasPins)
        {
            if (version != _pinAheadVersion)
            {
                _pinAheadVersion = version;
                _pinHashes.Clear();
                foreach (var (id, pin) in pins!)
                {
                    _pinHashes[id] = HashCode.Combine(pin.Kind, Nexus.Service.Lighting.DevicePinLook.StaticKey(pin), Nexus.Service.Persistence.SensorBinding.MapKey(pin.Sensors));
                }
            }
            var matched = 0;
            foreach (var dev in _devices)
            {
                if (!pins!.TryGetValue(dev.Id, out var pin))
                {
                    continue;
                }
                var c = ClassifyPinAhead(dev.Id, pin, active);
                if (previous.TryGetValue(dev.Id, out var before) && before.Sig == c.Sig)
                {
                    matched++;
                }
                else
                {
                    changed = true;
                    break;
                }
            }
            if (!changed && matched != previous.Count - TombstoneCount(previous))
            {
                changed = true;
            }
        }
        else
        {
            changed = true;
        }
        if (!changed && _pinAheadTombstones)
        {
            foreach (var state in previous.Values)
            {
                if (state.Sig == 0 && now - state.ChangedMs >= AheadParamSettleMs * 2)
                {
                    changed = true;
                    break;
                }
            }
        }
        if (!changed)
        {
            return;
        }
        var next = new Dictionary<string, PinAheadState>(StringComparer.Ordinal);
        if (hasPins)
        {
            foreach (var dev in _devices)
            {
                if (!pins!.TryGetValue(dev.Id, out var pin))
                {
                    continue;
                }
                var c = ClassifyPinAhead(dev.Id, pin, active);
                var since = previous.TryGetValue(dev.Id, out var before) && before.Sig == c.Sig ? before.ChangedMs : now;
                next[dev.Id] = new PinAheadState(c.Sig, since, c.Ok, c.Animates, c.UsesGlobal);
            }
        }
        // A pin that went away changes the device's source, so it settles like an edit.
        _pinAheadTombstones = false;
        foreach (var (id, old) in previous)
        {
            if (next.ContainsKey(id))
            {
                continue;
            }
            if (old.Sig != 0)
            {
                next[id] = new PinAheadState(0, now, true, false, true);
                _pinAheadTombstones = true;
            }
            else if (now - old.ChangedMs < AheadParamSettleMs * 2)
            {
                next[id] = old;
                _pinAheadTombstones = true;
            }
        }
        _pinAhead = next;
    }

    private static int TombstoneCount(Dictionary<string, PinAheadState> state)
    {
        var n = 0;
        foreach (var s in state.Values)
        {
            if (s.Sig == 0) n++;
        }
        return n;
    }

    // True when any requested zone follows the global canvas.
    private bool NeedsGlobal(string[] ids)
    {
        var state = _pinAhead;
        var any = false;
        foreach (var d in _devices)
        {
            if (Array.IndexOf(ids, d.Id) < 0)
            {
                continue;
            }
            any = true;
            if (!state.TryGetValue(d.Id, out var s) || s.UsesGlobal)
            {
                return true;
            }
        }
        return !any;
    }

    // Order-independent digest of the pin state of the requested devices.
    private int PinAheadSig(string[] ids)
    {
        var state = _pinAhead;
        var acc = 0;
        foreach (var id in ids)
        {
            if (state.TryGetValue(id, out var s) && s.Sig != 0)
            {
                unchecked { acc += HashCode.Combine(id, s.Sig); }
            }
        }
        return acc;
    }

    private void TrackAheadParams(IEffect effect)
    {
        if (effect is not Gpu.ShaderEffect shader)
        {
            SetAheadSig(HashCode.Combine(effect));
            return;
        }
        SetAheadSig(HashCode.Combine(
            HashCode.Combine(shader.Speed, shader.Intensity, shader.Hue, shader.Colorize, shader.Saturation, shader.Contrast, shader.AudioBoost),
            shader.ExtraParams, FullFrameSampling, shader));
    }

    private void SetAheadSig(int sig)
    {
        if (sig == _aheadParamsSig) return;
        _aheadParamsSig = sig;
        Volatile.Write(ref _aheadParamsChangedMs, Environment.TickCount64);
    }

    /// <summary>
    /// Asks the engine loop to render the current effect at future wall-clock
    /// times for <paramref name="deviceIds"/>, for hardware that plays an
    /// uploaded frame loop on its own clock. Rendered a few frames per tick after
    /// each live publish; <see cref="AheadRequest.Frames"/> stays null when the
    /// effect (when a requested zone follows it) or a requested zone's pin changes before the window completes.
    /// </summary>
    public AheadRequest RequestAhead(string[] deviceIds, long[] tickMs)
    {
        var req = new AheadRequest(deviceIds, tickMs);
        _aheadRequests.Enqueue(req);
        return req;
    }

    // Frames of one window rendered back to back before their readbacks are mapped together.
    private const int AheadBatchFrames = 8;

    // The active window's renders all go through ShaderEffect, so several frames may share one readback batch.
    private bool _aheadBatchOk;
    // Set by RenderAheadPins when a pinned source renders straight into its canvas instead of queueing a readback.
    private bool _aheadImmediatePin;
    private readonly List<DeviceFrame[]> _aheadBatchShadows = new();
    private readonly List<Dictionary<string, CanvasBuffer?>> _aheadBatchPinned = new();
    private readonly List<HashSet<string>> _aheadBatchFullFrame = new();

    private bool AheadAbandoned(AheadRequest req, IEffect effect) =>
        req.Abandoned || _studioOverlay is not null || req.PinSig != PinAheadSig(req.DeviceIds)
        || (Pins?.Version ?? -1) != _pinAheadSeenVersion
        || (req.NeedsGlobal && (!ReferenceEquals(req.Effect, effect) || req.ParamsSig != _aheadParamsSig || !IsTimePure(effect)));

    private void ServiceAheadRequests(IEffect effect, DeviceFrame[] devices)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rendered = 0;
        while (rendered == 0 || sw.Elapsed.TotalMilliseconds < AheadBudgetMs)
        {
            var req = _aheadActive;
            if (req is null)
            {
                if (!_aheadRequests.TryDequeue(out req))
                {
                    return;
                }
                req.Begin(effect, _aheadParamsSig, PinAheadSig(req.DeviceIds), NeedsGlobal(req.DeviceIds), devices);
                _aheadActive = req;
                _aheadBatchOk = false;
            }
            // A window whose zones are all pinned does not depend on the global effect at all.
            if (AheadAbandoned(req, effect))
            {
                _aheadActive = null;
                req.Done.Set();
                continue;
            }
            if (req.NextFrame == 0)
            {
                switch (TryFastWindow(req, effect))
                {
                    case FastWindow.Done:
                        rendered += req.TickMs.Length;
                        req.Frames = req.Rendered;
                        _aheadActive = null;
                        req.Done.Set();
                        continue;
                    default:
                        break;
                }
            }
            // The first frame of a window renders alone and shows whether every source queues its readback;
            // after that, up to AheadBatchFrames frames read back together. Without an open batch each
            // frame renders straight into the shared canvases, so it must be sampled before the next one.
            var batch = _aheadBatchOk && effect is Gpu.ShaderEffect batchShader ? Gpu.ReadbackBatch.TryBegin(batchShader.Context) : null;
            var want = batch is not null ? Math.Min(AheadBatchFrames, req.TickMs.Length - req.NextFrame) : 1;
            var count = 0;
            var discard = false;
            try
            {
                for (; count < want; count++)
                {
                    if (count > 0 && (AheadAbandoned(req, effect) || sw.Elapsed.TotalMilliseconds >= AheadBudgetMs))
                    {
                        break;
                    }
                    var tickMs = req.TickMs[req.NextFrame + count];
                    var shadow = req.NewShadowFrames();
                    batch?.Group = count;
                    if (req.NeedsGlobal)
                    {
                        effect.RenderFrame(_aheadCanvas, tickMs);
                    }
                    else
                    {
                        _aheadCanvas.Clear();
                    }
                    _aheadImmediatePin = false;
                    var failed = RenderAheadPins(effect, shadow, tickMs)
                        || (req.NeedsGlobal && effect is Gpu.ShaderEffect { HasFailed: true } or Gpu.ShaderEffect { IsRenderPending: true });
                    if (failed)
                    {
                        // A shader that failed mid-window left a stale canvas; ship nothing rather than those frames.
                        discard = true;
                        break;
                    }
                    if (count > 0 && _aheadImmediatePin)
                    {
                        // A source that renders straight into its canvas joined mid-group and overwrote earlier frames.
                        _aheadBatchOk = false;
                        discard = true;
                        break;
                    }
                    if (count == 0)
                    {
                        _aheadBatchOk = !_aheadImmediatePin && (!req.NeedsGlobal || effect is Gpu.ShaderEffect { TestRender: null });
                    }
                    SnapshotAheadMaps(count, shadow);
                }
                if (discard)
                {
                    batch?.Cancel();
                    _aheadActive = null;
                    req.Done.Set();
                    continue;
                }
                batch?.Resolve();
                for (var i = 0; i < count; i++)
                {
                    batch?.Apply(i);
                    CheckSpot(req, req.NextFrame);
                    var shadow = _aheadBatchShadows[i];
                    RestoreAheadMaps(i);
                    _sampleCanvas = _aheadCanvas;
                    SampleDevicesFromCanvas(shadow, pinned: _aheadPinned, pinFullFrame: _aheadPinFullFrame);
                    ApplyLedColorLocks(shadow);
                    foreach (var d in shadow)
                    {
                        d.Publish();
                    }
                    req.Rendered[req.NextFrame++] = shadow;
                    rendered++;
                }
            }
            catch
            {
                _aheadActive = null;
                req.Done.Set();
                throw;
            }
            finally
            {
                _sampleCanvas = _canvas;
                try
                {
                    batch?.Dispose();
                }
                catch (Exception ex)
                {
                    // Already resolved on the normal path, so this only runs while another exception unwinds; keep that one.
                    Gpu.GpuContext.Log($"[lighting-engine] look-ahead batch close failed: {ex.Message}");
                }
            }
            if (req.NextFrame == req.TickMs.Length)
            {
                req.Frames = req.Rendered;
                _aheadActive = null;
                req.Done.Set();
            }
        }
    }

    // Keeps frame i's shadow devices and pin routing while later frames of the batch render over the shared maps.
    private void SnapshotAheadMaps(int i, DeviceFrame[] shadow)
    {
        if (i == 0)
        {
            _aheadBatchShadows.Clear();
        }
        while (_aheadBatchPinned.Count <= i)
        {
            _aheadBatchPinned.Add(new Dictionary<string, CanvasBuffer?>(StringComparer.Ordinal));
            _aheadBatchFullFrame.Add(new HashSet<string>(StringComparer.Ordinal));
        }
        _aheadBatchShadows.Add(shadow);
        var pinned = _aheadBatchPinned[i];
        pinned.Clear();
        foreach (var kv in _aheadPinned)
        {
            pinned[kv.Key] = kv.Value;
        }
        var full = _aheadBatchFullFrame[i];
        full.Clear();
        full.UnionWith(_aheadPinFullFrame);
    }

    private void RestoreAheadMaps(int i)
    {
        _aheadPinned.Clear();
        foreach (var kv in _aheadBatchPinned[i])
        {
            _aheadPinned[kv.Key] = kv.Value;
        }
        _aheadPinFullFrame.Clear();
        _aheadPinFullFrame.UnionWith(_aheadBatchFullFrame[i]);
    }

    private readonly Dictionary<string, CanvasBuffer?> _aheadPinned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _aheadPinFullFrame = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CanvasBuffer> _aheadPinCanvases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _aheadPinRendered = new(StringComparer.Ordinal);

    private enum AheadPinKind { Base, FollowActive, Pin }

    /// <summary>
    /// Where a look-ahead device reads its canvas: the base canvas (no animate pin, or its pinned source cannot render),
    /// the active effect's canvas (pinned to what runs globally), or its own pinned source. Shared by the canvas
    /// window and the LED window so both route every device the same way.
    /// </summary>
    private AheadPinKind ResolveAheadPin(
        DeviceFrame dev, Dictionary<string, Nexus.Service.Persistence.DevicePin> pins, IEffect active,
        out string key, out IEffect? fx, out bool sweep)
    {
        key = "";
        fx = null;
        sweep = false;
        if (!pins.TryGetValue(dev.Id, out var pin)
            || pin.Kind != Nexus.Service.Persistence.DevicePinKinds.Animate
            || string.IsNullOrEmpty(pin.Effect))
        {
            return AheadPinKind.Base;
        }
        key = PinRenderKey(pin)!;
        sweep = Gpu.ShaderLibrary.IsSweepEffect(key);
        if (string.Equals(active.Name, key, StringComparison.Ordinal))
        {
            return AheadPinKind.FollowActive;
        }
        if (!_pinRenders.TryGetValue(key, out var render) || !PinSourceRendering(render, out fx) || !IsTimePure(fx))
        {
            fx = null;
            return AheadPinKind.Base;
        }
        return AheadPinKind.Pin;
    }

    /// <summary>
    /// Renders each animate-pinned source the chain uses at <paramref name="tickMs"/> onto its own scratch
    /// canvas and records which canvas each shadow device samples, as <see cref="RenderPins"/> does live.
    /// A device whose pinned source is not rendering follows the base canvas. True when a pinned shader failed
    /// while rendering this frame. Engine thread.
    /// </summary>
    private bool RenderAheadPins(IEffect active, DeviceFrame[] shadow, long tickMs)
    {
        _aheadPinned.Clear();
        _aheadPinFullFrame.Clear();
        _aheadPinRendered.Clear();
        var failed = false;
        var tracker = Pins;
        if (tracker is null || !tracker.Any)
        {
            _aheadPinCanvases.Clear();
            return false;
        }
        var pins = tracker.Snapshot();
        foreach (var dev in shadow)
        {
            var kind = ResolveAheadPin(dev, pins, active, out var key, out var fx, out var sweep);
            if (kind == AheadPinKind.Base)
            {
                continue;
            }
            if (kind == AheadPinKind.FollowActive)
            {
                _aheadPinned[dev.Id] = null;
                if (sweep) _aheadPinFullFrame.Add(dev.Id);
                continue;
            }
            if (!_aheadPinCanvases.TryGetValue(key, out var canvas))
            {
                canvas = new CanvasBuffer(_canvas.Width, _canvas.Height);
                _aheadPinCanvases[key] = canvas;
            }
            if (_aheadPinRendered.Add(key))
            {
                fx!.RenderFrame(canvas, tickMs);
                if (!Gpu.ReadbackBatch.CanDefer(fx))
                {
                    _aheadImmediatePin = true;
                }
                failed |= fx is Gpu.ShaderEffect { HasFailed: true } or Gpu.ShaderEffect { IsRenderPending: true };
            }
            _aheadPinned[dev.Id] = canvas;
            if (sweep) _aheadPinFullFrame.Add(dev.Id);
        }
        return failed;
    }

    private void SampleDevicesFromCanvas(
        DeviceFrame[] devices, bool honorPins = true,
        Dictionary<string, CanvasBuffer?>? pinned = null, HashSet<string>? pinFullFrame = null)
    {
        var baseCanvas = _sampleCanvas;
        try
        {
            var pinMap = pinned ?? _pinnedDevices;
            SampleDevicesCore(devices, baseCanvas, honorPins && pinMap.Count > 0, pinMap, pinFullFrame ?? _pinFullFrame);
        }
        finally
        {
            _sampleCanvas = baseCanvas;
        }
    }

    private void SampleDevicesCore(
        DeviceFrame[] devices, CanvasBuffer baseCanvas, bool honorPins,
        Dictionary<string, CanvasBuffer?> pinMap, HashSet<string> pinFullFrame)
    {
        var cw = _sampleCanvas.Width;
        var ch = _sampleCanvas.Height;
        const float CW = CanvasUnitsW, CH = CanvasUnitsH;
        // Overlays first, so an LED they own never gets a canvas colour it is
        // about to lose. Correctness no longer rests on this - DeviceFrame
        // publishes once per tick - but painting an LED twice is wasted work.
        var overlaid = ApplyTestOverlays(devices);
        // Placed devices follow the scene camera under every sampling mode: a
        // sweep in 3D crosses the whole scene instead of each device end to end.
        var scene = _scene;
        for (var di = 0; di < devices.Length; di++)
        {
            var dev = devices[di];
            // An animate or mirror pinned device samples its source's canvas, the main
            // one when it is pinned to what is running globally.
            CanvasBuffer? pinCanvas = null;
            var onPinCanvas = honorPins && pinMap.TryGetValue(dev.Id, out pinCanvas) && pinCanvas is not null;
            _sampleCanvas = onPinCanvas ? pinCanvas! : baseCanvas;
            // Preview LED count from the editor's unsaved draft; clamped to the
            // physical buffer size because SetLed bounds-guards on LedCount.
            var ledCount = dev.PreviewLedCount is { } pc
                ? Math.Min(pc, dev.LedCount)
                : dev.LedCount;
            if (ledCount <= 0)
            {
                continue;
            }

            // LEDs above the preview count are not part of the draft; zero them
            // so they don't show stale colour from a prior render.
            for (int z = ledCount; z < dev.LedCount; z++)
            {
                dev.SetLed(z, 0, 0, 0);
            }

            // The overlay pass already gave this device its final colours.
            if (overlaid[di])
            {
                continue;
            }

            // The stored rect is the frame before it turns; the frame turns
            // about that rect's centre by Rotation degrees clockwise, in canvas
            // units, so a turned frame samples what the page shows over the
            // same stretched canvas. Every sample point below is laid out
            // unturned in units, rotated about (frameCx, frameCy), and only
            // then scaled onto the pixel grid. A stack slot is cut from the
            // unturned rect and turns with the whole frame.
            // A device on its own pin canvas follows that source alone: the
            // global sweep flag describes the main canvas, not this one.
            var fullFrame = onPinCanvas
                ? pinFullFrame.Contains(dev.Id)
                : FullFrameSampling || (honorPins && pinFullFrame.Contains(dev.Id));
            // A full-frame device's cell is a slice of the whole canvas, so it
            // takes the plain cell mean instead of the spot. See SampleLedBox.
            var rectX = fullFrame ? 0f : dev.X;
            var rectY = fullFrame ? 0f : dev.Y;
            var rectW = fullFrame ? CW : dev.W;
            var rectH = fullFrame ? CH : dev.H;
            var frameCx = rectX + rectW * 0.5f;
            var frameCy = rectY + rectH * 0.5f;
            var kx = cw / CW;
            var ky = ch / CH;
            var slots = fullFrame ? null : _stackSlots;
            if (slots is not null && slots.TryGetValue(dev.Id, out var slot))
            {
                (rectX, rectY, rectW, rectH) = Nexus.Service.Lighting.StackSlots.Slice(rectX, rectY, rectW, rectH, slot);
            }

            // Preview layout: editor draft positions keyed by LED index.
            // When present, build parallel U/V/Disabled arrays in index order
            // so the same UV sampling path below works without branching.
            var previewLayout = dev.PreviewLayout;
            float[]? devLedU;
            float[]? devLedV;
            bool[]? devLedDisabled;
            if (previewLayout is not null && previewLayout.Length > 0)
            {
                var pu = new float[ledCount];
                var pv = new float[ledCount];
                bool[]? pd = null;
                foreach (var pos in previewLayout)
                {
                    var idx = pos.Index;
                    if (idx < 0 || idx >= ledCount)
                    {
                        continue;
                    }
                    pu[idx] = pos.U;
                    pv[idx] = pos.V;
                    if (pos.Disabled)
                    {
                        pd ??= new bool[ledCount];
                        pd[idx] = true;
                    }
                }
                devLedU = pu;
                devLedV = pv;
                devLedDisabled = pd;
            }
            else
            {
                // Keyboards and other matrix devices provide per-LED UVs so each key
                // samples from its real 2D position inside the rectangle instead of
                // being stretched along a single axis. Rotation is applied to the UV
                // coordinates around the rectangle centre so reorienting the board
                // keeps the mapping right.
                devLedU = dev.LedU;
                devLedV = dev.LedV;
                devLedDisabled = dev.LedDisabled;
            }

            if (scene is not null && scene.Placements.TryGetValue(dev.Id, out var quads))
            {
                SampleSceneDevice(dev, ledCount, devLedU, devLedV, devLedDisabled, quads, scene.Camera, kx, ky);
                continue;
            }

            // UV maps read a cell sized from a uniform cols x rows density matched to the rect aspect; strips read one LED
            // pitch along the centerline by the frame breadth. Geometry and spot rule live in LedSampling.cs.
            var geo = LedSpotGeometry.Create(
                rectX, rectY, rectW, rectH, frameCx, frameCy, dev.Rotation, cw, ch, ledCount, devLedU, devLedV, fullFrame,
                FullFrameStripBreadth);
            for (int i = 0; i < ledCount; i++)
            {
                if (devLedDisabled is not null && i < devLedDisabled.Length && devLedDisabled[i])
                {
                    dev.SetLed(i, 0, 0, 0);
                    continue;
                }
                geo.Center(i, out var sx, out var sy);
                var (r, g, b) = fullFrame
                    ? SampleLedBox(sx - geo.HalfW, sy - geo.HalfH, sx + geo.HalfW, sy + geo.HalfH)
                    : SampleLedSpot(sx, sy, geo.HalfW, geo.HalfH);
                dev.SetLed(i, r, g, b);
            }
        }
    }

    /// <summary>
    /// Samples a device placed in the 3D scene: each LED's map point is laid on
    /// its surface, projected through the scene camera, and read there. A device
    /// with several surfaces splits its LEDs into equal runs, each run's map
    /// stretched to fill its own surface.
    /// </summary>
    private void SampleSceneDevice(
        DeviceFrame dev, int ledCount, float[]? ledU, float[]? ledV, bool[]? disabled,
        Nexus.Service.Lighting.Scene.SceneQuad[] quads, in Nexus.Service.Lighting.Scene.SceneCameraBasis cam,
        float kx, float ky)
    {
        var hasUv = ledU is not null && ledV is not null && ledU.Length == ledCount && ledV.Length == ledCount;
        var runs = quads.Length;
        for (var k = 0; k < runs; k++)
        {
            var lo = k * ledCount / runs;
            var hi = (k + 1) * ledCount / runs;
            var count = hi - lo;
            if (count <= 0)
            {
                continue;
            }
            var quad = quads[k];
            float u0 = 0f, uSpan = 1f, v0 = 0f, vSpan = 1f;
            if (hasUv && runs > 1)
            {
                float u1 = float.MinValue, v1 = float.MinValue;
                u0 = float.MaxValue;
                v0 = float.MaxValue;
                for (var i = lo; i < hi; i++)
                {
                    if (disabled is not null && i < disabled.Length && disabled[i])
                    {
                        continue;
                    }
                    u0 = MathF.Min(u0, ledU![i]);
                    u1 = MathF.Max(u1, ledU[i]);
                    v0 = MathF.Min(v0, ledV![i]);
                    v1 = MathF.Max(v1, ledV[i]);
                }
                uSpan = u1 - u0;
                vSpan = v1 - v0;
            }

            // Cell size as the UV path picks it, from the surface's on-canvas
            // bounding box split into a grid matched to its aspect.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var c = 0; c < 4; c++)
            {
                if (cam.Project(quad.At(c & 1, c >> 1), out var px, out var py))
                {
                    minX = MathF.Min(minX, px);
                    maxX = MathF.Max(maxX, px);
                    minY = MathF.Min(minY, py);
                    maxY = MathF.Max(maxY, py);
                }
            }
            // A surface just past the near plane can project far wider than the canvas; the cell never needs more.
            var boxW = maxX > minX ? MathF.Min(maxX - minX, CanvasUnitsW) : 1f / kx;
            var boxH = maxY > minY ? MathF.Min(maxY - minY, CanvasUnitsH) : 1f / ky;
            var cols = Math.Max(1, (int)MathF.Round(MathF.Sqrt(count * boxW / boxH)));
            var rows = Math.Max(1, (count + cols - 1) / cols);
            var halfW = Math.Max(1f / kx, boxW / cols) * 0.5f * kx;
            var halfH = Math.Max(1f / ky, boxH / rows) * 0.5f * ky;

            for (var i = lo; i < hi; i++)
            {
                if (disabled is not null && i < disabled.Length && disabled[i])
                {
                    dev.SetLed(i, 0, 0, 0);
                    continue;
                }
                float u, v;
                if (hasUv)
                {
                    u = uSpan > 1e-6f ? (ledU![i] - u0) / uSpan : 0.5f;
                    v = vSpan > 1e-6f ? (ledV![i] - v0) / vSpan : 0.5f;
                }
                else
                {
                    u = count > 1 ? (float)(i - lo) / (count - 1) : 0.5f;
                    v = 0.5f;
                }
                if (!cam.Project(quad.At(u, v), out var cx, out var cy))
                {
                    dev.SetLed(i, 0, 0, 0);
                    continue;
                }
                var sx = cx * kx;
                var sy = cy * ky;
                var (r, g, b) = SampleLedSpot(sx, sy, halfW, halfH);
                dev.SetLed(i, r, g, b);
            }
        }
    }

    /// <summary>
    /// Color of one LED: the plain mean, black included, of a small spot of
    /// canvas pixels centered on the LED's sample position (<paramref name="sx"/>,
    /// <paramref name="sy"/>, canvas pixels). The spot is
    /// <see cref="SpotSideOfCanvasWidth"/> of the canvas width on a side, centered
    /// on the pixel holding the position, and shrinks to the LED's own cell
    /// (<paramref name="cellHalfW"/>, <paramref name="cellHalfH"/>, half extents
    /// in canvas pixels) when that is smaller, so dense neighbours do not blur
    /// together. Clamped at the canvas edges.
    /// </summary>
    private (byte r, byte g, byte b) SampleLedSpot(float sx, float sy, float cellHalfW, float cellHalfH)
    {
        LedSpotGeometry.SpotRect(
            sx, sy, cellHalfW, cellHalfH, _sampleCanvas.Width, _sampleCanvas.Height, SpotSideOfCanvasWidth,
            out var ix0, out var iy0, out var ix1, out var iy1);
        long n = 0, rSum = 0, gSum = 0, bSum = 0;
        for (int py = iy0; py <= iy1; py++)
        {
            for (int px = ix0; px <= ix1; px++)
            {
                var (r, g, b) = _sampleCanvas.GetPixel(px, py);
                rSum += r;
                gSum += g;
                bSum += b;
                n++;
            }
        }
        return ((byte)(rSum / n), (byte)(gSum / n), (byte)(bSum / n));
    }

    /// <summary>
    /// Plain unweighted mean of the LED's cell, black pixels included - the read
    /// under <see cref="FullFrameSampling"/>, where the cell is a slice of the
    /// whole canvas rather than of the device's own frame. That makes it a box
    /// filter over the pattern: a device with LEDs to spare resolves the dark
    /// gaps, and one with too few settles on the pattern's average instead of
    /// aliasing (a 1-LED zone whose point sample would blink at the band rate
    /// holds a steady mid tone). The per-LED spot read does not fit here: a
    /// sweep fills the frame and the cell is the unit.
    /// </summary>
    private (byte r, byte g, byte b) SampleLedBox(float x0, float y0, float x1, float y1)
    {
        var ix0 = Math.Clamp((int)MathF.Floor(x0), 0, _sampleCanvas.Width - 1);
        var iy0 = Math.Clamp((int)MathF.Floor(y0), 0, _sampleCanvas.Height - 1);
        var ix1 = Math.Clamp((int)MathF.Ceiling(x1) - 1, ix0, _sampleCanvas.Width - 1);
        var iy1 = Math.Clamp((int)MathF.Ceiling(y1) - 1, iy0, _sampleCanvas.Height - 1);
        long n = 0, rSum = 0, gSum = 0, bSum = 0;
        for (int py = iy0; py <= iy1; py++)
        {
            for (int px = ix0; px <= ix1; px++)
            {
                var (r, g, b) = _sampleCanvas.GetPixel(px, py);
                rSum += r;
                gSum += g;
                bSum += b;
                n++;
            }
        }
        if (n == 0)
        {
            return (0, 0, 0);
        }
        return ((byte)(rSum / n), (byte)(gSum / n), (byte)(bSum / n));
    }

    /// <summary>
    /// Paints locked LEDs over whatever the frame already holds. Skips a device
    /// under a test pattern and LEDs the map marks disabled; under an editor
    /// highlight only the highlighted LEDs show their lock.
    /// </summary>
    private void ApplyLedColorLocks(DeviceFrame[] devices)
    {
        var tracker = LedColorLocks;
        if (tracker is null) return;
        foreach (var dev in devices)
        {
            if (dev.TestPattern is not null) continue;
            if (_studioOverlay is not null && dev.HighlightLeds is not { Count: > 0 }) continue;
            var highlights = dev.HighlightLeds is { Count: > 0 } h ? h : null;
            var count = dev.PreviewLedCount is { } pc ? Math.Min(pc, dev.LedCount) : dev.LedCount;
            tracker.PaintLocks(dev, count, highlights);
        }
    }

    /// <summary>
    /// Paint the devices whose frame comes from something other than the canvas:
    /// a zone highlight, a static pin (in every mode), or a test pattern.
    /// Returns, per device, whether it painted the whole preview range, so the
    /// caller can skip a canvas sample that would only be overwritten.
    /// </summary>
    private bool[] ApplyTestOverlays(DeviceFrame[] devices)
    {
        if (_overlaid.Length < devices.Length) _overlaid = new bool[devices.Length];
        var owned = _overlaid;
        Array.Clear(owned, 0, devices.Length);
        if (_reactionBlocked.Length < devices.Length) _reactionBlocked = new bool[devices.Length];
        var blocked = _reactionBlocked;
        Array.Clear(blocked, 0, devices.Length);
        // The one place a device is claimed, so the two arrays cannot drift apart.
        void Claim(int index, bool blocksReactions)
        {
            owned[index] = true;
            blocked[index] = blocksReactions;
        }
        PruneAssignments(devices);
        var studio = _studioOverlay;
        if (studio is not null)
        {
            _studioCanvas ??= new CanvasBuffer(_canvas.Width, _canvas.Height);
            try
            { studio.RenderFrame(_studioCanvas, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
            catch (Exception ex) { Console.Error.WriteLine($"[lighting-engine] studio overlay threw: {ex.Message}"); }
        }
        for (var di = 0; di < devices.Length; di++)
        {
            var dev = devices[di];
            var previewCount = dev.PreviewLedCount is { } pc ? Math.Min(pc, dev.LedCount) : dev.LedCount;

            var highlights = dev.HighlightLeds;
            if (highlights is not null && highlights.Count > 0)
            {
                for (int i = 0; i < previewCount; i++)
                {
                    if (highlights.Contains(i))
                    {
                        dev.SetLed(i, 255, 255, 255);
                    }
                    else
                    {
                        dev.SetLed(i, 0, 0, 0);
                    }
                }
                Claim(di, blocksReactions: true);
                continue;
            }

            // The studio overlay shows the whole effect on every device; only the mapper's own highlight and test pattern sit above it.
            if (studio is not null && dev.TestPattern is null && _studioCanvas is not null)
            {
                PaintStudioOverlay(dev, previewCount, _studioCanvas);
                Claim(di, blocksReactions: true);
                continue;
            }

            // Identify and test overlays outrank a pin; a pin outranks the global source.
            if (Pins is not null
                && dev.TestPattern is null
                && Pins.TryGet(dev.Id, out var assignment)
                && assignment.Kind == Nexus.Service.Persistence.DevicePinKinds.Static)
            {
                // A palette pick is a color, not an effect: nothing to render,
                // nothing to sample, no canvas. Paint it and move on.
                if (Nexus.Service.Lighting.StaticColorHex.TryParse(assignment.Color, out var cr, out var cg, out var cb))
                {
                    for (int i = 0; i < previewCount; i++) dev.SetLed(i, cr, cg, cb);
                    Claim(di, blocksReactions: false);
                    continue;
                }
                var render = RenderAssignment(dev.Id, assignment);
                if (render is not null)
                {
                    PaintFromAssignment(dev, previewCount, render);
                    Claim(di, blocksReactions: false);
                    continue;
                }
            }

            var pattern = dev.TestPattern;
            if (pattern is not null)
            {
                if (pattern == "none")
                {
                    dev.Fill(0, 0, 0);
                    Claim(di, blocksReactions: true);
                }
                else
                {
                    // Prefer preview layout UVs when present; fall back to saved LedU/LedV.
                    float[]? ledU = null;
                    float[]? ledV = null;
                    var previewLayout = dev.PreviewLayout;
                    if (previewLayout is not null && previewLayout.Length > 0)
                    {
                        var pu = new float[previewCount];
                        var pv = new float[previewCount];
                        foreach (var pos in previewLayout)
                        {
                            if (pos.Index >= 0 && pos.Index < previewCount)
                            {
                                pu[pos.Index] = pos.U;
                                pv[pos.Index] = pos.V;
                            }
                        }
                        ledU = pu;
                        ledV = pv;
                    }
                    else
                    {
                        ledU = dev.LedU;
                        ledV = dev.LedV;
                    }

                    if (ledU is not null && ledV is not null)
                    {
                        var elapsed = dev.TestPatternStartMs > 0
                            ? Environment.TickCount64 - dev.TestPatternStartMs
                            : 0L;
                        var phase = (float)(elapsed % 2000 / 2000.0);
                        var covered = Math.Min(previewCount, Math.Min(ledU.Length, ledV.Length));
                        for (int i = 0; i < covered; i++)
                        {
                            var u = ledU[i];
                            var v = ledV[i];
                            float t = pattern == "horizontal" ? u : v;
                            var band = 1f - Math.Min(1f, Math.Abs(t - phase) * 5f);
                            dev.SetLed(i,
                                (byte)(band * 255), (byte)(band * 255), (byte)(band * 255));
                        }
                        // Short UV arrays would leave a tail the canvas still
                        // has to fill, so only a full sweep claims the device.
                        if (covered >= previewCount) Claim(di, blocksReactions: true);
                    }
                    // No UVs: nothing was painted, so the canvas sample stands.
                }
            }
        }
        return owned;
    }

    // Each LED reads the overlay at its own normalized UV (editor draft layout first, then the saved one);
    // a device without UVs spreads its LEDs along the canvas middle in index order.
    private static void PaintStudioOverlay(DeviceFrame dev, int count, CanvasBuffer canvas)
    {
        float[]? ledU = dev.LedU;
        float[]? ledV = dev.LedV;
        bool[]? disabled = dev.LedDisabled;
        var previewLayout = dev.PreviewLayout;
        if (previewLayout is not null && previewLayout.Length > 0)
        {
            ledU = new float[count];
            ledV = new float[count];
            disabled = null;
            foreach (var pos in previewLayout)
            {
                if (pos.Index < 0 || pos.Index >= count)
                {
                    continue;
                }
                ledU[pos.Index] = pos.U;
                ledV[pos.Index] = pos.V;
                if (pos.Disabled)
                {
                    disabled ??= new bool[count];
                    disabled[pos.Index] = true;
                }
            }
        }
        var w = canvas.Width;
        var h = canvas.Height;
        for (var i = 0; i < count; i++)
        {
            if (disabled is not null && i < disabled.Length && disabled[i])
            {
                dev.SetLed(i, 0, 0, 0);
                continue;
            }
            float u, v;
            if (ledU is not null && ledV is not null && i < ledU.Length && i < ledV.Length)
            {
                u = ledU[i];
                v = ledV[i];
            }
            else
            {
                u = count > 1 ? i / (float)(count - 1) : 0.5f;
                v = 0.5f;
            }
            var (r, g, b) = canvas.GetPixel((int)MathF.Round(Math.Clamp(u, 0f, 1f) * (w - 1)), (int)MathF.Round(Math.Clamp(v, 0f, 1f) * (h - 1)));
            dev.SetLed(i, r, g, b);
        }
    }

    private void SerializeAndBroadcast()
    {
        var devices = _devices;
        var canvasBytes = _canvas.ByteCount;
        var totalSize = 1 + 4 + canvasBytes + 1;
        foreach (var dev in devices)
        {
            totalSize += 1 + 2 + dev.LedCount * 3;
        }

        if (_frameBuffer.Length < totalSize)
        {
            _frameBuffer = new byte[totalSize + 256];
        }

        var buf = _frameBuffer;
        buf[0] = 0x03;
        var pos = 1;
        var cw = (ushort)_canvas.Width;
        var ch = (ushort)_canvas.Height;
        buf[pos++] = (byte)(cw & 0xFF);
        buf[pos++] = (byte)(cw >> 8);
        buf[pos++] = (byte)(ch & 0xFF);
        buf[pos++] = (byte)(ch >> 8);
        _canvas.Pixels.CopyTo(buf.AsSpan(pos));
        pos += canvasBytes;
        buf[pos++] = (byte)Math.Min(devices.Length, 255);
        foreach (var dev in devices)
        {
            buf[pos++] = DeviceFrameIndex.WireByte(dev);
            var lc = (ushort)dev.LedCount;
            buf[pos++] = (byte)(lc & 0xFF);
            buf[pos++] = (byte)(lc >> 8);
            dev.LedBytes.CopyTo(buf.AsSpan(pos));
            pos += dev.LedCount * 3;
        }
        OnFrame?.Invoke(new ReadOnlyMemory<byte>(_frameBuffer, 0, pos));
    }

    /// <summary>
    /// Rendered pixels for an assignment, rendering it if this look has not been
    /// seen. Runs on the engine thread, which is where the GL context lives.
    /// Only a frame the shader actually drew is cached. While the program is still
    /// compiling, or the draw was skipped or dropped, the effect stays alive for the
    /// next tick and the device keeps its last finished look (or, with none, the
    /// shared canvas via null). A failed or overdue build backs off from AssignRetryBaseMs up to AssignRetryMaxMs and the
    /// device keeps the same fallback: unlike an animate pin, which holds black, a
    /// static look has no live frame to lose, so black would only hide the fault.
    /// </summary>
    private byte[]? RenderAssignment(string deviceId, Nexus.Service.Persistence.DevicePin assignment)
    {
        var factory = StaticPinEffectFactory;
        if (Pins is null || factory is null) return null;
        var key = Nexus.Service.Lighting.DevicePinLook.StaticKey(assignment);
        if (_assignRenders.TryGetValue(key, out var cached))
        {
            _assignLastGood[deviceId] = cached;
            return cached;
        }
        _assignLastGood.TryGetValue(deviceId, out var fallback);
        var nowTick = Environment.TickCount64;
        _assignRetry.TryGetValue(key, out var retry);
        if (retry.Attempts > 0 && nowTick < retry.At) return fallback;

        var keep = false;
        IEffect? effect = null;
        try
        {
            if (!_assignPending.TryGetValue(key, out effect))
            {
                effect = factory(assignment);
            }
            if (effect is null) return null;
            _assignCanvas ??= new CanvasBuffer(_canvas.Width, _canvas.Height);
            _assignCanvas.Clear();
            // Static holds a frame, so one render at tick 0 is the whole look.
            effect.RenderFrame(_assignCanvas, 0);
            if (effect is Gpu.ShaderEffect shader)
            {
                if (shader.HasFailed)
                {
                    ScheduleAssignRetry(key, assignment.Effect, retry.Attempts, nowTick, "render failed", null);
                    return fallback;
                }
                if (!shader.DrewFrame)
                {
                    var since = nowTick;
                    if (_assignPendingSince.TryGetValue(key, out var track) && nowTick - track.Polled <= AssignPollGapMs)
                    {
                        since = track.Since;
                    }
                    if (nowTick - since > AssignPendingBudgetMs)
                    {
                        // Never drew and never failed: give the compile up and retry on the backoff.
                        ScheduleAssignRetry(key, assignment.Effect, retry.Attempts, nowTick, "no frame drawn within the pending budget", null);
                        return fallback;
                    }
                    _assignPendingSince[key] = (since, nowTick);
                    _assignPending[key] = effect;
                    keep = true;
                    return fallback;
                }
            }
            var pixels = _assignCanvas.Pixels.ToArray();
            _assignRenders[key] = pixels;
            _assignLastGood[deviceId] = pixels;
            _assignRetry.Remove(key);
            return pixels;
        }
        catch (Exception ex)
        {
            // A look that cannot render must not take the whole frame down.
            ScheduleAssignRetry(key, assignment.Effect, retry.Attempts, nowTick, "render failed", ex);
            return fallback;
        }
        finally
        {
            if (!keep)
            {
                _assignPending.Remove(key);
                _assignPendingSince.Remove(key);
                try { effect?.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    private void ScheduleAssignRetry(string key, string? effectName, int attempts, long nowTick, string reason, Exception? ex)
    {
        if (attempts == 0)
        {
            Gpu.GpuContext.Log($"[static-pin] {reason} for {effectName}, retrying with backoff{(ex is null ? "" : $": {ex.GetType().Name}: {ex.Message}")}");
        }
        var delay = Math.Min(AssignRetryMaxMs, AssignRetryBaseMs << Math.Min(attempts, 10));
        _assignRetry[key] = (nowTick + delay, attempts + 1);
    }

    /// <summary>
    /// Drops cached looks, pending compiles, retries and held pixels that no current
    /// device pin references. A look is keyed by every control, so a pin change on
    /// one device leaves every other device's look alone. Runs when the pin set or
    /// the device array changes.
    /// </summary>
    private void PruneAssignments(DeviceFrame[] devices)
    {
        // Look-ahead passes a fresh shadow array every frame; only the live device array defines what is referenced.
        if (!ReferenceEquals(devices, _devices)) return;
        var pins = Pins;
        var version = pins?.Version ?? -1;
        if (version == _assignPruneVersion && ReferenceEquals(devices, _assignPruneDevices)) return;
        _assignPruneVersion = version;
        _assignPruneDevices = devices;
        if (_assignRenders.Count + _assignPending.Count + _assignRetry.Count + _assignLastGood.Count == 0) return;
        _assignRefKeys.Clear();
        _assignRefDevices.Clear();
        if (pins is not null)
        {
            foreach (var dev in devices)
            {
                if (pins.TryGet(dev.Id, out var a)
                    && a.Kind == Nexus.Service.Persistence.DevicePinKinds.Static
                    && !Nexus.Service.Lighting.StaticColorHex.TryParse(a.Color, out _, out _, out _))
                {
                    _assignRefKeys.Add(Nexus.Service.Lighting.DevicePinLook.StaticKey(a));
                    _assignRefDevices.Add(dev.Id);
                }
            }
        }
        foreach (var k in _assignRenders.Keys.Where(k => !_assignRefKeys.Contains(k)).ToList()) _assignRenders.Remove(k);
        foreach (var k in _assignRetry.Keys.Where(k => !_assignRefKeys.Contains(k)).ToList()) _assignRetry.Remove(k);
        foreach (var k in _assignPending.Keys.Where(k => !_assignRefKeys.Contains(k)).ToList())
        {
            try { _assignPending[k].Dispose(); } catch { /* best-effort */ }
            _assignPending.Remove(k);
            _assignPendingSince.Remove(k);
        }
        foreach (var d in _assignLastGood.Keys.Where(d => !_assignRefDevices.Contains(d)).ToList()) _assignLastGood.Remove(d);
    }

    /// <summary>True while a pending static look can still make progress: its context is up, so polling it is not a no-op.</summary>
    private bool AnyAssignPollable()
    {
        foreach (var e in _assignPending.Values)
        {
            if (e is Gpu.ShaderEffect { CanPoll: true })
            {
                return true;
            }
        }
        return false;
    }

    private void DisposeAssignState()
    {
        foreach (var e in _assignPending.Values)
        {
            try { e.Dispose(); } catch { /* best-effort */ }
        }
        _assignPending.Clear();
        _assignPendingSince.Clear();
        _assignRenders.Clear();
        _assignRetry.Clear();
        _assignLastGood.Clear();
        _assignPruneVersion = -1;
        _assignPruneDevices = null;
    }

    /// <summary>
    /// Paint a device from an assignment render. Static evaluates every device
    /// as if its frame filled the canvas, so the look reads end to end on the
    /// device rather than the slice its rect covers.
    ///
    /// Matrix devices (keyboards) carry per-LED UVs and sample their real 2D
    /// position, so a two-axis pattern lands correctly. Everything else walks
    /// its own long axis: sampling a fixed horizontal midline collapsed any
    /// vertically-swept pattern to a single colour (bench-hit on the Keeb).
    /// </summary>
    private void PaintFromAssignment(DeviceFrame dev, int ledCount, byte[] pixels)
    {
        var w = _canvas.Width;
        var h = _canvas.Height;

        var ledU = dev.PreviewLayout is { Length: > 0 } ? null : dev.LedU;
        var ledV = dev.PreviewLayout is { Length: > 0 } ? null : dev.LedV;
        var haveUv = ledU is not null && ledV is not null
            && ledU.Length >= ledCount && ledV.Length >= ledCount;

        // A frame taller than it is wide runs its LEDs down the canvas.
        var vertical = dev.H > dev.W;

        for (int i = 0; i < ledCount; i++)
        {
            float u, v;
            if (haveUv)
            {
                u = ledU![i];
                v = ledV![i];
            }
            else
            {
                var t = ledCount > 1 ? (float)i / (ledCount - 1) : 0.5f;
                u = vertical ? 0.5f : t;
                v = vertical ? t : 0.5f;
            }
            var x = Math.Clamp((int)MathF.Round(Math.Clamp(u, 0f, 1f) * (w - 1)), 0, w - 1);
            var y = Math.Clamp((int)MathF.Round(Math.Clamp(v, 0f, 1f) * (h - 1)), 0, h - 1);
            var o = ((y * w) + x) * 3;
            if (o + 2 >= pixels.Length) break;
            dev.SetLed(i, pixels[o], pixels[o + 1], pixels[o + 2]);
        }
    }

    public void Dispose()
    {
        // Idempotent: the test host disposes the DI scope and the factory, so
        // a second pass would reach Stop() and touch the disposed signal.
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _blackoutApplied.Dispose();
    }
}

/// <summary>One look-ahead render job; see <see cref="LightingEngine.RequestAhead"/>.</summary>
public sealed class AheadRequest
{
    private DeviceFrame[] _sources = Array.Empty<DeviceFrame>();

    public AheadRequest(string[] deviceIds, long[] tickMs)
    {
        DeviceIds = deviceIds;
        TickMs = tickMs;
        Rendered = new DeviceFrame[tickMs.Length][];
    }

    public string[] DeviceIds { get; }
    public long[] TickMs { get; }
    public DeviceFrame[][]? Frames { get; set; }
    public ManualResetEventSlim Done { get; } = new(false);

    private volatile bool _abandoned;

    /// <summary>True once the requester stopped waiting; the engine drops the job.</summary>
    public bool Abandoned => _abandoned;

    public void Abandon() => _abandoned = true;

    internal IEffect? Effect { get; private set; }
    internal int ParamsSig { get; private set; }
    internal int PinSig { get; private set; }
    internal bool NeedsGlobal { get; private set; }
    internal int NextFrame { get; set; }
    internal DeviceFrame[][] Rendered { get; }

    internal void Begin(IEffect effect, int paramsSig, int pinSig, bool needsGlobal, DeviceFrame[] devices)
    {
        NeedsGlobal = needsGlobal;
        Effect = effect;
        ParamsSig = paramsSig;
        PinSig = pinSig;
        var sources = new List<DeviceFrame>();
        foreach (var d in devices)
        {
            if (Array.IndexOf(DeviceIds, d.Id) >= 0) sources.Add(d);
        }
        _sources = sources.ToArray();
    }

    // Layout-only copies of the live frames, so sampling never writes the published ones.
    internal DeviceFrame[] NewShadowFrames()
    {
        var shadow = new DeviceFrame[_sources.Length];
        for (var i = 0; i < _sources.Length; i++)
        {
            var s = _sources[i];
            shadow[i] = new DeviceFrame(s.Index, s.Id, s.LedCount, s.X, s.Y, s.W, s.H, s.Rotation, s.PhysicalIndex, s.ZoneIndex, s.ZoneOffset)
            {
                LedU = s.LedU, LedV = s.LedV, LedDisabled = s.LedDisabled, Archetype = s.Archetype,
                PreviewLedCount = s.PreviewLedCount, PreviewLayout = s.PreviewLayout,
            };
        }
        return shadow;
    }
}
