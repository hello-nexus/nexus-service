using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Nexus.Service.Diagnostics;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Monitoring.Events;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;

namespace Nexus.Service.Cooling;

/// <summary>What <see cref="CurveEngine"/> applies at its final per-channel write this tick.</summary>
public sealed class GuardPlan
{
    public ThermalGuardOutput Output { get; init; }
    /// <summary>CPU-side channels the guard may write: Nexus-driven, controlled, unlocked, not GPU.</summary>
    public HashSet<string> Eligible { get; init; } = new(StringComparer.Ordinal);
    /// <summary>GPU fans handed back to the driver: no writes.</summary>
    public HashSet<string> GpuBlocked { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> GpuForced { get; init; } = new(StringComparer.Ordinal);
    public List<string> GpuRelease { get; init; } = new();
    public List<string> GpuResume { get; init; } = new();
    /// <summary>Manual duties to persist before the engine releases the GPU fans.</summary>
    public Dictionary<string, int> GpuBackup { get; init; } = new(StringComparer.Ordinal);

    public static readonly GuardPlan None = new() { Output = new ThermalGuardOutput(ThermalGuardStates.Off, 0, false, false, false, false, false, null) };

    public bool Overrides => Output.FloorDuty > 0 || Output.StopWriting || GpuBlocked.Count > 0 || GpuForced.Count > 0;

    /// <summary>Final duty for a channel, or null when the guard forbids writing it.</summary>
    public int? Apply(string channelId, int duty)
    {
        if (GpuBlocked.Contains(channelId))
        {
            return null;
        }
        if (GpuForced.Contains(channelId))
        {
            return 100;
        }
        if (!Eligible.Contains(channelId))
        {
            return duty;
        }
        if (Output.StopWriting)
        {
            return null;
        }
        return Math.Max(duty, Output.FloorDuty);
    }
}

/// <summary>
/// Runtime side of the CPU thermal guard: gathers inputs, runs the pure state
/// machines, and owns the side effects of a trip (persisted record, broadcast,
/// alert, timeline event, auto-heal). Writing fans stays with CurveEngine.
/// </summary>
public sealed class ThermalGuardController
{
    private const long CpuInfoRefreshMs = 5000;
    private const long WatchdogHotStallMs = 10_000;
    private const long WatchdogStallMs = 30_000;
    // Stalls this many times within the window mean the engine cannot be trusted with the fans.
    private const int WatchdogLatchFires = 3;
    private const long WatchdogLatchWindowMs = 10 * 60_000;
    private const long CpuLimitRefreshMs = 60_000;
    private const long TachZeroSustainMs = 60_000;
    private const int TachConsecutiveTicks = 3;
    private const int TachMaxRpm = 10_000;
    private const long WatchdogPollMs = 2000;
    private const long TripAlertCooldownMs = 30 * 60_000;

    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly MultiplexHub? _hub;
    private readonly ISensorProvider? _sensors;
    private readonly IMonitoringEventStore? _events;
    private readonly DiagnosticsAlertService? _alerts;
    private readonly Func<long> _utcNowMs;

    private readonly object _gate = new();
    private readonly ThermalGuard _cpu;
    private readonly Dictionary<string, GpuThermalGuard> _gpus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, double? Temp, double? Limit, string? Source)> _gpuInfo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RpmTrack> _rpm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastTripAlertMs = new(StringComparer.Ordinal);
    private readonly ThermalGuardThresholds _thresholds;

    private string _publicState = ThermalGuardStates.Inactive;
    private double? _guardTemp;
    // What was detected, and the effective limit (the user's override applied when allowed).
    private ThermalLimit _detected = new(ThermalLimits.GenericDefaultC, ThermalLimitSources.Default);
    private ThermalLimit _limit = new(ThermalLimits.GenericDefaultC, ThermalLimitSources.Default);
    private double? _loggedOverrideC;
    private long? _sinceUtcMs;
    private long? _cpuInfoAtMs;
    private double? _cpuLoad;
    private long _lastTickMs;
    private readonly List<long> _watchdogFires = new();
    private bool _watchdogLatched;
    private string _cpuModel = "";
    private long? _cpuLimitAtMs;
    private bool _cpuInfoFailureLogged;
    private string _loggedLimitKey = "";
    private bool _watchdogArmed;
    private bool _watchdogFired;
    private bool _watchdogPending;
    private Timer? _watchdog;

