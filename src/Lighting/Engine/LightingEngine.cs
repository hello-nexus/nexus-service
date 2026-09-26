using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine.Effects;

namespace Nexus.Service.Lighting.Engine;

public sealed class LightingEngine : IDisposable
{
    private readonly CanvasBuffer _canvas;
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

    public LightingEngine() { _canvas = new CanvasBuffer(160, 90); }

    public int FrameIntervalMs { get; set; } = 33;

    /// <summary>
    /// Selects the canvas-to-LED sampling technique. True = LED FOOTPRINT
    /// SAMPLING: each LED integrates its cell of the device frame (see
    /// <see cref="SampleLedFootprint"/>), so sparse content - music-reactive
    /// bars, isolated lit pixels - registers anywhere inside the frame.
    /// False = POINT SAMPLING: each LED reads the single canvas pixel under
    /// its mapped point; content that misses those exact pixels leaves the
    /// device dark. Flip to false to revert to the pre-footprint behaviour.
    /// Ignored under <see cref="FullFrameSampling"/>, which always takes the
    /// plain cell mean (<see cref="SampleLedBox"/>).
    /// </summary>
    public bool FootprintSamplingEnabled { get; set; } = true;

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
    private const float FullFrameStripBreadth = 0.08f;

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

    // Which part of its frame each stacked device samples; null or a miss
    // means the whole frame. Replaced wholesale on every stack save.
    private volatile Dictionary<string, Nexus.Service.Lighting.StackSlots.Slot>? _stackSlots;
    public void SetStackSlots(IReadOnlyList<Nexus.Service.Persistence.DeviceGroup> stacks)
    {
        _stackSlots = Nexus.Service.Lighting.StackSlots.Index(stacks);
    }

    /// <summary>
    /// Per-device Static assignments. Set once at wire-up; null in tests and any
    /// host that never assigns one, in which case sampling is unchanged.
    /// </summary>
    public Nexus.Service.Lighting.StaticDeviceEffectTracker? StaticEffects { get; set; }

    /// <summary>
    /// Builds the IEffect for an assignment. Supplied by the provider, which
    /// owns shader construction - the engine only renders what it is handed.
    /// </summary>
    public Func<Nexus.Service.Lighting.StaticDeviceAssignment, IEffect?>? StaticEffectFactory { get; set; }

    // One assignment renders once into this scratch canvas and every device
    // wearing that look samples it. Static is a held frame, so a look is
    // rendered only when its key changes, not per frame.
    private CanvasBuffer? _assignCanvas;
    private readonly Dictionary<string, byte[]> _assignRenders = new(StringComparer.Ordinal);
    private int _assignVersionSeen = -1;
    // Reused per-frame scratch: which devices ApplyTestOverlays painted. Valid
    // for the rest of the tick after SampleDevicesFromCanvas returns.
    private bool[] _overlaid = Array.Empty<bool>();

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
            _currentEffect = effect;
            _paused = false;
            _frozen = false;
            // The user picking a mode is the escape hatch if a resume event
            // never lands: it always ends in a lit device, never a dark one.
            ReleaseBlackoutState();
            Interlocked.Increment(ref _frozenEpoch);
            try
            { old?.Dispose(); }
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
                var effect = _currentEffect;
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
                        var epoch = Volatile.Read(ref _frozenEpoch);
                        if (!_frozen || Volatile.Read(ref _frozenRenderedEpoch) != epoch)
                        {
                            effect.RenderFrame(_canvas, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            Volatile.Write(ref _frozenRenderedEpoch, epoch);
                        }
                        // One snapshot for the sample, the game pass and the
                        // publish: UpdateDevices swaps the array lock-free,
                        // and the skip mask below is indexed against it.
                        var devices = _devices;
                        SampleDevicesFromCanvas(devices);
                        if (effect is GameSyncEffect gs)
                        {
                            // A locked look (or a highlight / test pattern)
                            // already owns its device; the game frame must
                            // not paint over it.
                            gs.WriteToDevices(devices, _overlaid);
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
                    SerializeAndBroadcast();
                    FramePublished?.Invoke();
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
            _canvas.Clear();
            foreach (var dev in _devices)
            {
                dev.Clear();
            }
        }
    }

