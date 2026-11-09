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
/// The one tick behind every device that follows the brightness schedule. Each second it reads
/// the caps in <see cref="ScheduleFollowState"/> and pushes the changed levels:
/// the Y70 through its provider, DDC monitors through the display controller (whole-percent
/// changes only, spaced out to spare slow monitors and their NVRAM), and the other surfaces through <see cref="IScheduleSurface"/>. The Corsair LCD worker
/// reads its cap on its own loop. Idle (schedule off, nothing ramping) it only reads settings.
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
        _stop = stoppingToken;
        _store.OnChanged += OnStoreChanged;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(Environment.TickCount64, userTriggered: false, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ServiceLog.Warn($"[schedule-follow] tick failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private CancellationToken _stop;

    /// <summary><paramref name="userTriggered"/> is a settings change: its writes skip the spacing, so an opt-in, a schedule switch or a
    /// curve edit lands at once. Only the minute-by-minute curve drift of the timer tick keeps the gap.</summary>
    internal async Task TickAsync(long nowMs, bool userTriggered, CancellationToken ct)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _store.Load();
            var scheduleOn = settings.Lighting.BrightnessSchedule is { Enabled: true };
            TickY70(settings);
            await TickSurfacesAsync(settings, scheduleOn, nowMs, userTriggered, ct).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>A settings write (schedule on or off, curve, a follow switch) is acted on at once, not at the next tick.</summary>
    internal Task OnSettingsChangedAsync() => TickAsync(Environment.TickCount64, userTriggered: true, _stop);

    private void OnStoreChanged()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await OnSettingsChangedAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceLog.Warn($"[schedule-follow] settings tick failed: {ex.Message}");
            }
        });
    }

    public override void Dispose()
    {
        _store.OnChanged -= OnStoreChanged;
        base.Dispose();
    }

    private void TickY70(NexusSettings settings)
    {
        var slider = settings.Y70.Brightness;
        var cap = _state.Target(settings, ScheduleFollowKeys.Y70);
        if (_y70Written is null && cap >= 1.0)
        {
            return;
        }
        var shown = ScheduleFollowMath.Scale(slider, cap);
        if (shown == (_y70Written ?? slider) || !_y70.IsConnected())
        {
            return;
        }
        _y70.ReapplyBrightness();
        _y70Written = cap >= 1.0 ? null : shown;
    }

    private async Task TickSurfacesAsync(NexusSettings settings, bool scheduleOn, long nowMs, bool userTriggered, CancellationToken ct)
    {
        // A device still off its slider keeps getting ticks until it lands.
        var moving = _written.Values.Any(v => v is not null);
        if (!moving && (!scheduleOn || settings.Devices.ScheduleFollowing.Count == 0))
        {
            return;
        }
        _present.Clear();
        foreach (var surface in _surfaces)
        {
            foreach (var key in surface.PresentKeys())
            {
                _state.Track(key);
                _present[key] = surface;
            }
        }
        foreach (var (key, surface) in _present)
        {
            await TickSurfaceKeyAsync(settings, key, surface, nowMs, userTriggered, ct).ConfigureAwait(false);
        }
    }

    private async Task TickSurfaceKeyAsync(NexusSettings settings, string key, IScheduleSurface surface, long nowMs, bool userTriggered, CancellationToken ct)
    {
        var cap = _state.Target(settings, key);
        _written.TryGetValue(key, out var written);
        // A device the user never opted in is not touched at all, not even to read it.
        if (written is null && cap >= 1.0 && !_state.Follows(settings, key))
        {
            return;
        }
        var slider = surface.Slider(key);
        if (slider is null)
        {
            // No remembered slider: the hardware value is the user's, taken once.
            if (await surface.ReadHardwareAsync(key, ct).ConfigureAwait(false) is not { } hardware)
            {
                return;
            }
            surface.RememberSlider(key, hardware);
            slider = hardware;
        }
        var settled = cap >= 1.0;
        var shown = ScheduleFollowMath.Scale(slider.Value, cap);
        var gapOpen = userTriggered || nowMs - _writtenAt.GetValueOrDefault(key, long.MinValue / 2) >= surface.MinWriteGapMs;
        if (shown == (written ?? slider.Value) || !gapOpen)
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
}

/// <summary>A device family the follow worker writes to. Keys are the stable ids the web binds to.</summary>
public interface IScheduleSurface
{
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
