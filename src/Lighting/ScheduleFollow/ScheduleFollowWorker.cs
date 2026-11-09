using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.ScheduleFollow;

/// <summary>
/// The one tick behind every device that follows the brightness schedule. Each second it advances
/// the ramped multipliers in <see cref="ScheduleFollowState"/> and pushes the changed levels:
/// the Y70 through its provider, DDC monitors through the display controller (whole-percent
/// changes only, spaced out to spare slow monitors and their NVRAM), and the other surfaces through <see cref="IScheduleSurface"/>. The Corsair LCD worker
/// reads its multiplier on its own loop. Idle (schedule off, nothing ramping) it only reads settings.
/// </summary>
public sealed class ScheduleFollowWorker : BackgroundService
{
    /// <summary>Gap between writes to one DDC monitor.</summary>
    public const long DdcMinWriteGapMs = 4000;

    private readonly ScheduleFollowState _state;
    private readonly IConfigStore _store;
    private readonly IY70Provider _y70;
    private readonly IReadOnlyList<IScheduleSurface> _surfaces;

    private int? _y70Written;
    private readonly Dictionary<string, IScheduleSurface> _present = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _written = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _writtenAt = new(StringComparer.Ordinal);

    public ScheduleFollowWorker(ScheduleFollowState state, IConfigStore store, IY70Provider y70, IEnumerable<IScheduleSurface> surfaces)
    {
        _state = state;
        _store = store;
        _y70 = y70;
        _surfaces = surfaces.ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var last = Environment.TickCount64;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var now = Environment.TickCount64;
                try
                {
                    await TickAsync((now - last) / 1000.0, now, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ServiceLog.Warn($"[schedule-follow] tick failed: {ex.Message}");
                }
                last = now;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task TickAsync(double elapsedSeconds, long nowMs, CancellationToken ct)
    {
        var settings = _store.Load();
        var scheduleOn = settings.Lighting.BrightnessSchedule is { Enabled: true };
        _state.Advance(elapsedSeconds);
        TickY70(settings);
        await TickSurfacesAsync(scheduleOn, nowMs, ct).ConfigureAwait(false);
    }

    private void TickY70(NexusSettings settings)
    {
        var slider = settings.Y70.Brightness;
        var shown = _state.Scale(ScheduleFollowKeys.Y70, slider);
        if (_y70Written is null && _state.Multiplier(ScheduleFollowKeys.Y70) >= 1.0)
        {
            return;
        }
        if (shown == (_y70Written ?? slider) || !_y70.IsConnected())
        {
            return;
        }
        _y70.ReapplyBrightness();
        _y70Written = _state.Multiplier(ScheduleFollowKeys.Y70) >= 1.0 ? null : shown;
    }

    private async Task TickSurfacesAsync(bool scheduleOn, long nowMs, CancellationToken ct)
    {
        // A device still off its slider (a multiplier below 1, or a last write that is not the plain level yet) keeps getting ticks until it lands.
        var moving = _written.Values.Any(v => v is not null);
        foreach (var key in _state.TrackedKeys)
        {
            if (IsSurfaceKey(key) && _state.Multiplier(key) < 1.0)
            {
                moving = true;
            }
        }
        if (!scheduleOn && !moving)
        {
            return;
        }
        _present.Clear();
        foreach (var surface in _surfaces)
        {
            foreach (var key in surface.PresentKeys())
            {
                _state.Track(key, surface.RatePerSecond);
                _present[key] = surface;
            }
        }
        foreach (var (key, surface) in _present)
        {
            await TickSurfaceKeyAsync(key, surface, nowMs, ct).ConfigureAwait(false);
        }
    }

    private async Task TickSurfaceKeyAsync(string key, IScheduleSurface surface, long nowMs, CancellationToken ct)
    {
        var multiplier = _state.Multiplier(key);
        var slider = surface.Slider(key);
        _written.TryGetValue(key, out var written);
        if (slider is null || (written is null && multiplier >= 1.0 && _state.Target(_store.Load(), key) < 1.0))
        {
            // About to move this surface off its plain level: the hardware value is the user's, and may
            // have been changed on the device's own buttons since the last remembered slider.
            if (await surface.ReadHardwareAsync(key, ct).ConfigureAwait(false) is { } hardware)
            {
                if (slider != hardware)
                {
                    surface.RememberSlider(key, hardware);
                }
                slider = hardware;
            }
            else if (slider is null)
            {
                return;
            }
        }
        var settled = multiplier >= 1.0;
        var shown = _state.Scale(key, slider.Value);
        if (shown == (written ?? slider.Value) || nowMs - _writtenAt.GetValueOrDefault(key, long.MinValue / 2) < surface.MinWriteGapMs)
        {
            if (shown == slider.Value && settled)
            {
                _written[key] = null;
            }
            return;
        }
        var ok = await surface.WriteAsync(key, shown, ct).ConfigureAwait(false);
        _writtenAt[key] = nowMs;
        _written[key] = ok && !(shown == slider.Value && settled) ? shown : null;
    }

    private static bool IsSurfaceKey(string key) =>
        key != ScheduleFollowKeys.Y70 && key != ScheduleFollowKeys.CorsairLcd && !key.StartsWith(ScheduleFollowKeys.Slv3AioPrefix, StringComparison.Ordinal);
}

/// <summary>A device family the follow worker writes to. Keys are the stable ids the web binds to.</summary>
public interface IScheduleSurface
{
    /// <summary>Per-second multiplier change for this family.</summary>
    double RatePerSecond { get; }

    /// <summary>Smallest gap between two writes to one device.</summary>
    long MinWriteGapMs { get; }

    /// <summary>Keys of the devices present now and able to take a level.</summary>
    IReadOnlyList<string> PresentKeys();

    /// <summary>The user's remembered slider, or null.</summary>
    int? Slider(string key);

    void RememberSlider(string key, int percent);

    /// <summary>The level the hardware reports, or null when it cannot be read.</summary>
    Task<int?> ReadHardwareAsync(string key, CancellationToken ct);

    /// <summary>Puts a level on the device without touching the remembered slider.</summary>
    Task<bool> WriteAsync(string key, int level, CancellationToken ct);
}
