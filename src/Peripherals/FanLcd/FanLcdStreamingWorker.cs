using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// Streams each attached fan screen's configured content, for one <see cref="IFanLcdScreens"/>
/// family. Runs one independent loop per attached serial, since each screen's animation has
/// its own frame cadence and a single shared loop cannot pace three screens with different
/// GIF/video delays at once. A screen in panel mode is left to its streamed-panel session.
/// </summary>
public sealed class FanLcdStreamingWorker : BackgroundService
{
    private const int DiscoveryPollMs = 2000;
    private const int IdlePollMs = 1000;
    /// <summary>~1 fps: a sensor/clock frame only needs to look live, not smooth.</summary>
    private const int SensorClockPollMs = 1000;
    /// <summary>How often a wait re-checks the screen's settings, so a new pick shows within this.</summary>
    private const int ChangePollMs = 100;

    private readonly IFanLcdScreens _screens;
    private readonly Slv3LcdMediaLibrary _library;
    private readonly Slv3LcdSensorReader _sensorReader;
    private readonly IConfigStore _store;
    private readonly DeviceControlGate _gate;
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Loop)> _running = new(StringComparer.OrdinalIgnoreCase);

    // Cancelled loops still unwinding; a serial restarts only once its old loop's hand-back has
    // run, or that hand-back would take the screen from the new loop.
    private readonly Dictionary<string, Task> _stopping = new(StringComparer.OrdinalIgnoreCase);

    public FanLcdStreamingWorker(IFanLcdScreens screens, Slv3LcdMediaLibrary library, Slv3LcdSensorReader sensorReader, IConfigStore store, DeviceControlGate gate)
    {
        _screens = screens;
        _library = library;
        _sensorReader = sensorReader;
        _store = store;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_gate.IsEnabled(_screens.HandlerId))
                    {
                        StopAll();
                    }
                    else
                    {
                        ReconcileScreenLoops(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ServiceLog.Error($"[{_screens.LogTag}] streaming worker error: {ex.Message}");
                }

                await Task.Delay(DiscoveryPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // The delay above throws on the same cancellation that ends this
            // loop, so StopAll must run here rather than after the loop to
            // guarantee every per-screen CancellationTokenSource is disposed.
            StopAll();
        }
    }

    private void ReconcileScreenLoops(CancellationToken stoppingToken)
    {
        foreach (var (serial, unwinding) in new List<KeyValuePair<string, Task>>(_stopping))
        {
            if (unwinding.IsCompleted)
            {
                _stopping.Remove(serial);
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serial in _screens.AttachedSerials())
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                continue;
            }
            seen.Add(serial);
            if (_running.ContainsKey(serial))
            {
                continue;
            }
            if (_stopping.ContainsKey(serial))
            {
                continue;
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[serial] = (cts, RunScreenLoopAsync(serial, cts.Token));
        }

        foreach (var serial in new List<string>(_running.Keys))
        {
            if (!seen.Contains(serial))
            {
                StopScreen(serial);
            }
        }
    }

    private void StopScreen(string serial)
    {
        if (!_running.Remove(serial, out var loop))
        {
            return;
        }
        loop.Cts.Cancel();
        loop.Cts.Dispose();
        _stopping[serial] = loop.Loop;
    }

    private void StopAll()
    {
        foreach (var serial in new List<string>(_running.Keys))
        {
            StopScreen(serial);
        }
    }

    /// <summary>
    /// One screen's lifetime loop: reapplies brightness/rotation/content on
    /// change and streams the active content. A fresh call (attach after
    /// detach) starts with sentinel "applied" values, so a static image
    /// re-pushes automatically on reconnect without special-casing it.
    /// </summary>
    private async Task RunScreenLoopAsync(string serial, CancellationToken ct)
    {
        string? loadedMediaId = null;
        Slv3LcdImageData? loadedImage = null;
        var appliedBrightness = -1;
        var lockSlept = false;
        var appliedRotation = -1;
        var appliedContentType = "";
        var pushedStatic = false;
        // Animation phase resets on every (re)connect and content-type switch;
        // continuity across a reconnect is not required.
        var animationStartUtc = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _screens.PrepareFrames();

                    var settings = _screens.SettingsOf(_store.Load().Devices).TryGetValue(serial, out var s)
                        ? s
                        : new LianLiWirelessScreenSettings();

                    _screens.SyncBrightness(serial, settings, ref appliedBrightness, ref lockSlept);
                    if (settings.Rotation != appliedRotation)
                    {
                        _screens.SetRotation(serial, settings.Rotation);
                        appliedRotation = settings.Rotation;
                    }

                    if (settings.MediaId != loadedMediaId)
                    {
                        loadedMediaId = settings.MediaId;
                        loadedImage = loadedMediaId is null ? null : _library.LoadFrames(loadedMediaId);
                        pushedStatic = false;
                    }
                    var mode = FanLcdContent.ModeOf(settings);
                    var pick = PickOf(settings);
                    if (mode != appliedContentType)
                    {
                        appliedContentType = mode;
                        pushedStatic = false;
                        animationStartUtc = DateTime.UtcNow;
                        // Taken back before this loop draws or releases, so a lingering panel
                        // session's frame cannot land after the switch.
                        _screens.SetPanelOwned(serial, mode == FanLcdContent.Panel);
                        if (mode == "off")
                        {
                            _screens.Release(serial);
                        }
                    }

                    switch (mode)
                    {
                        case "image":
                        case "gif":
                        case "video":
                            if (loadedImage is null)
                            {
                                await IdleAsync(serial, pick, IdlePollMs, ct).ConfigureAwait(false);
                                continue;
                            }
                            if (loadedImage.Frames.Length == 1)
                            {
                                if (!pushedStatic)
                                {
                                    pushedStatic = _screens.PushImage(serial, loadedImage.Frames[0].JpegBytes);
                                }
                                await IdleAsync(serial, pick, IdlePollMs, ct).ConfigureAwait(false);
                                continue;
                            }
                            await StreamAnimatedAsync(serial, loadedImage, pick, ct).ConfigureAwait(false);
                            break;

                        case "sensor":
                        {
                            var reading = _sensorReader.Read(settings.SensorSource, settings.TempUnit);
                            var jpeg = Slv3LcdSensorRenderer.Render(
                                settings.SensorStyle, reading.Value, reading.Min, reading.Max, reading.Label, reading.Unit,
                                settings.ColorA, settings.ColorB);
                            _screens.PushImage(serial, jpeg);
                            await IdleAsync(serial, pick, SensorClockPollMs, ct).ConfigureAwait(false);
                            break;
                        }

                        case "clock":
                        {
                            var jpeg = Slv3LcdClockRenderer.Render(settings.ClockFace, DateTime.Now, settings.ColorA, settings.ColorB);
                            _screens.PushImage(serial, jpeg);
                            await IdleAsync(serial, pick, SensorClockPollMs, ct).ConfigureAwait(false);
                            break;
                        }

                        case "animation":
                        {
                            var elapsed = (DateTime.UtcNow - animationStartUtc).TotalSeconds;
                            var jpeg = Slv3LcdAnimationRenderer.Render(settings.AnimationId, elapsed, settings.ColorA, settings.ColorB);
                            _screens.PushImage(serial, jpeg);
                            await Task.Delay(Slv3LcdAnimationRenderer.FrameIntervalMs, ct).ConfigureAwait(false);
                            break;
                        }

                        default:
                            await IdleAsync(serial, pick, IdlePollMs, ct).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[{_screens.LogTag}] stream error for {serial}: {ex.Message}");
                    await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // After this loop's last push, so nothing it sends can follow the hand-back.
            _screens.SetPanelOwned(serial, false);
            _screens.Release(serial);
        }
    }

    /// <summary>Everything a pass applies from the screen's settings, so any edit ends a wait or a clip early.</summary>
    internal readonly record struct ScreenPick(
        string Mode, string? MediaId, byte Brightness, bool SleepWhenLocked, byte Rotation,
        string? SensorSource, string? SensorStyle, string? ClockFace, string? AnimationId,
        string? ColorA, string? ColorB, string? TempUnit);

    internal static ScreenPick PickOf(LianLiWirelessScreenSettings screen) => new(
        FanLcdContent.ModeOf(screen), screen.MediaId, screen.Brightness, screen.SleepWhenLocked, screen.Rotation,
        screen.SensorSource, screen.SensorStyle, screen.ClockFace, screen.AnimationId,
        screen.ColorA, screen.ColorB, screen.TempUnit);

    /// <summary>A screen with no stored settings reads as the defaults the pass used.</summary>
    private bool StillShowing(string serial, ScreenPick pick) =>
        PickOf(_screens.SettingsOf(_store.Load().Devices).TryGetValue(serial, out var s) ? s : new LianLiWirelessScreenSettings()) == pick;

    /// <summary>Waits <paramref name="ms"/> on a deadline, so a frame cadence holds; returns early once the screen's settings change.</summary>
    private async Task IdleAsync(string serial, ScreenPick pick, int ms, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + ms;
        while (StillShowing(serial, pick))
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                break;
            }
            await Task.Delay((int)Math.Min(ChangePollMs, remaining), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Plays one loop of the clip, stopping at the next frame once the screen's settings change.</summary>
    private async Task StreamAnimatedAsync(string serial, Slv3LcdImageData image, ScreenPick pick, CancellationToken ct)
    {
        foreach (var frame in image.Frames)
        {
            if (ct.IsCancellationRequested || !StillShowing(serial, pick))
            {
                break;
            }
            _screens.PushImage(serial, frame.JpegBytes);
            await Task.Delay(Math.Max(10, frame.DelayMs), ct).ConfigureAwait(false);
        }
    }
}
