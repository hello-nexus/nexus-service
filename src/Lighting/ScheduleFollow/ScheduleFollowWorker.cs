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
    // Keys whose last write failed; they stay unsettled until a write lands. Repeated failures back the retry off.
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private const long MaxRetryGapMs = 60_000;
    // A device that keeps failing at the longest gap is parked: retried once a minute, or at once when its target changes.
    // Parked keys do not keep the tick going; the slow wake covers them.
    private const int ParkAfterFailures = 10;
    private const long ParkRetryMs = 60_000;
    private readonly Dictionary<string, (long AtMs, int Shown)> _parked = new(StringComparer.Ordinal);
    // Keys that left while off their slider (kept in the state): the level is unknown on return, so one write is forced then.
    // They do not keep the tick going; while any exist, presence is looked at on a slow cadence only.
    private const long SightCheckGapMs = 5_000;
    private long _lastSightCheckMs = long.MinValue / 2;
    // Failed hardware reads for a seed, per key, and when the last was tried; a few misses and the stored slider stands.
    private readonly Dictionary<string, (int Tries, long AtMs)> _seedMisses = new(StringComparer.Ordinal);
    private const int MaxSeedMisses = 3;
    // Followed keys whose slider was taken from the hardware (or trusted at startup); an opt-in seeds it afresh.
    private readonly HashSet<string> _seeded = new(StringComparer.Ordinal);
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
        // Devices already followed at startup keep their remembered slider; the hardware may still hold a capped level.
        _seeded.UnionWith(_store.Load().Devices.ScheduleFollowing);
        _lastSignature = InputSignatureOf(_store);
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
                foreach (var key in _parked.Keys)
                {
                    _failures.Remove(key);
                }
                _parked.Clear();
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

    private static int InputSignatureOf(IConfigStore store)
    {
        var signature = 0;
        store.Read(s => signature = InputSignature(s));
        return signature;
    }

    private void OnStoreChanged()
    {
        // Never throws: a throwing handler would skip every later OnChanged subscriber.
        try
        {
            // Only the inputs the follow reads start a tick; at most one is pending.
            var signature = InputSignatureOf(_store);
            if (Interlocked.Exchange(ref _lastSignature, signature) == signature)
            {
                return;
            }
            if (Interlocked.Exchange(ref _pendingTick, 1) == 1)
            {
                return;
            }
            StartSettingsTick();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[schedule-follow] settings change ignored: {ex.Message}");
        }
    }

    private void StartSettingsTick()
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
        var moving = _failures.Keys.Any(k => !_parked.ContainsKey(k)) || _written.Any(p => p.Value is not null && !_parked.ContainsKey(p.Key));
        if (!moving && (!scheduleOn || settings.Devices.ScheduleFollowing.Count == 0))
        {
            var lookForReturns = (_state.RestoreOnSight.Count > 0 || _parked.Count > 0) && (userTriggered || nowMs - _lastSightCheckMs >= SightCheckGapMs);
            if (!lookForReturns)
            {
                return;
            }
            _lastSightCheckMs = nowMs;
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
        // A device that left must not keep the tick alive, but one that left off its slider is written once when it returns.
        foreach (var gone in _written.Keys.Concat(_failures.Keys).Where(k => !_present.ContainsKey(k)).ToList())
        {
            if (_written.GetValueOrDefault(gone) is not null || _failures.ContainsKey(gone))
            {
                _state.RequireRestore(gone);
            }
            _written.Remove(gone);
            _writtenAt.Remove(gone);
            _failures.Remove(gone);
            _parked.Remove(gone);
        }
        foreach (var (key, surface) in _present)
        {
            await TickSurfaceKeyAsync(settings, key, surface, nowMs, userTriggered, ct).ConfigureAwait(false);
        }
    }

    private async Task TickSurfaceKeyAsync(NexusSettings settings, string key, IScheduleSurface surface, long nowMs, bool userTriggered, CancellationToken ct)
    {
        var cap = _state.Target(settings, key);
        if (_parked.TryGetValue(key, out var park))
        {
            var target = surface.Slider(key) is { } parkedSlider ? ScheduleFollowMath.Scale(parkedSlider, cap) : -1;
            if (target == park.Shown && nowMs - park.AtMs < ParkRetryMs)
            {
                return;
            }
            _parked.Remove(key);
            if (target != park.Shown)
            {
                _failures.Remove(key);
            }
            else
            {
                _failures[key] = ParkAfterFailures - 1;
            }
        }
        _written.TryGetValue(key, out var written);
        // A device the user never opted in is not touched at all, not even to read it.
        var follows = _state.Follows(settings, key);
        if (!follows)
        {
            _seeded.Remove(key);
        }
        var force = _state.NeedsRestore(key);
        if (written is null && cap >= 1.0 && !follows && !force)
        {
            _failures.Remove(key);
            return;
        }
        var slider = surface.Slider(key);
        // Opting in re-reads the hardware, which is still the user's level, so a change made on the
        // device itself is not lost; with no remembered slider it is taken once. A device back from an
        // absence may still hold a capped level, so it is never read as the slider.
        var seed = follows && written is null && !force && !_seeded.Contains(key) && SeedDue(key, surface, nowMs);
        if (slider is null || seed)
        {
            if (await surface.ReadHardwareAsync(key, ct).ConfigureAwait(false) is { } hardware)
            {
                surface.RememberSlider(key, hardware);
                slider = hardware;
                _seeded.Add(key);
                _seedMisses.Remove(key);
            }
            else
            {
                NoteSeedMiss(key, nowMs);
                if (slider is null)
                {
                    return;
                }
            }
        }
        var settled = cap >= 1.0;
        var shown = ScheduleFollowMath.Scale(slider.Value, cap);
        // Each failed write doubles the gap, up to a minute; a settings change skips the gap.
        var ceiling = Math.Max(MaxRetryGapMs, surface.MinWriteGapMs);
        var gap = surface.MinWriteGapMs;
        for (var i = _failures.GetValueOrDefault(key); i > 0 && gap < ceiling; i--)
        {
            gap = Math.Min(gap * 2, ceiling);
        }
        var gapOpen = userTriggered || nowMs - _writtenAt.GetValueOrDefault(key, long.MinValue / 2) >= gap;
        if (shown == (written ?? slider.Value) && !force)
        {
            _failures.Remove(key);
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
            var failed = _failures.GetValueOrDefault(key) + 1;
            _failures[key] = failed;
            if (failed >= ParkAfterFailures)
            {
                _parked[key] = (nowMs, shown);
            }
            return;
        }
        _failures.Remove(key);
        _state.RestoreDone(key);
        _written[key] = shown == slider.Value && settled ? null : shown;
    }

    private bool SeedDue(string key, IScheduleSurface surface, long nowMs) =>
        !_seedMisses.TryGetValue(key, out var miss) || (miss.Tries < MaxSeedMisses && nowMs - miss.AtMs >= surface.MinWriteGapMs);

    // After a few unreadable attempts the stored slider stands, so a surface that cannot be read is not polled forever.
    private void NoteSeedMiss(string key, long nowMs)
    {
        var tries = _seedMisses.GetValueOrDefault(key).Tries + 1;
        _seedMisses[key] = (tries, nowMs);
        if (tries >= MaxSeedMisses)
        {
            _seeded.Add(key);
            _seedMisses.Remove(key);
        }
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
