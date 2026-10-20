using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Streams 480x480 JPEG frames to the attached LCD display. Reads settings on
/// each loop pass to pick up media, brightness, and rotation changes immediately.
/// </summary>
public sealed class CorsairLinkLcdWorker : BackgroundService
{

    private readonly CorsairLinkLcd _lcd;
    private readonly CorsairLinkLcdMediaLibrary _library;
    private readonly IConfigStore _store;
    private readonly CorsairLinkHubs _hubs;
    private readonly DeviceControlGate _gate;

    private string? _loadedMediaId;
    private LcdImageData? _loadedImage;
    // Sentinels ensure brightness and rotation are pushed on the first connected loop.
    private byte _appliedBrightness = 255;
    private byte _appliedRotation = 255;
    private int _appliedGeneration = -1;
    private bool _lockSlept;
    private long _lastDarkSentMs;
    private const long DarkReassertMs = 2000;

    public CorsairLinkLcdWorker(
        CorsairLinkLcd lcd,
        CorsairLinkLcdMediaLibrary library,
        IConfigStore store,
        CorsairLinkHubs hubs,
        DeviceControlGate gate)
    {
        _lcd = lcd;
        _library = library;
        _store = store;
        _hubs = hubs;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Same switch the panel stream rides: until the user claims the glass,
                // nothing here writes to it.
                if (!_gate.IsEnabled(CorsairLinkLcd.DeviceId) || _hubs.LcdHub is null)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                if (!_lcd.HasDevice)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                // A reconnect re-opens the panel with firmware defaults, so the sentinels
                // go back to "nothing applied yet" and the block below re-pushes.
                SyncGeneration();

                var s = _store.Load().Devices.Corsair;
                _lcd.ShowsStoredImage = s.LcdDeviceMediaId is not null;

                // Brightness and rotation are feature reports on their own channel, so they
                // apply while a panel session streams.
                SyncBrightness(s);

                if (s.LcdRotation != _appliedRotation)
                {
                    _lcd.SetRotation(s.LcdRotation);
                    _appliedRotation = s.LcdRotation;
                }

                // A panel session owns the frame pipe while it streams widget frames; the
                // still or GIF resumes on the pass after the frames stop.
                if (_lcd.IsStreamLive)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                if (s.LcdSelectedMediaId is null)
                {
                    // A profile with no selected image takes the glass back from the last still it streamed.
                    if (_loadedMediaId is not null)
                    {
                        _loadedMediaId = null;
                        _loadedImage = null;
                        _lcd.SelectHandBackScreen(s.LcdDeviceMediaId is not null);
                    }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                if (s.LcdSelectedMediaId != _loadedMediaId)
                {
                    _loadedImage = _library.LoadImageData(s.LcdSelectedMediaId);
                    _loadedMediaId = s.LcdSelectedMediaId;
                }

                if (_loadedImage is null)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                var image = _loadedImage;
                await StreamImageAsync(image, mem => _lcd.SendFrame(mem.Span), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[corsair-lcd] worker error: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    // The lock fade, sampled by the runner. It owns the backlight while it runs: the re-send of 0
    // below waits for it to land, and nothing here touches the stored setting.
    private readonly object _fadeGate = new();
    private LcdFade? _fade;
    private LcdFadeRunner? _fadeRunner;

    /// <summary>Millisecond clock the fade reads; tests replace it.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    internal LcdFadeRunner FadeRunner => _fadeRunner ??= new LcdFadeRunner(StepFade, onFault: FaultFade);

    internal void SyncGeneration()
    {
        lock (_fadeGate)
        {
            var generation = _lcd.AttachGeneration;
            if (generation != _appliedGeneration)
            {
                _appliedGeneration = generation;
                _appliedBrightness = 255;
                _appliedRotation = 255;
                // A screen that attaches or re-opens mid-lock goes dark at once; no ramp.
                _fade = null;
            }
        }
    }

    /// <summary>Pushes the brightness the glass should show now: a ramp down or up across a lock, dark while it holds, else the stored value.</summary>
    internal void SyncBrightness(CorsairSettings s)
    {
        lock (_fadeGate)
        {
            // The lock override lives here, not in the stored setting: an attach or
            // re-open resets the sentinels above, so the dark value wins over the stored
            // one on every path, and unlock simply stops overriding.
            var sleeping = _lcd.IsLockSleeping(() => s.SleepWhenLocked);
            var fresh = _appliedBrightness == 255;
            if (sleeping != _lockSlept)
            {
                _lockSlept = sleeping;
                ServiceLog.Info($"[lcd-lock] corsair LCD {(sleeping ? "sleeping" : "waking")}");
                if (fresh)
                {
                    _fade = null;
                }
                else
                {
                    // From the level on the glass now (or the step a ramp is on) to the other end:
                    // the stored level, which a lock never touches, is what a fade up returns to.
                    var from = _fade is { } running ? running.LevelAt(Clock()) : _appliedBrightness;
                    var to = sleeping ? 0 : s.LcdBrightness;
                    _fade = from == to
                        ? null
                        : new LcdFade(from, to, sleeping ? SleepBlackoutCoordinator.LockFadeDuration : SleepBlackoutCoordinator.UnlockFadeDuration, Clock());
                    if (_fade is not null)
                    {
                        FadeRunner.Kick();
                    }
                }
            }
            if (_fade is not null)
            {
                return;
            }
            var brightness = sleeping ? (byte)0 : s.LcdBrightness;
            // Frames keep flowing while locked (the firmware drops to its own screen if
            // they stop), and whether the first frame after a host-mode change moves the
            // backlight is unmeasured, so the dark value is also re-sent at a slow cadence.
            var reassert = sleeping && Environment.TickCount64 - _lastDarkSentMs >= DarkReassertMs;
            if (brightness != _appliedBrightness || reassert)
            {
                _lcd.SetBrightness(brightness);
                _appliedBrightness = brightness;
                _lastDarkSentMs = Environment.TickCount64;
            }
        }
    }

    /// <summary>
    /// A step threw: the ramp is dropped. With no ramp the next pass of the worker loop writes the
    /// settled level (dark while the lock holds, else the stored one), so the glass is not left on a half step.
    /// </summary>
    internal void FaultFade()
    {
        lock (_fadeGate) { _fade = null; }
    }

    /// <summary>One sample of the fade: writes the stepped level when it changed and finishes the ramp when it lands.</summary>
    internal bool StepFade()
    {
        lock (_fadeGate)
        {
            if (_fade is not { } fade)
            {
                return false;
            }
            var now = Clock();
            var level = (byte)fade.LevelAt(now);
            var done = fade.IsDoneAt(now);
            if (level != _appliedBrightness || done)
            {
                if (_lcd.SetBrightness(level))
                {
                    _appliedBrightness = level;
                }
            }
            if (done)
            {
                _fade = null;
                _lastDarkSentMs = Environment.TickCount64;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Executes one image display cycle. Static images send one frame followed by
    /// the hardware keep-alive delay (lsh.go:5071). Animated images send all frames
    /// in order, each followed by its GIF delay clamped to the 10ms minimum.
    /// </summary>
    internal static async Task StreamImageAsync(
        LcdImageData image,
        Action<ReadOnlyMemory<byte>> sendFrame,
        CancellationToken ct)
    {
        if (image.Frames.Length == 1)
        {
            sendFrame(image.Frames[0].JpegBytes);
            // Firmware clears the panel if frames stop arriving; 10ms matches lsh.go:5071.
            await Task.Delay(10, ct).ConfigureAwait(false);
            return;
        }

        foreach (var frame in image.Frames)
        {
            if (ct.IsCancellationRequested) break;
            sendFrame(frame.JpegBytes);
            await Task.Delay(Math.Max(10, frame.DelayMs), ct).ConfigureAwait(false);
        }
    }
}