    private void SampleDevicesFromCanvas(DeviceFrame[] devices)
    {
        var cw = _canvas.Width;
        var ch = _canvas.Height;
        const float CW = 1000f, CH = 600f;
        // Overlays first, so an LED they own never gets a canvas colour it is
        // about to lose. Correctness no longer rests on this - DeviceFrame
        // publishes once per tick - but painting an LED twice is wasted work.
        var overlaid = ApplyTestOverlays(devices);
        for (var di = 0; di < devices.Length; di++)
        {
            var dev = devices[di];
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
            var fullFrame = FullFrameSampling;
            // A full-frame device's cell is a slice of the whole canvas, which
            // SampleLedFootprint's lit-pixel weighting reads wrong; it takes the
            // plain mean instead. See SampleLedBox for why.
            var footprint = FootprintSamplingEnabled && !fullFrame;
            var rectX = fullFrame ? 0f : dev.X;
            var rectY = fullFrame ? 0f : dev.Y;
            var rectW = fullFrame ? CW : dev.W;
            var rectH = fullFrame ? CH : dev.H;
            var frameCx = rectX + rectW * 0.5f;
            var frameCy = rectY + rectH * 0.5f;
            var kx = cw / CW;
            var ky = ch / CH;
            // A full-frame device is not placed on the canvas at all, so it has
            // no orientation to honour either: the pattern reads along its LED
            // order whichever way its card is turned.
            var rad = fullFrame ? 0f : (((dev.Rotation % 360) + 360) % 360) * (MathF.PI / 180f);
            var cos = MathF.Cos(rad);
            var sin = MathF.Sin(rad);
            var absCos = MathF.Abs(cos);
            var absSin = MathF.Abs(sin);
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

            if (devLedU is not null && devLedV is not null
                && devLedU.Length == ledCount && devLedV.Length == ledCount)
            {
                // Cell dims from a uniform cols x rows density estimate matched
                // to the rect aspect. The true UV layout may be non-uniform, so
                // neighbouring cells can overlap or leave small gaps; keyboards
                // land at ~key-sized cells and keep per-key sharpness. The cell
                // read is the turned cell's bounding box.
                var aspect = rectH > 0.001f ? rectW / rectH : 1f;
                var cols = Math.Max(1, (int)MathF.Round(MathF.Sqrt(ledCount * aspect)));
                var rows = Math.Max(1, (ledCount + cols - 1) / cols);
                // Cells are at least one pixel on each axis.
                var cellHalfW = Math.Max(1f / kx, rectW / cols) * 0.5f;
                var cellHalfH = Math.Max(1f / ky, rectH / rows) * 0.5f;
                // Full frame: the grid estimate spreads cols over the WHOLE
                // canvas, so a device whose LEDs really run in a line gets a
                // cell an order of magnitude too wide and the box mean flattens
                // the pattern into a wash. The rect is not this device's frame
                // here, so the layout tells us nothing about spacing - take the
                // same cell the linear path does, n samples across the pattern
                // by a thin slice.
                // Width comes from the LED COUNT, not the span the u values
                // actually cover, which matters for a hand-authored map that
                // clusters its LEDs into a sliver of u: every LED then reads the
                // same narrow window and the device pulses with the bands. A
                // count-derived cell is the damping choice there - sizing it to
                // the real (small) u extent would narrow the window further and
                // sharpen the pulse, not soften it. The fix, if one is wanted,
                // is to spread u over the full width under full frame.
                if (fullFrame)
                {
                    cellHalfW = Math.Max(1f / kx, rectW / ledCount) * 0.5f;
                    cellHalfH = Math.Max(1f / ky, rectH * FullFrameStripBreadth) * 0.5f;
                }
                var uvHalfW = (cellHalfW * absCos + cellHalfH * absSin) * kx;
                var uvHalfH = (cellHalfW * absSin + cellHalfH * absCos) * ky;
                for (int i = 0; i < ledCount; i++)
                {
                    if (devLedDisabled is not null && i < devLedDisabled.Length && devLedDisabled[i])
                    {
                        dev.SetLed(i, 0, 0, 0);
                        continue;
                    }
                    var lx = rectX + devLedU[i] * rectW - frameCx;
                    var ly = rectY + devLedV[i] * rectH - frameCy;
                    var sx = (frameCx + lx * cos - ly * sin) * kx;
                    var sy = (frameCy + lx * sin + ly * cos) * ky;
                    var (r, g, b) = footprint
                        ? SampleLedFootprint(sx - uvHalfW, sy - uvHalfH, sx + uvHalfW, sy + uvHalfH)
                        : fullFrame
                            ? SampleLedBox(sx - uvHalfW, sy - uvHalfH, sx + uvHalfW, sy + uvHalfH)
                            : _canvas.GetPixel((int)sx, (int)sy);
                    dev.SetLed(i, r, g, b);
                }
                continue;
            }

            // Linear strip fallback: the LEDs walk the unturned rect's width
            // along its centreline, first LED at the left. Each LED's cell spans
            // the full breadth across the strip and one LED pitch along it, so
            // content anywhere inside the frame reaches the LED at that position
            // instead of only the centerline; the read is the turned cell's
            // bounding box.
            var pitchHalf = Math.Max(1f / kx, rectW / ledCount) * 0.5f;
            // Full frame: a cell spanning the canvas top to bottom averages
            // every band into one colour, so the strip reads a thin slice
            // through the middle instead.
            var breadth = fullFrame ? rectH * FullFrameStripBreadth : rectH;
            var breadthHalf = Math.Max(1f / ky, breadth) * 0.5f;
            var linHalfW = (pitchHalf * absCos + breadthHalf * absSin) * kx;
            var linHalfH = (pitchHalf * absSin + breadthHalf * absCos) * ky;
            var midY = rectY + rectH * 0.5f - frameCy;
            var denom = ledCount > 1 ? 1f / (ledCount - 1) : 0f;
            for (int i = 0; i < ledCount; i++)
            {
                if (devLedDisabled is not null && i < devLedDisabled.Length && devLedDisabled[i])
                {
                    dev.SetLed(i, 0, 0, 0);
                    continue;
                }
                var t = ledCount > 1 ? i * denom - 0.5f : 0f;
                var lx = rectX + rectW * 0.5f + t * rectW - frameCx;
                var sx = (frameCx + lx * cos - midY * sin) * kx;
                var sy = (frameCy + lx * sin + midY * cos) * ky;
                var (r, g, b) = footprint
                    ? SampleLedFootprint(sx - linHalfW, sy - linHalfH, sx + linHalfW, sy + linHalfH)
                    : fullFrame
                        ? SampleLedBox(sx - linHalfW, sy - linHalfH, sx + linHalfW, sy + linHalfH)
                        : _canvas.GetPixel((int)sx, (int)sy);
                dev.SetLed(i, r, g, b);
            }
        }
    }

