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
/// Pushes the schedule cap in <see cref="ScheduleFollowState"/> to every device that follows it: the Y70
/// through its provider, everything else through <see cref="IScheduleSurface"/>. The timer tick spaces writes
/// per device; a relevant settings change writes at once. The Corsair LCD worker reads its cap on its own loop.
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
    // Keys whose last write failed; they stay unsettled until a write lands.
    private readonly HashSet<string> _retry = new(StringComparer.Ordinal);
    // Followed keys whose slider was taken from the hardware (or trusted at startup); an opt-in seeds it afresh.
    private readonly HashSet<string> _seeded = new(StringComparer.Ordinal);
    private bool _seedInit;
    private int _pendingTick;
    private int _lastSignature;

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
            if (userTriggered)
            {
                Volatile.Write(ref _pendingTick, 0);
            }
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
        // Only the inputs the follow reads start a tick; at most one is pending.
        var signature = InputSignature(_store.Load());
        if (signature == _lastSignature)
        {
            return;
        }
        _lastSignature = signature;
        if (Interlocked.Exchange(ref _pendingTick, 1) == 1)
        {
            return;
        }
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

    private static int InputSignature(NexusSettings settings)
    {
        var hash = new HashCode();
        var schedule = settings.Lighting.BrightnessSchedule;
        hash.Add(schedule?.Enabled);
        hash.Add(schedule?.LightingFollows);
        hash.Add(schedule?.SunShift);
        hash.Add(schedule?.SunNight);
        hash.Add(schedule?.SunDay);
        hash.Add(schedule?.SunLocation?.GetHashCode());
        foreach (var point in schedule?.Points ?? new())
        {
            hash.Add(point.Hour);
            hash.Add(point.Brightness);
        }
        foreach (var key in settings.Devices.ScheduleFollowing)
        {
            hash.Add(key, StringComparer.Ordinal);
        }
        foreach (var (key, value) in settings.Devices.DisplaySliderBrightness.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value);
        }
        hash.Add(settings.Y70.Brightness);
        return hash.ToHashCode();
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
        var moving = _retry.Count > 0 || _written.Values.Any(v => v is not null);
        if (!moving && (!scheduleOn || settings.Devices.ScheduleFollowing.Count == 0))
        {
            return;
        }
        if (!_seedInit)
        {
            _seedInit = true;
            _seeded.UnionWith(settings.Devices.ScheduleFollowing);
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
        // A device that left has nothing to restore and must not keep the tick alive.
        foreach (var gone in _written.Keys.Concat(_retry).Where(k => !_present.ContainsKey(k)).ToList())
        {
            _written.Remove(gone);
            _writtenAt.Remove(gone);
            _retry.Remove(gone);
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
        var follows = _state.Follows(settings, key);
        if (!follows)
        {
            _seeded.Remove(key);
        }
        if (written is null && cap >= 1.0 && !follows)
        {
            _retry.Remove(key);
            return;
        }
        var slider = surface.Slider(key);
        // Opting in re-reads the hardware, which is still the user's level, so a change made on the
        // device itself is not lost; with no remembered slider it is taken once.
        var seed = follows && written is null && _seeded.Add(key);
        if (slider is null || seed)
        {
            if (await surface.ReadHardwareAsync(key, ct).ConfigureAwait(false) is { } hardware)
            {
                surface.RememberSlider(key, hardware);
                slider = hardware;
            }
            else if (slider is null)
            {
                return;
            }
        }
        var settled = cap >= 1.0;
        var shown = ScheduleFollowMath.Scale(slider.Value, cap);
        var gapOpen = userTriggered || nowMs - _writtenAt.GetValueOrDefault(key, long.MinValue / 2) >= surface.MinWriteGapMs;
        if (shown == (written ?? slider.Value))
        {
            if (shown == slider.Value && settled)
            {
                _written[key] = null;
            }
            return;
        }
        if (!gapOpen)
        {
            return;
        }
        var ok = await surface.WriteAsync(key, shown, ct).ConfigureAwait(false);
        _writtenAt[key] = nowMs;
        if (!ok)
        {
            _retry.Add(key);
            return;
        }
        _retry.Remove(key);
        _written[key] = shown == slider.Value && settled ? null : shown;
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