    public ThermalGuardController(
        IFanControlProvider fans,
        IConfigStore store,
        MultiplexHub? hub = null,
        ISensorProvider? sensors = null,
        IMonitoringEventStore? events = null,
        DiagnosticsAlertService? alerts = null,
        Func<long>? utcNowMs = null,
        ThermalGuardThresholds? thresholds = null)
    {
        _fans = fans;
        _store = store;
        _hub = hub;
        _sensors = sensors;
        _events = events;
        _alerts = alerts;
        _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _thresholds = thresholds ?? ThermalGuardThresholds.Default;
        _cpu = new ThermalGuard(_thresholds);
        // A trip record still open at construction belongs to a previous run that never closed it.
        CloseOpenTrip();
    }

    private sealed class RpmTrack
    {
        public int Best;
        public int Streak;
        public int StreakMin;
        public long? ZeroSinceMs;
    }

    /// <summary>Monotonic millisecond clock for the watchdog; tests substitute it.</summary>
    internal Func<long> MonotonicMs { get; set; } = () => Environment.TickCount64;

    // ── Engine hooks ──

    public void StartWatchdog()
    {
        lock (_gate) { _lastTickMs = MonotonicMs(); }
        _watchdog ??= new Timer(_ => WatchdogCheck(MonotonicMs()), null, WatchdogPollMs, WatchdogPollMs);
    }

    public void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    public void TickCompleted()
    {
        lock (_gate)
        {
            _lastTickMs = MonotonicMs();
            _watchdogFired = false;
        }
    }

    internal void WatchdogCheck(long nowMs)
    {
        var latchedNow = false;
        lock (_gate)
        {
            if (!_watchdogArmed || _watchdogFired || _watchdogLatched)
            {
                return;
            }
            var stalled = nowMs - _lastTickMs;
            var hot = _guardTemp is { } temp && temp >= _limit.LimitC - _thresholds.FloorStartBelowLimitC;
            // A stalled engine is not managing fans, so the BIOS is the safe owner whatever the temperature.
            if (stalled < (hot ? WatchdogHotStallMs : WatchdogStallMs))
            {
                return;
            }
            _watchdogFired = true;
            _watchdogPending = true;
            _watchdogFires.Add(nowMs);
            _watchdogFires.RemoveAll(t => nowMs - t > WatchdogLatchWindowMs);
            if (_watchdogFires.Count >= WatchdogLatchFires)
            {
                // Repeated stalls: stop handing fans back and forth, the BIOS keeps them.
                _watchdogLatched = true;
                latchedNow = true;
            }
        }
        Console.Error.WriteLine("[thermal-guard] curve engine stalled: releasing all fans to BIOS");
        try { _fans.ReleaseAll(); }
        catch { /* the fans are already in an unknown state; nothing more to do */ }
        if (latchedNow)
        {
            Console.Error.WriteLine("[thermal-guard] repeated engine stalls: Nexus fan writes stopped until restart or a guard toggle");
            Raise("Cooling handed to the BIOS", "Nexus handed your fans to the BIOS because the cooling engine kept stalling.");
            if (_hub is not null)
            {
                PanelTopics.BroadcastCooling(_hub);
            }
        }
    }

    /// <summary>True once after the watchdog released the fans: the next tick forgets what it wrote so every duty is written again, unchanged ones included.</summary>
    public bool ConsumeWatchdogRelease()
    {
        lock (_gate)
        {
            var pending = _watchdogPending;
            _watchdogPending = false;
            return pending;
        }
    }

    /// <summary>True after repeated engine stalls: Nexus writes no fan until restart or a guard toggle. The guard still evaluates and reports.</summary>
    public bool WatchdogLatched
    {
        get
        {
            lock (_gate) { return _watchdogLatched; }
        }
    }

    /// <summary>Called by the engine on a tick where the Cooling feature is off.</summary>
    public void NotifyCoolingOff() => Stand(ThermalGuardStates.Inactive);

    /// <summary>Called by the engine on a tick with nothing configured to drive.</summary>
    public void NotifyIdle() => Stand(ThermalGuardStates.Inactive);

    // The guard stops acting: reset the machine, publish the state, and close any open trip record.
    private void Stand(string state)
    {
        lock (_gate)
        {
            _watchdogArmed = false;
            // Leaving a latch means the fans were released: the next tick must write everything again.
            _watchdogPending |= _watchdogLatched;
            _watchdogLatched = false;
            _watchdogFires.Clear();
            _cpu.Reset();
            _guardTemp = null;
            SetPublicState(state, null);
        }
        CloseOpenTrip();
    }