    /// <summary>
    /// LED FOOTPRINT SAMPLING (gated by <see cref="FootprintSamplingEnabled"/>):
    /// samples the LED's cell of the canvas instead of a single point, so sparse
    /// content (music-reactive bars, isolated lit pixels) registers wherever it
    /// lands inside the frame. Samples are weighted by max-channel luminance
    /// squared: lit pixels dominate dark filler, an all-dark cell stays black,
    /// and hue is preserved (a per-channel max would mix channels from different
    /// pixels). Reads every canvas pixel in the cell; cells tile the frame
    /// (linear) or partition it by LED density (UV), so per-device cost is
    /// bounded by the frame's canvas-pixel area per rendered frame.
    /// </summary>
    private (byte r, byte g, byte b) SampleLedFootprint(float x0, float y0, float x1, float y1)
    {
        var ix0 = Math.Clamp((int)MathF.Floor(x0), 0, _canvas.Width - 1);
        var iy0 = Math.Clamp((int)MathF.Floor(y0), 0, _canvas.Height - 1);
        var ix1 = Math.Clamp((int)MathF.Ceiling(x1) - 1, ix0, _canvas.Width - 1);
        var iy1 = Math.Clamp((int)MathF.Ceiling(y1) - 1, iy0, _canvas.Height - 1);
        long wSum = 0, rSum = 0, gSum = 0, bSum = 0;
        for (int py = iy0; py <= iy1; py++)
        {
            for (int px = ix0; px <= ix1; px++)
            {
                var (r, g, b) = _canvas.GetPixel(px, py);
                int m = Math.Max(r, Math.Max(g, b));
                if (m == 0)
                {
                    continue;
                }
                long w = m * m;
                wSum += w;
                rSum += w * r;
                gSum += w * g;
                bSum += w * b;
            }
        }
        if (wSum == 0)
        {
            return (0, 0, 0);
        }
        return ((byte)(rSum / wSum), (byte)(gSum / wSum), (byte)(bSum / wSum));
    }

