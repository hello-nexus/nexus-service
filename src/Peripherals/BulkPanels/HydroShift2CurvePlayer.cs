using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Plays library videos on the HydroShift II OLED Curved's own decoder: looped in video
/// mode, once per interval as a screen saver. While a video plays it owns the glass, so the
/// streamed panel is withheld and its frames dropped; afterwards the glass is readied for
/// frames again.
/// </summary>
public sealed class HydroShift2CurvePlayer : BackgroundService
{
    private const int TickMs = 250;
    private const int BufferPollMs = 50;

    /// <summary>The rate library videos are encoded at, told to the decoder before each play.</summary>
    public const int FrameRate = 30;

    private readonly BulkPanelHub _hub;
    private readonly HydroShift2CurveLcdDriver _driver;
    private readonly HydroShift2CurveMedia _media;
    private readonly IConfigStore _store;
    private readonly PanelDeviceRegistry _registry;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private volatile bool _ownsGlass;
    private volatile string? _playing;
    private bool _connected;
    private bool? _offlineClockSent;
    private int _saverMinutes;
    private long _nextSaverAt;

    public HydroShift2CurvePlayer(
        BulkPanelHub hub, HydroShift2CurveLcdDriver driver, HydroShift2CurveMedia media, IConfigStore store, PanelDeviceRegistry registry)
    {
        _hub = hub;
        _driver = driver;
        _media = media;
        _store = store;
        _registry = registry;
    }

    /// <summary>The glass is attached under Nexus Control.</summary>
    public bool IsConnected => _hub.IsConnected;

    /// <summary>A video is on the glass; the streamed panel must stay off it.</summary>
    public bool OwnsGlass => _ownsGlass;

    /// <summary>The library item on the glass, or null.</summary>
    public string? Playing => _playing;

    /// <summary>Raised when <see cref="OwnsGlass"/> flips, so the panel stream stops or resumes.</summary>
    public event Action? GlassOwnerChanged;

    /// <summary>Settings changed: act now rather than on the next tick.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>How the head is mounted, from its panel record; library videos are encoded to match.</summary>
    public (bool Flip180, bool Mirror) Mount() =>
        Record() is { } record ? (record.Flip180 == true, record.Mirror == true) : (false, false);

    private bool ShowsSecondaryMonitor() => Record()?.SecondaryMonitor == true;