    private void CloseOpenTrip()
    {
        if (_store.Load().Cooling.LastThermalTrip is not { EndedAtUtcMs: null })
        {
            return;
        }
        var now = _utcNowMs();
        _store.Update(s =>
        {
            if (s.Cooling.LastThermalTrip is { EndedAtUtcMs: null } trip)
            {
                trip.EndedAtUtcMs = now;
            }
        });
    }

    public GuardPlan Evaluate(
        long nowMs,
        NexusSettings settings,
        IReadOnlyList<CurveDocument> curves,
        IReadOnlyDictionary<string, int> manual,
        IReadOnlyList<FanChannel> channels,
        IReadOnlyList<TemperatureSource> sources)
    {
        var cooling = settings.Cooling;
        if (!cooling.ThermalGuardEnabled)
        {
            lock (_gate)
            {
                _gpus.Clear();
                _gpuInfo.Clear();
            }
            Stand(ThermalGuardStates.Off);
            var backup = CoolingSnapshots.GpuManualBackup(cooling);
            return backup.Count > 0
                ? new GuardPlan { Output = GuardPlan.None.Output, GpuResume = backup.Keys.ToList() }
                : GuardPlan.None;
        }

        RefreshCpuInfo(nowMs);
        ApplyLimitOverride(cooling.ThermalGuardLimitOverrideC);

        var guardTemp = ThermalLimits.MaxPlausible(
            sources.Where(s => s.Category == "CPU" && s.DeviceId is null && !ThermalLimits.IsDistanceToTjMax(s.Name))
                .Select(s => (double)s.Value));
        var nexusDriven = NexusDrivenIds(curves, manual);
        var roles = CoolingSnapshots.FanRoles(cooling);
        var uncontrolled = CoolingSnapshots.Uncontrolled(cooling);

        // Pumps run at a steady speed by design; only fans say whether air is moving.
        double? maxCpuDuty = null;
        foreach (var ch in channels)
        {
            if (ch.Kind != FanKinds.Pump && CoolingConfigLint.IsCpuCooling(ch, roles))
            {
                maxCpuDuty = Math.Max(maxCpuDuty ?? 0, ch.DutyPercent);
            }
        }

        var eligible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ch in channels)
        {
            if (nexusDriven.Contains(ch.Id) && IsGuardWritable(ch, uncontrolled))
            {
                eligible.Add(ch.Id);
            }
        }
        var writesNotLanding = WritesNotLanding(nowMs, channels, eligible);

        ThermalGuardOutput output;
        var plan = new GuardPlan();
        lock (_gate)
        {
            _watchdogArmed = true;
            _guardTemp = guardTemp;
            output = _cpu.Step(nowMs, guardTemp, _limit.LimitC, _cpuLoad, maxCpuDuty, writesNotLanding);
            SetPublicState(output.State, output.TripStarted ? _utcNowMs() : null);
            plan = BuildGpuPlan(nowMs, cooling, uncontrolled, manual, channels, sources, nexusDriven, output, eligible);
        }