    /// <summary>
    /// Plain unweighted mean of the LED's cell, black pixels included - the read
    /// under <see cref="FullFrameSampling"/>, where the cell is a slice of the
    /// whole canvas rather than of the device's own frame. That makes it a box
    /// filter over the pattern: a device with LEDs to spare resolves the dark
    /// gaps, and one with too few settles on the pattern's average instead of
    /// aliasing (a 1-LED zone whose point sample would blink at the band rate
    /// holds a steady mid tone). <see cref="SampleLedFootprint"/>'s lit-pixel
    /// weighting is wrong here for the opposite reason: it exists to find sparse
    /// content inside a frame, and a sweep fills the frame.
    /// </summary>
    private (byte r, byte g, byte b) SampleLedBox(float x0, float y0, float x1, float y1)
    {
        var ix0 = Math.Clamp((int)MathF.Floor(x0), 0, _canvas.Width - 1);
        var iy0 = Math.Clamp((int)MathF.Floor(y0), 0, _canvas.Height - 1);
        var ix1 = Math.Clamp((int)MathF.Ceiling(x1) - 1, ix0, _canvas.Width - 1);
        var iy1 = Math.Clamp((int)MathF.Ceiling(y1) - 1, iy0, _canvas.Height - 1);
        long n = 0, rSum = 0, gSum = 0, bSum = 0;
        for (int py = iy0; py <= iy1; py++)
        {
            for (int px = ix0; px <= ix1; px++)
            {
                var (r, g, b) = _canvas.GetPixel(px, py);
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
    /// Paint the devices whose frame comes from something other than the canvas:
    /// a zone highlight, a per-device Static assignment (every one in Static,
    /// only locked ones elsewhere), or a test pattern.
    /// Returns, per device, whether it painted the whole preview range, so the
    /// caller can skip a canvas sample that would only be overwritten.
    /// </summary>
    private bool[] ApplyTestOverlays(DeviceFrame[] devices)
    {
        if (_overlaid.Length < devices.Length) _overlaid = new bool[devices.Length];
        var owned = _overlaid;
        Array.Clear(owned, 0, devices.Length);
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
                owned[di] = true;
                continue;
            }

            if (StaticEffects is not null
                && dev.TestPattern is null
                && StaticEffects.TryGet(dev.Id, out var assignment))
            {
                // A palette pick is a colour, not an effect: nothing to render,
                // nothing to sample, no canvas. Paint it and move on.
                if (Nexus.Service.Lighting.StaticColorHex.TryParse(assignment.Color, out var cr, out var cg, out var cb))
                {
                    for (int i = 0; i < previewCount; i++) dev.SetLed(i, cr, cg, cb);
                    owned[di] = true;
                    continue;
                }
                var render = RenderAssignment(assignment);
                if (render is not null)
                {
                    PaintFromAssignment(dev, previewCount, render);
                    owned[di] = true;
                    continue;
                }
            }

            var pattern = dev.TestPattern;
            if (pattern is not null)
            {
                if (pattern == "none")
                {
                    dev.Fill(0, 0, 0);
                    owned[di] = true;
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
                        owned[di] = covered >= previewCount;
                    }
                    // No UVs: nothing was painted, so the canvas sample stands.
                }
            }
        }
        return owned;
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
            buf[pos++] = (byte)dev.Index;
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
    /// </summary>
    private byte[]? RenderAssignment(Nexus.Service.Lighting.StaticDeviceAssignment assignment)
    {
        var tracker = StaticEffects;
        var factory = StaticEffectFactory;
        if (tracker is null || factory is null) return null;
        if (tracker.Version != _assignVersionSeen)
        {
            // A control moved: every cached look is potentially stale.
            _assignVersionSeen = tracker.Version;
            _assignRenders.Clear();
        }
        var key = assignment.Key();
        if (_assignRenders.TryGetValue(key, out var cached)) return cached;

        IEffect? effect = null;
        try
        {
            effect = factory(assignment);
            if (effect is null) return null;
            _assignCanvas ??= new CanvasBuffer(_canvas.Width, _canvas.Height);
            _assignCanvas.Clear();
            // Static holds a frame, so one render at tick 0 is the whole look.
            effect.RenderFrame(_assignCanvas, 0);
            var pixels = _assignCanvas.Pixels.ToArray();
            _assignRenders[key] = pixels;
            return pixels;
        }
        catch (Exception ex)
        {
            // A look that cannot render must not take the whole frame down; the
            // device falls through to the shared canvas.
            Gpu.GpuContext.Log($"[static-assign] render failed for {assignment.Effect}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { effect?.Dispose(); } catch { /* best-effort */ }
        }
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