    private Nexus.Service.Models.Panel.PanelDeviceRecord? Record()
    {
        foreach (var record in _registry.List())
        {
            if (record.Capabilities?.Family == HydroShift2CurveLcdDriver.Id)
            {
                return record;
            }
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TickMs, stoppingToken).ConfigureAwait(false);
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] player tick failed: {ex.Message}");
                Release(restoreBacklight: true);
            }
        }
        Release(restoreBacklight: true);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!_hub.IsConnected)
        {
            _connected = false;
            _offlineClockSent = null;
            return;
        }
        var settings = _store.Load().Devices.HydroShift2Curve;
        if (!_connected)
        {
            _connected = true;
            ScheduleSaver(settings.ScreenSaverMinutes, force: true);
        }
        if (settings.OfflineClock is { } clock && _offlineClockSent != clock
            && _hub.Exchange(pipe => _driver.SetOfflineClock(pipe, clock), false))
        {
            _offlineClockSent = clock;
        }
        ScheduleSaver(settings.ScreenSaverMinutes, force: false);

        if (settings.ScreenMode == HydroShift2CurveSettings.ScreenVideo && settings.Video is { } video && _media.Exists(video))
        {
            var mount = Mount();
            await PlayAsync(video, loop: true, saverBacklight: null,
                () => StillWanted(s => s.ScreenMode == HydroShift2CurveSettings.ScreenVideo && s.Video == video, mount), ct).ConfigureAwait(false);
            return;
        }
        if (_saverMinutes > 0 && Environment.TickCount64 >= _nextSaverAt)
        {
            ScheduleSaver(_saverMinutes, force: true);
            // A Windows monitor on the glass would be torn down and rebuilt every interval.
            if (settings.ScreenSaverVideo is { } saver && _media.Exists(saver) && !ShowsSecondaryMonitor())
            {
                var mount = Mount();
                await PlayAsync(saver, loop: false, saverBacklight: settings.ScreenSaverBrightness,
                    () => StillWanted(s => s.ScreenMode == HydroShift2CurveSettings.ScreenNexus && s.ScreenSaverMinutes > 0, mount), ct).ConfigureAwait(false);
            }
        }
    }

    private bool StillWanted(Func<HydroShift2CurveSettings, bool> settingsAllow, (bool, bool) mount) =>
        _hub.IsConnected && settingsAllow(_store.Load().Devices.HydroShift2Curve) && Mount() == mount;

    private void ScheduleSaver(int minutes, bool force)
    {
        if (!force && minutes == _saverMinutes)
        {
            return;
        }
        _saverMinutes = minutes;
        _nextSaverAt = Environment.TickCount64 + (Math.Max(minutes, 0) * 60_000L);
    }

    private async Task PlayAsync(string name, bool loop, int? saverBacklight, Func<bool> stillWanted, CancellationToken ct)
    {
        var (flip, mirror) = Mount();
        var path = await _media.EnsureVariantAsync(name, flip, mirror, ct).ConfigureAwait(false);
        if (path is null || !stillWanted())
        {
            return;
        }

        _driver.VideoOwnsGlass = true;
        _ownsGlass = true;
        _playing = name;
        RaiseGlassOwnerChanged();
        ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] playing '{name}'{(loop ? " in a loop" : " once")}");
        try
        {
            int block = _hub.Exchange(pipe => _driver.BeginVideo(pipe, FrameRate), 0);
            if (block <= 0)
            {
                return;
            }
            if (saverBacklight is { } backlight)
            {
                _hub.Exchange(pipe => _driver.SetBacklight(pipe, backlight), false);
            }

            var session = (uint)Environment.TickCount;
            var buffer = new byte[block];
            var started = Environment.TickCount64;
            using var file = File.OpenRead(path);
            while (stillWanted())
            {
                int read = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    if (!loop)
                    {
                        break;
                    }
                    file.Position = 0;
                    continue;
                }
                bool last = file.Position >= file.Length;
                var chunk = new ReadOnlyMemory<byte>(buffer, 0, read);
                var buffered = _hub.Exchange(pipe => _driver.SendVideoChunk(pipe, chunk.Span, last, session), (int?)null);
                // L-Connect's flow control: past a few queued blocks, poll until the decoder drains.
                if (buffered > HydroShift2CurveProtocol.H264BufferHigh)
                {
                    while (buffered > HydroShift2CurveProtocol.H264BufferLow && stillWanted())
                    {
                        await Task.Delay(BufferPollMs, ct).ConfigureAwait(false);
                        buffered = _hub.Exchange(pipe => _driver.QueryVideoBuffer(pipe), (int?)null);
                    }
                }
                if (buffered is null)
                {
                    break;
                }
            }

            if (!loop && _media.Duration(name) is { } seconds)
            {
                // The decoder plays on after the last chunk; hold the glass until the clip ends.
                var end = started + (long)(seconds * 1000);
                while (Environment.TickCount64 < end && stillWanted())
                {
                    await Task.Delay(TickMs, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Release(restoreBacklight: saverBacklight is not null);
        }
    }

    private void Release(bool restoreBacklight)
    {
        if (!_ownsGlass)
        {
            return;
        }
        _hub.Exchange(pipe =>
        {
            _driver.EndVideo(pipe);
            if (restoreBacklight)
            {
                _driver.SetBacklight(pipe, _driver.Brightness);
            }
            return true;
        }, false);
        _driver.VideoOwnsGlass = false;
        _ownsGlass = false;
        _playing = null;
        ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] video stopped, panel back on the glass");
        RaiseGlassOwnerChanged();
    }

    private void RaiseGlassOwnerChanged()
    {
        try
        {
            GlassOwnerChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] glass owner subscriber failed: {ex.Message}");
        }
    }
}