        if (plan.GpuBackup.Count > 0)
        {
            var persist = plan.GpuBackup.ToList();
            _store.Update(st =>
            {
                foreach (var kv in persist)
                {
                    st.Cooling.GpuManualBackup[kv.Key] = kv.Value;
                }
            });
        }
        HandleTransitions(output, settings);
        return plan;
    }

    /// <summary>
    /// True when at least one guarded fan (pumps excluded: they run steady by design) has a
    /// known best RPM and every such fan reads far below it. A zero reading counts only once
    /// it has lasted a sustained spell, so a tach that flaps to zero is not evidence; a fan
    /// with no known best says nothing about whether writes land. The best is trusted only
    /// after consecutive ticks, so a single spike never sets it.
    /// </summary>
    private bool WritesNotLanding(long nowMs, IReadOnlyList<FanChannel> channels, HashSet<string> eligible)
    {
        var qualifying = 0;
        var allLow = true;
        lock (_gate)
        {
            foreach (var ch in channels)
            {
                if (!_rpm.TryGetValue(ch.Id, out var track))
                {
                    _rpm[ch.Id] = track = new RpmTrack();
                }
                ObserveRpm(track, ch.Rpm, nowMs);
                if (!eligible.Contains(ch.Id) || ch.Kind == FanKinds.Pump)
                {
                    continue;
                }
                var best = ch.MaxRpm is > 0 ? Math.Min(ch.MaxRpm.Value, TachMaxRpm) : track.Best;
                if (best <= 0)
                {
                    continue;
                }
                qualifying++;
                var low = ch.Rpm <= 0
                    ? track.ZeroSinceMs is { } since && nowMs - since >= TachZeroSustainMs
                    : ch.Rpm < best * _thresholds.EscalateRpmFraction;
                if (!low)
                {
                    allLow = false;
                }
            }
        }
        return qualifying > 0 && allLow;
    }

    private static void ObserveRpm(RpmTrack track, int rpm, long nowMs)
    {
        if (rpm <= 0)
        {
            track.ZeroSinceMs ??= nowMs;
            track.Streak = 0;
            return;
        }
        track.ZeroSinceMs = null;
        if (rpm > TachMaxRpm || rpm <= track.Best)
        {
            track.Streak = 0;
            return;
        }
        track.StreakMin = track.Streak == 0 ? rpm : Math.Min(track.StreakMin, rpm);
        track.Streak++;
        if (track.Streak >= TachConsecutiveTicks)
        {
            track.Best = track.StreakMin;
            track.Streak = 0;
        }
    }

    // CPU-side writable: Nexus drives it and it is controlled. A preset lock does not matter
    // here (it only exempts a channel from preset applies); a GPU fan has its own guard.
    private static bool IsGuardWritable(FanChannel ch, HashSet<string> uncontrolled) =>
        !ch.IsGpu && !uncontrolled.Contains(ch.Id);

    private static HashSet<string> NexusDrivenIds(IReadOnlyList<CurveDocument> curves, IReadOnlyDictionary<string, int> manual)
    {
        var ids = new HashSet<string>(manual.Keys, StringComparer.Ordinal);
        foreach (var curve in curves)
        {
            ids.UnionWith(CoolingSnapshots.OutputIds(curve));
        }
        return ids;
    }

    private GuardPlan BuildGpuPlan(
        long nowMs,
        CoolingSettings cooling,
        HashSet<string> uncontrolled,
        IReadOnlyDictionary<string, int> manual,
        IReadOnlyList<FanChannel> channels,
        IReadOnlyList<TemperatureSource> sources,
        HashSet<string> nexusDriven,
        ThermalGuardOutput output,
        HashSet<string> eligible)
    {
        var plan = new GuardPlan { Output = output, Eligible = eligible };
        var backup = CoolingSnapshots.GpuManualBackup(cooling);
        _gpuInfo.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in channels.Where(c => c.IsGpu && c.DeviceId is not null).GroupBy(c => c.DeviceId!))
        {
            var deviceId = group.Key;
            var name = group.First().DeviceName ?? deviceId;
            var limit = GpuLimit(deviceId);
            var coreTemp = GpuCoreTemp(sources, deviceId);
            _gpuInfo[deviceId] = (name, coreTemp, limit, limit is null ? null : ThermalLimitSources.Hardware);
            seen.Add(deviceId);
            var guarded = group
                .Where(c => (nexusDriven.Contains(c.Id) || backup.ContainsKey(c.Id)) && !uncontrolled.Contains(c.Id))
                .Select(c => c.Id)
                .ToList();
            if (guarded.Count == 0)
            {
                continue;
            }
            if (limit is null)
            {
                // No threshold to defend: never strand a manual duty the guard backed up.
                _gpus.Remove(deviceId);
                plan.GpuResume.AddRange(guarded.Where(backup.ContainsKey));
                continue;
            }
            if (coreTemp is null)
            {
                // A missing reading never returns a handed-back fan to its curve.
                if (_gpus.TryGetValue(deviceId, out var held))
                {
                    if (held.State == GpuGuardStates.HandedBack)
                    {
                        plan.GpuBlocked.UnionWith(guarded);
                    }
                    else if (held.State == GpuGuardStates.Forced)
                    {
                        plan.GpuForced.UnionWith(guarded);
                    }
                }
                continue;
            }
            if (!_gpus.TryGetValue(deviceId, out var g))
            {
                _gpus[deviceId] = g = new GpuThermalGuard();
            }
            var (state, handBack, resumed) = g.Step(nowMs, coreTemp.Value, limit.Value);
            if (handBack)
            {
                plan.GpuRelease.AddRange(guarded);
                foreach (var id in guarded)
                {
                    if (manual.TryGetValue(id, out var duty))
                    {
                        plan.GpuBackup[id] = duty;
                    }
                }
                Console.Error.WriteLine($"[thermal-guard] GPU {name} at {coreTemp:0.#} C near its {limit} C slowdown threshold: fan handed back to the driver");
            }
            // A backup with a guard that is Normal is a handback a previous run never finished.
            if (resumed || (state == GpuGuardStates.Normal && !handBack && guarded.Any(backup.ContainsKey)))
            {
                plan.GpuResume.AddRange(guarded);
                Console.Error.WriteLine($"[thermal-guard] GPU {name} cooled down: Nexus fan control resumed");
            }
            if (state == GpuGuardStates.HandedBack)
            {
                plan.GpuBlocked.UnionWith(guarded);
            }
            else if (state == GpuGuardStates.Forced)
            {
                plan.GpuForced.UnionWith(guarded);
            }
        }
        foreach (var stale in _gpus.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _gpus.Remove(stale);
        }
        return plan;
    }

    // NVIDIA only: other vendors self-protect, and the NVML index is read off the device id.
    private static double? GpuLimit(string deviceId)
    {
        var nvidia = deviceId.StartsWith("/gpu-nvidia/", StringComparison.Ordinal)
            || deviceId.StartsWith("nvidia:", StringComparison.Ordinal);
        if (!nvidia)
        {
            return null;
        }
        var digits = new string(deviceId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }
        return GpuSlowdownThreshold.Get(index);
    }

    /// <summary>
    /// That GPU's own core temperature, never the hot spot, memory junction or another GPU's
    /// sensor: the slowdown threshold is a core-temperature threshold.
    /// </summary>
    internal static double? GpuCoreTemp(IReadOnlyList<TemperatureSource> sources, string deviceId)
    {
        if (deviceId.StartsWith("nvidia:", StringComparison.Ordinal))
        {
            var id = "nvidia:temp:" + deviceId["nvidia:".Length..];
            return ThermalLimits.MaxPlausible(sources.Where(s => s.Id == id).Select(s => (double)s.Value));
        }
        var prefix = deviceId + "/temperature/";
        return ThermalLimits.MaxPlausible(sources
            .Where(s => s.Category == "GPU"
                && s.Id.StartsWith(prefix, StringComparison.Ordinal)
                && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
            .Select(s => (double)s.Value));
    }

    /// <summary>The engine reports the GPU manual duties it must put back after a resume.</summary>
    public void RestoreGpuManual(IEnumerable<string> ids)
    {
        var backup = CoolingSnapshots.GpuManualBackup(_store.Load().Cooling);
        var restore = ids.Where(backup.ContainsKey).Select(id => new KeyValuePair<string, int>(id, backup[id])).ToList();
        if (restore.Count == 0)
        {
            return;
        }
        _store.Update(s =>
        {
            foreach (var kv in restore)
            {
                s.Cooling.ManualSpeeds[kv.Key] = kv.Value;
                s.Cooling.GpuManualBackup.Remove(kv.Key);
            }
        });
    }

    // Reads only what the monitoring sampler has already cached: forcing a hardware refresh
    // from the engine thread would race the sampler's own use of the hardware.
    private void RefreshCpuInfo(long nowMs)
    {
        if (_sensors is null)
        {
            return;
        }
        if (_cpuInfoAtMs is not { } infoAt || nowMs - infoAt >= CpuInfoRefreshMs)
        {
            _cpuInfoAtMs = nowMs;
            var load = TryRead(() => _sensors.GetCpuTotalLoadCached());
            lock (_gate) { _cpuLoad = load; }
        }

        // Retry quickly until the model is known, then re-resolve slowly (Intel's reported
        // limit can appear after the first sensor samples).
        var interval = _cpuModel.Length > 0 ? CpuLimitRefreshMs : CpuInfoRefreshMs;
        if (_cpuLimitAtMs is { } limitAt && nowMs - limitAt < interval)
        {
            return;
        }
        _cpuLimitAtMs = nowMs;
        var read = TryRead(() => _sensors.GetCpuModelCached());
        var model = string.IsNullOrWhiteSpace(read) ? _cpuModel : read;
        var tjMax = TryRead(() => _sensors.GetCpuTjMaxC());
        var limit = ThermalLimits.ResolveCpu(model, tjMax);
        lock (_gate)
        {
            _cpuModel = model;
            _detected = limit;
            if (_limit.Source != ThermalLimitSources.User)
            {
                _limit = limit;
            }
        }
        var key = $"{limit.LimitC}|{limit.Source}|{model}";
        if (key != _loggedLimitKey)
        {
            _loggedLimitKey = key;
            Console.Error.WriteLine($"[thermal-guard] CPU limit {limit.LimitC:0.#} C ({limit.Source}) for model \"{model}\"");
        }
    }

    /// <summary>
    /// The effective limit for an override: the user's value (clamped) when the detected limit
    /// is not read from the hardware, otherwise the detected one. Also says whether a stored
    /// override must be dropped because the hardware now reports its own.
    /// </summary>
    private (ThermalLimit Effective, bool ClearStored) EffectiveFor(double? overrideC)
    {
        if (_detected.Source == ThermalLimitSources.Hardware)
        {
            return (_detected, overrideC is not null);
        }
        if (overrideC is { } o && double.IsFinite(o))
        {
            return (new ThermalLimit(Math.Clamp(o, ThermalLimits.UserMinC, ThermalLimits.UserMaxC), ThermalLimitSources.User), false);
        }
        return (_detected, false);
    }

    private void ApplyLimitOverride(double? overrideC)
    {
        bool clear;
        lock (_gate)
        {
            var (effective, clearStored) = EffectiveFor(overrideC);
            clear = clearStored;
            _limit = effective;
            var applied = effective.Source == ThermalLimitSources.User ? (double?)effective.LimitC : null;
            if (applied != _loggedOverrideC)
            {
                _loggedOverrideC = applied;
                Console.Error.WriteLine(applied is { } v
                    ? $"[thermal-guard] CPU limit override set: {v:0.#} C"
                    : $"[thermal-guard] CPU limit override cleared, using {effective.LimitC:0.#} C ({effective.Source})");
            }
        }
        if (clear)
        {
            _store.Update(s => s.Cooling.ThermalGuardLimitOverrideC = null);
        }
    }

    private T? TryRead<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex)
        {
            if (!_cpuInfoFailureLogged)
            {
                _cpuInfoFailureLogged = true;
                Console.Error.WriteLine($"[thermal-guard] CPU sensor read failed, will retry: {ex.Message}");
            }
            return default;
        }
    }

    private void SetPublicState(string state, long? startedUtcMs)
    {
        if (_publicState == state)
        {
            return;
        }
        var previous = _publicState;
        _publicState = state;
        _sinceUtcMs = startedUtcMs ?? _utcNowMs();
        Console.Error.WriteLine($"[thermal-guard] {previous} -> {state} (temp {_guardTemp?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/a"} C, limit {_limit.LimitC:0.#} C {_limit.Source})");
        if (_hub is not null)
        {
            PanelTopics.BroadcastCooling(_hub);
        }
    }

    // ── Trip side effects ──

    private void HandleTransitions(ThermalGuardOutput output, NexusSettings settings)
    {
        if (output.TripStarted)
        {
            var peak = _cpu.PeakC;
            var reason = output.TripReason ?? ThermalTripReasons.Limit;
            var now = _utcNowMs();
            _store.Update(s => s.Cooling.LastThermalTrip = new ThermalGuardTripRecord
            {
                AtUtcMs = now,
                PeakC = peak,
                Reason = reason,
            });
            var cause = reason == ThermalTripReasons.CoolingLoss ? "cooling loss" : "temperature limit";
            var text = $"CPU reached {peak:0} C ({cause}). Fans forced to full speed.";
            if (!_lastTripAlertMs.TryGetValue(reason, out var lastAlert) || now - lastAlert >= TripAlertCooldownMs)
            {
                _lastTripAlertMs[reason] = now;
                Raise("CPU thermal guard tripped", text);
            }
            AppendTimeline(now, "CPU thermal guard", text);
        }
        if (output.ReleaseAllNow)
        {
            _store.Update(s =>
            {
                if (s.Cooling.LastThermalTrip is { } t)
                {
                    t.Escalated = true;
                    t.PeakC = Math.Max(t.PeakC, _cpu.PeakC);
                }
            });
        }
        if (output.TripEnded)
        {
            var now = _utcNowMs();
            var peak = _cpu.PeakC;
            _store.Update(s =>
            {
                if (s.Cooling.LastThermalTrip is { } t)
                {
                    t.EndedAtUtcMs = now;
                    t.PeakC = Math.Max(t.PeakC, peak);
                }
            });
            // Heal at trip end: the trip itself is handled by the override, and healing
            // while the fans are pinned means the curves resume already corrected.
            HealNow(automatic: true);
        }
    }

    /// <summary>Where trip and heal notices go. Defaults to the diagnostics alert service; tests substitute it.</summary>
    internal Action<DiagnosticsAlertNotice>? AlertSink { get; set; }

    private void Raise(string title, string text)
    {
        try
        {
            var notice = new DiagnosticsAlertNotice(title, text, "thermalGuard");
            if (AlertSink is { } sink)
            {
                sink(notice);
            }
            else
            {
                _alerts?.Raise(notice);
            }
        }
        catch { /* a subscriber failure must not affect fan control */ }
    }

    private void AppendTimeline(long nowMs, string label, string detail)
    {
        try { _events?.Append(nowMs, "thermalGuard", label, detail, custom: false); }
        catch { /* timeline is best effort */ }
    }

    // ── Heal ──

    internal LintInput BuildLintInput(
        CoolingSettings cooling,
        IReadOnlyList<FanChannel> channels,
        IReadOnlyList<TemperatureSource> sources,
        IReadOnlyList<CurveDocument> curves)
    {
        var named = channels
            .Select(c => new FanChannel
            {
                Id = c.Id,
                Name = cooling.FanNames.TryGetValue(c.Id, out var custom) ? custom : c.Name,
                Kind = c.Kind,
                IsAio = c.IsAio,
                IsGpu = c.IsGpu,
                DeviceId = c.DeviceId,
            })
            .ToList();
        double limit;
        lock (_gate) { limit = _limit.LimitC; }
        return new LintInput
        {
            Curves = curves,
            Channels = named,
            Sources = sources,
            FanRoles = cooling.FanRoles,
            ManualSpeeds = CoolingSnapshots.ManualSpeeds(cooling),
            Uncontrolled = cooling.UncontrolledFanChannels.ToList(),
            LimitC = limit,
        };
    }

    public LintCurvesResponse Lint(SetCurvesBody body)
    {
        var settings = _store.Load();
        var curves = body.Curves.ConvertAll(CurveWireMapper.ToDocument);
        var input = BuildLintInput(settings.Cooling, _fans.GetFanChannels(), _fans.GetTemperatureSources(), curves);
        var hazards = CoolingConfigLint.Analyze(input);
        return new LintCurvesResponse
        {
            Hazards = hazards.ConvertAll(h => new LintHazardDto
            {
                ChannelId = h.ChannelId,
                ChannelName = h.ChannelName,
                Kind = h.Kind,
                RootId = h.RootId,
                RootName = h.RootName,
            }),
            FixAvailable = hazards.Count > 0 && CoolingConfigLint.Heal(input, hazards) is not null,
        };
    }

    /// <summary>Heals the saved config. Returns the new heal state; a no-op when there is nothing to fix.</summary>
    public HealStateDto HealNow(bool automatic)
    {
        if (!_store.Load().Cooling.ThermalGuardEnabled && automatic)
        {
            return BuildHealState(_store.Load().Cooling);
        }
        // Hardware reads stay outside the store lock; the transform runs on the settings
        // the update hands over, so a concurrent curves/set cannot be overwritten.
        var channels = _fans.GetFanChannels();
        var sources = _fans.GetTemperatureSources();
        HealResult? result = null;
        _store.Update(s =>
        {
            var input = BuildLintInput(s.Cooling, channels, sources, s.Cooling.Curves);
            result = CoolingConfigLint.Heal(input, CoolingConfigLint.Analyze(input));
            if (result is null)
            {
                return;
            }
            s.Cooling.HealSnapshot = s.Cooling.Curves.Select(CoolingConfigLint.CloneCurve).ToList();
            s.Cooling.Curves = result.Curves;
            s.Cooling.HealedAtUtcMs = _utcNowMs();
            s.Cooling.HealedChannels = result.Healed
                .Select(h => new HealedChannelRecord { Id = h.ChannelId, Name = h.ChannelName, Hazard = h.Kind })
                .ToList();
        });
        if (result is null)
        {
            return BuildHealState(_store.Load().Cooling);
        }
        var derived = FanProfiles.DerivePresetFromCurves(_store, _fans);
        _store.Update(s => s.Cooling.ActivePreset = derived);
        Console.Error.WriteLine($"[thermal-guard] healed {result.Healed.Count} channel(s): {string.Join(", ", result.Healed.Select(h => h.ChannelId))}");
        if (_hub is not null)
        {
            PanelTopics.BroadcastCooling(_hub);
        }
        Raise("Cooling config repaired", $"{result.Healed.Count} fan channel(s) could stop while the CPU is hot and now have a CPU safety curve.");
        return BuildHealState(_store.Load().Cooling);
    }

    public HealStateDto Undo()
    {
        var settings = _store.Load();
        var snapshot = settings.Cooling.HealSnapshot;
        if (snapshot is null)
        {
            return BuildHealState(settings.Cooling);
        }
        var restored = snapshot.Select(CoolingConfigLint.CloneCurve).ToList();
        _store.Update(s =>
        {
            s.Cooling.Curves = restored;
            s.Cooling.HealSnapshot = null;
            s.Cooling.HealedAtUtcMs = null;
            s.Cooling.HealedChannels = new List<HealedChannelRecord>();
        });
        var derived = FanProfiles.DerivePresetFromCurves(_store, _fans);
        _store.Update(s => s.Cooling.ActivePreset = derived);
        if (_hub is not null)
        {
            PanelTopics.BroadcastCooling(_hub);
        }
        return BuildHealState(_store.Load().Cooling);
    }

    /// <summary>
    /// Partial config update. A limit override is refused while the detected limit comes from
    /// the hardware itself, and nothing changes in that case.
    /// </summary>
    public (ThermalGuardResponse? Result, string? Error) SetConfig(SetThermalGuardConfigBody body)
    {
        if (body.LimitOverrideC is { } requested)
        {
            if (!double.IsFinite(requested))
            {
                return (null, "The limit must be a number.");
            }
            bool hardware;
            lock (_gate) { hardware = _detected.Source == ThermalLimitSources.Hardware; }
            if (hardware)
            {
                return (null, "The CPU reports its own temperature limit, which cannot be overridden.");
            }
        }

        if (body.LimitOverrideC is { } value)
        {
            var clamped = Math.Clamp(value, ThermalLimits.UserMinC, ThermalLimits.UserMaxC);
            _store.Update(s => s.Cooling.ThermalGuardLimitOverrideC = clamped);
        }
        else if (body.ClearLimitOverride == true)
        {
            _store.Update(s => s.Cooling.ThermalGuardLimitOverrideC = null);
        }
        if (body.LimitOverrideC is not null || body.ClearLimitOverride == true)
        {
            // Effective from the next tick on; reflect it now so the response agrees.
            ApplyLimitOverride(_store.Load().Cooling.ThermalGuardLimitOverrideC);
        }

        if (body.Enabled is { } enabled)
        {
            return (SetEnabled(enabled), null);
        }
        if (_hub is not null)
        {
            PanelTopics.BroadcastCooling(_hub);
        }
        return (GetState(), null);
    }

    public ThermalGuardResponse SetEnabled(bool enabled)
    {
        _store.Update(s => s.Cooling.ThermalGuardEnabled = enabled);
        if (!enabled)
        {
            Stand(ThermalGuardStates.Off);
        }
        if (_hub is not null)
        {
            PanelTopics.BroadcastCooling(_hub);
        }
        return GetState();
    }

    private static HealStateDto BuildHealState(CoolingSettings cooling) => new()
    {
        UndoAvailable = cooling.HealSnapshot is not null,
        HealedAtUtcMs = cooling.HealedAtUtcMs,
        Channels = cooling.HealedChannels
            .Select(c => new HealChannelDto { Id = c.Id, Name = c.Name, Hazard = c.Hazard })
            .ToList(),
    };

    public ThermalGuardResponse GetState()
    {
        var cooling = _store.Load().Cooling;
        var response = new ThermalGuardResponse { Heal = BuildHealState(cooling), WatchdogLatched = WatchdogLatched };
        lock (_gate)
        {
            response.DetectedLimitC = _detected.LimitC;
            response.DetectedLimitSource = _detected.Source;
            response.LimitOverrideC = EffectiveFor(cooling.ThermalGuardLimitOverrideC).Effective.Source == ThermalLimitSources.User
                ? EffectiveFor(cooling.ThermalGuardLimitOverrideC).Effective.LimitC
                : null;
            if (!cooling.ThermalGuardEnabled)
            {
                response.State = ThermalGuardStates.Off;
            }
            else
            {
                response.State = _publicState;
                response.GuardTempC = _guardTemp;
                var effective = EffectiveFor(cooling.ThermalGuardLimitOverrideC).Effective;
                response.LimitC = effective.LimitC;
                response.LimitSource = effective.Source;
                response.SinceUtcMs = _sinceUtcMs;
            }
            foreach (var kv in _gpuInfo)
            {
                response.Gpus.Add(new GpuGuardDto
                {
                    Id = kv.Key,
                    Name = kv.Value.Name,
                    TempC = kv.Value.Temp,
                    LimitC = kv.Value.Limit,
                    LimitSource = kv.Value.Source,
                    State = _gpus.TryGetValue(kv.Key, out var g) ? g.State : GpuGuardStates.Inactive,
                });
            }
            if (cooling.LastThermalTrip is { } t)
            {
                var live = _cpu.State is ThermalGuardStates.Tripped or ThermalGuardStates.Escalated;
                response.LastTrip = new ThermalGuardTripDto
                {
                    AtUtcMs = t.AtUtcMs,
                    PeakC = live ? Math.Max(t.PeakC, _cpu.PeakC) : t.PeakC,
                    Reason = t.Reason,
                    Escalated = t.Escalated || _cpu.Escalated,
                };
            }
        }
        return response;
    }
}
