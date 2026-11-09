using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Lighting.ScheduleFollow;

/// <summary>
/// The one tick behind every device that follows the brightness schedule. Each second it advances
/// the ramped multipliers in <see cref="ScheduleFollowState"/> and pushes the changed levels:
/// the Y70 through its provider, DDC monitors through the display controller (whole-percent
/// changes only, spaced out to spare slow monitors and their NVRAM). The Corsair LCD worker
/// reads its multiplier on its own loop. Idle (schedule off, nothing ramping) it only reads settings.
/// </summary>
public sealed class ScheduleFollowWorker : BackgroundService
{
    /// <summary>Gap between writes to one DDC monitor.</summary>
    public const long DdcMinWriteGapMs = 4000;

    /// <summary>How often the monitor list is refreshed while following.</summary>
    public const long DisplayScanGapMs = 60_000;

    private readonly ScheduleFollowState _state;
    private readonly IConfigStore _store;
    private readonly IY70Provider _y70;
    private readonly DisplayBrightnessController _displays;

    private int? _y70Written;
    private long _lastScanMs = long.MinValue;
    private readonly HashSet<string> _present = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _written = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _writtenAt = new(StringComparer.Ordinal);

    public ScheduleFollowWorker(ScheduleFollowState state, IConfigStore store, IY70Provider y70, DisplayBrightnessController displays)
    {
        _state = state;
        _store = store;
        _y70 = y70;
        _displays = displays;
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
        await TickDisplaysAsync(scheduleOn, nowMs, ct).ConfigureAwait(false);
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

    private async Task TickDisplaysAsync(bool scheduleOn, long nowMs, CancellationToken ct)
    {
        var moving = false;
        foreach (var key in _state.TrackedKeys)
        {
            if (IsDisplayKey(key) && _state.Multiplier(key) < 1.0)
            {
                moving = true;
            }
        }
        if (!scheduleOn && !moving)
        {
            return;
        }
        if (nowMs - _lastScanMs >= DisplayScanGapMs)
        {
            _lastScanMs = nowMs;
            ScanDisplays();
        }
        foreach (var id in _present)
        {
            await TickDisplayAsync(id, nowMs, ct).ConfigureAwait(false);
        }
    }

    private void ScanDisplays()
    {
        _present.Clear();
        foreach (var display in _displays.ListDisplays().Displays)
        {
            if (display.Capabilities.Brightness
                && display.BrightnessControl.Supported
                && display.BrightnessControl.ControlPath != DisplayBrightnessControlPaths.Y70
                && display.Id.Length > 0)
            {
                _state.Track(display.Id, ScheduleFollowMath.SlowRatePerSecond);
                _present.Add(display.Id);
            }
        }
    }

    private async Task TickDisplayAsync(string id, long nowMs, CancellationToken ct)
    {
        var multiplier = _state.Multiplier(id);
        var slider = _displays.GetSlider(id);
        _written.TryGetValue(id, out var written);
        if (slider is null || (written is null && multiplier >= 1.0 && _state.Target(_store.Load(), id) < 1.0))
        {
            // About to move this monitor off its plain level: the hardware value is the user's, and may
            // have been changed on the monitor's own buttons since the last remembered slider.
            if (_displays.GetBrightness(id) is not { } hardware)
            {
                return;
            }
            if (slider != hardware)
            {
                _displays.RememberSlider(id, hardware);
            }
            slider = hardware;
        }
        var shown = _state.Scale(id, slider.Value);
        if (shown == (written ?? slider.Value) || nowMs - _writtenAt.GetValueOrDefault(id, long.MinValue / 2) < DdcMinWriteGapMs)
        {
            if (shown == slider.Value && multiplier >= 1.0)
            {
                _written[id] = null;
            }
            return;
        }
        var result = await _displays.ApplyLevelAsync(id, shown, ct).ConfigureAwait(false);
        _writtenAt[id] = nowMs;
        _written[id] = result.Status == DisplayBrightnessWriteStatuses.Applied && !(shown == slider.Value && multiplier >= 1.0) ? shown : null;
    }

    private static bool IsDisplayKey(string key) =>
        key != ScheduleFollowKeys.Y70 && key != ScheduleFollowKeys.CorsairLcd;
}
