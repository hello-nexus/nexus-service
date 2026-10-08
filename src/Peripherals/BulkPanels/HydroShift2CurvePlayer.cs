using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Panel;
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
    /// <summary>After the glass refused a play, wait this long before trying again rather than retaking it every tick.</summary>
    private const long RetryAfterFailMs = 30_000;

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
    private TaskCompletionSource _stopped = Stopped();
    private bool _connected;
    private bool? _offlineClockSent;
    private bool? _offlineClockTriedInPlay;
    private int? _saverBacklight;
    private int _saverMinutes;
    private long _nextSaverAt;
    private long _retryAt;
    private long _wokenAt;

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

    /// <summary>Completes once the video on the glass now has stopped and released its file.</summary>
    public Task WhenStopped => Volatile.Read(ref _stopped).Task;

    /// <summary>Raised when <see cref="OwnsGlass"/> flips, so the panel stream stops or resumes.</summary>
    public event Action? GlassOwnerChanged;

    /// <summary>Settings changed: act now rather than on the next tick.</summary>
    public void Wake()
    {
        Volatile.Write(ref _wokenAt, Environment.TickCount64);
        Volatile.Write(ref _retryAt, 0);
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>How the head is mounted, from its panel record; library videos are encoded to match.</summary>
    public (bool Flip180, bool Mirror) Mount() => MountOf(Record());

    private static (bool Flip180, bool Mirror) MountOf(PanelDeviceRecord? record) =>
        record is null ? (false, false) : (record.Flip180 == true, record.Mirror == true);

    private PanelDeviceRecord? Record()
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
            long tickStartedAt = Environment.TickCount64;
            try
            {
                await _wake.WaitAsync(TickMs, stoppingToken).ConfigureAwait(false);
                tickStartedAt = Environment.TickCount64;
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] player tick failed: {ex.Message}");
                Release();
                BackOff(tickStartedAt);
            }
        }
        Release();
    }

    /// <summary>Holds off the next play after a failure, unless settings changed since the attempt began.</summary>
    private void BackOff(long attemptStartedAt)
    {
        if (Volatile.Read(ref _wokenAt) <= attemptStartedAt)
        {
            Volatile.Write(ref _retryAt, Environment.TickCount64 + RetryAfterFailMs);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var settings = _store.Load().Devices.HydroShift2Curve;
        _driver.OwnScreenOnRelease = settings.OfflineClock == true && settings.ScreenMode == HydroShift2CurveSettings.ScreenNexus;
        if (!_hub.IsConnected)
        {
            _connected = false;
            _offlineClockSent = null;
            return;
        }
        if (!_connected)
        {
            _connected = true;
            ScheduleSaver(settings.ScreenSaverMinutes, force: true);
        }
        ApplyOfflineClock(settings);
        ScheduleSaver(settings.ScreenSaverMinutes, force: false);
        if (Environment.TickCount64 < Volatile.Read(ref _retryAt))
        {
            return;
        }

        var record = Record();
        if (settings.ScreenMode == HydroShift2CurveSettings.ScreenVideo && settings.Video is { } video && _media.Exists(video))
        {
            await PlayAsync(video, loop: true, saverBacklight: null, record,
                s => s.ScreenMode == HydroShift2CurveSettings.ScreenVideo && s.Video == video, ct).ConfigureAwait(false);
            return;
        }
        if (_saverMinutes > 0 && Environment.TickCount64 >= _nextSaverAt)
        {
            ScheduleSaver(_saverMinutes, force: true);
            // A Windows monitor on the glass would be torn down and rebuilt every interval.
            if (settings.ScreenSaverVideo is { } saver && _media.Exists(saver) && record?.SecondaryMonitor != true)
            {
                await PlayAsync(saver, loop: false, saverBacklight: settings.ScreenSaverBrightness, record,
                    s => s.ScreenMode == HydroShift2CurveSettings.ScreenNexus && s.ScreenSaverMinutes > 0 && s.ScreenSaverVideo == saver, ct).ConfigureAwait(false);
                ScheduleSaver(_saverMinutes, force: true);
            }
        }
    }

    /// <summary>During a play it is tried once, so a glass that leaves it unanswered never stalls the stream; the tick retries after the play.</summary>
    private void ApplyOfflineClock(HydroShift2CurveSettings settings, bool playing = false)
    {
        if (settings.OfflineClock is not { } clock || _offlineClockSent == clock || (playing && _offlineClockTriedInPlay == clock))
        {
            return;
        }
        if (playing)
        {
            _offlineClockTriedInPlay = clock;
        }
        if (_hub.Exchange(pipe => _driver.SetOfflineClock(pipe, clock), false))
        {
            _offlineClockSent = clock;
        }
    }

    /// <summary>Whether the play should go on; settings that act during a play apply here.</summary>
    private bool StillWanted(Func<HydroShift2CurveSettings, bool> settingsAllow, string? recordId, (bool, bool) mount)
    {
        if (!_hub.IsConnected)
        {
            return false;
        }
        var settings = _store.Load().Devices.HydroShift2Curve;
        if (!settingsAllow(settings))
        {
            return false;
        }
        ApplyOfflineClock(settings, playing: true);
        if (_saverBacklight is { } applied && applied != settings.ScreenSaverBrightness)
        {
            _saverBacklight = settings.ScreenSaverBrightness;
            _hub.Exchange(pipe => _driver.SetBacklight(pipe, settings.ScreenSaverBrightness), false);
        }
        return recordId is null || MountOf(_registry.Get(recordId)) == mount;
    }

    private void ScheduleSaver(int minutes, bool force)
    {
        if (!force && minutes == _saverMinutes)
        {
            return;
        }
        _saverMinutes = minutes;
        _nextSaverAt = Environment.TickCount64 + (Math.Max(minutes, 0) * 60_000L);
    }

    private async Task PlayAsync(
        string name, bool loop, int? saverBacklight, PanelDeviceRecord? record, Func<HydroShift2CurveSettings, bool> settingsAllow, CancellationToken ct)
    {
        var mount = MountOf(record);
        bool StillWanted() => this.StillWanted(settingsAllow, record?.Id, mount);
        _offlineClockTriedInPlay = null;
        var attemptStartedAt = Environment.TickCount64;

        var path = await _media.EnsureVariantAsync(name, mount.Flip180, mount.Mirror, ct).ConfigureAwait(false);
        if (path is null)
        {
            BackOff(attemptStartedAt);
            return;
        }
        if (!StillWanted())
        {
            return;
        }

        // _stopped before _playing: a delete that sees the name must wait on this play.
        Volatile.Write(ref _stopped, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _driver.VideoOwnsGlass = true;
        _ownsGlass = true;
        _playing = name;
        RaiseGlassOwnerChanged();
        ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] playing '{name}'{(loop ? " in a loop" : " once")}");
        bool accepted = false;
        bool refused = false;
        try
        {
            int block = _hub.Exchange(pipe => _driver.BeginVideo(pipe, FrameRate), 0);
            if (block <= 0)
            {
                refused = true;
                return;
            }
            if (saverBacklight is { } backlight)
            {
                _saverBacklight = backlight;
                _hub.Exchange(pipe => _driver.SetBacklight(pipe, backlight), false);
            }

            var session = (uint)Environment.TickCount;
            var buffer = new byte[block];
            var started = Environment.TickCount64;
            using (var file = File.OpenRead(path))
            {
                while (StillWanted())
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
                        while (buffered > HydroShift2CurveProtocol.H264BufferLow && StillWanted())
                        {
                            await Task.Delay(BufferPollMs, ct).ConfigureAwait(false);
                            buffered = _hub.Exchange(pipe => _driver.QueryVideoBuffer(pipe), (int?)null);
                        }
                    }
                    if (buffered is null)
                    {
                        refused = !accepted;
                        break;
                    }
                    accepted = true;
                }
            }

            if (!loop && accepted && _media.Duration(name) is { } seconds)
            {
                // The decoder plays on after the last chunk; hold the glass until the clip ends.
                var end = started + (long)(seconds * 1000);
                while (Environment.TickCount64 < end && StillWanted())
                {
                    await Task.Delay(TickMs, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Release();
            if (refused)
            {
                ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] the glass took none of '{name}'; retrying in {RetryAfterFailMs / 1000} s");
                BackOff(attemptStartedAt);
            }
        }
    }

    private void Release()
    {
        if (!_ownsGlass)
        {
            return;
        }
        var restoreBacklight = _saverBacklight is not null;
        _hub.Exchange(pipe =>
        {
            _driver.EndVideo(pipe);
            if (restoreBacklight)
            {
                _driver.SetBacklight(pipe, _driver.Brightness);
            }
            return true;
        }, false);
        _saverBacklight = null;
        _offlineClockTriedInPlay = null;
        _driver.VideoOwnsGlass = false;
        _ownsGlass = false;
        _playing = null;
        Volatile.Read(ref _stopped).TrySetResult();
        ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] video stopped, panel back on the glass");
        RaiseGlassOwnerChanged();
    }

    private static TaskCompletionSource Stopped()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
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
