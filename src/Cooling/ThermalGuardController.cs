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
    private const long WatchdogStallMs = 10_000;
    private const long WatchdogPollMs = 2000;
    private static readonly TimeSpan HealTripWindow = TimeSpan.FromHours(24);

    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly MultiplexHub? _hub;
    private readonly ISensorProvider? _sensors;
    private readonly IMonitoringEventStore? _events;
    private readonly DiagnosticsAlertService? _alerts;
    private readonly Func<long> _utcNowMs;

    private readonly object _gate = new();
    private readonly ThermalGuard _cpu = new();
    private readonly Dictionary<string, GpuThermalGuard> _gpus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, double? Temp, double? Limit, string? Source)> _gpuInfo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _gpuManualBackup = new(StringComparer.Ordinal);

    private string _publicState = ThermalGuardStates.Inactive;
    private double? _guardTemp;
    private ThermalLimit _limit = new(ThermalLimits.GenericDefaultC, ThermalLimitSources.Default);
    private long? _sinceUtcMs;
    private long _cpuInfoAtMs = long.MinValue;
    private double? _cpuLoad;
    private long _lastTickMs = Environment.TickCount64;
    private bool _watchdogArmed;
    private bool _watchdogFired;
    private Timer? _watchdog;

    public ThermalGuardController(
        IFanControlProvider fans,
        IConfigStore store,
        MultiplexHub? hub = null,
        ISensorProvider? sensors = null,
        IMonitoringEventStore? events = null,
        DiagnosticsAlertService? alerts = null,
        Func<long>? utcNowMs = null)
    {
        _fans = fans;
        _store = store;
        _hub = hub;
        _sensors = sensors;
        _events = events;
        _alerts = alerts;
        _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // ── Engine hooks ──

    public void StartWatchdog()
    {
        _watchdog ??= new Timer(_ => WatchdogCheck(Environment.TickCount64), null, WatchdogPollMs, WatchdogPollMs);
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
            _lastTickMs = Environment.TickCount64;
            _watchdogFired = false;
        }
    }

    internal void WatchdogCheck(long nowMs)
    {
        lock (_gate)
        {
            if (!_watchdogArmed || _watchdogFired || nowMs - _lastTickMs < WatchdogStallMs)
            {
                return;
            }
            if (_guardTemp is not { } temp || temp < _limit.LimitC - ThermalGuard.FloorStartBelowLimitC)
            {
                return;
            }
            _watchdogFired = true;
        }
        Console.Error.WriteLine("[thermal-guard] curve engine stalled while hot: releasing all fans to BIOS");
        try { _fans.ReleaseAll(); }
        catch { /* the fans are already in an unknown state; nothing more to do */ }
    }

    /// <summary>Called by the engine on a tick where the Cooling feature is off.</summary>
    public void NotifyCoolingOff()
    {
        lock (_gate)
        {
            _watchdogArmed = false;
            _cpu.Reset();
            SetPublicState(ThermalGuardStates.Inactive, null);
        }
    }

    public GuardPlan Evaluate(
        long nowMs,
        NexusSettings settings,
        IReadOnlyList<FanChannel> channels,
        IReadOnlyList<TemperatureSource> sources)
    {
        var cooling = settings.Cooling;
        if (!cooling.ThermalGuardEnabled)
        {
            lock (_gate)
            {
                _watchdogArmed = false;
                _cpu.Reset();
                _gpus.Clear();
                _gpuInfo.Clear();
                _guardTemp = null;
                SetPublicState(ThermalGuardStates.Off, null);
                if (_gpuManualBackup.Count > 0)
                {
                    return new GuardPlan { Output = GuardPlan.None.Output, GpuResume = _gpuManualBackup.Keys.ToList() };
                }
            }
            return GuardPlan.None;
        }

        RefreshCpuInfo(nowMs);

        var guardTemp = ThermalLimits.MaxPlausible(
            sources.Where(s => s.Category == "CPU" && s.DeviceId is null).Select(s => (double)s.Value));
        var nexusDriven = NexusDrivenIds(cooling);
        var roles = cooling.FanRoles;

        double? maxCpuDuty = null;
        foreach (var ch in channels)
        {
            if (CoolingConfigLint.IsCpuCooling(ch, roles))
            {
                maxCpuDuty = Math.Max(maxCpuDuty ?? 0, ch.DutyPercent);
            }
        }

        var eligible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ch in channels)
        {
            if (nexusDriven.Contains(ch.Id) && IsGuardWritable(ch, cooling))
            {
                eligible.Add(ch.Id);
            }
        }

        ThermalGuardOutput output;
        var plan = new GuardPlan();
        lock (_gate)
        {
            _watchdogArmed = true;
            _guardTemp = guardTemp;
            output = _cpu.Step(nowMs, guardTemp, _limit.LimitC, _cpuLoad, maxCpuDuty);
            SetPublicState(output.State, output.TripStarted ? _utcNowMs() : null);
            plan = BuildGpuPlan(nowMs, cooling, channels, sources, nexusDriven, output, eligible);
        }

        HandleTransitions(output, settings);
        return plan;
    }

    // CPU-side writable: controlled, not locked, not a GPU fan (GPU fans have their own guard).
    private static bool IsGuardWritable(FanChannel ch, CoolingSettings cooling) =>
        !ch.IsGpu
        && !cooling.UncontrolledFanChannels.Contains(ch.Id)
        && !FanProfiles.IsLocked(ch, cooling.FanLockOverrides);

    private static HashSet<string> NexusDrivenIds(CoolingSettings cooling)
    {
        var ids = new HashSet<string>(cooling.ManualSpeeds.Keys, StringComparer.Ordinal);
        foreach (var curve in cooling.Curves)
        {
            foreach (var o in curve.Outputs)
            {
                ids.Add(o.Id);
            }
        }
        return ids;
    }

    private GuardPlan BuildGpuPlan(
        long nowMs,
        CoolingSettings cooling,
        IReadOnlyList<FanChannel> channels,
        IReadOnlyList<TemperatureSource> sources,
        HashSet<string> nexusDriven,
        ThermalGuardOutput output,
        HashSet<string> eligible)
    {
        var plan = new GuardPlan { Output = output, Eligible = eligible };
        var gpuTemp = ThermalLimits.MaxPlausible(sources.Where(s => s.Category == "GPU").Select(s => (double)s.Value));
        _gpuInfo.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in channels.Where(c => c.IsGpu && c.DeviceId is not null).GroupBy(c => c.DeviceId!))
        {
            var deviceId = group.Key;
            var name = group.First().DeviceName ?? deviceId;
            var limit = GpuLimit(deviceId);
            _gpuInfo[deviceId] = (name, gpuTemp, limit, limit is null ? null : ThermalLimitSources.Hardware);
            seen.Add(deviceId);
            if (limit is null || gpuTemp is null)
            {
                continue;
            }
            var guarded = group.Where(c => (nexusDriven.Contains(c.Id) || _gpuManualBackup.ContainsKey(c.Id))
                && !cooling.UncontrolledFanChannels.Contains(c.Id)
                && !FanProfiles.IsLocked(c, cooling.FanLockOverrides)).Select(c => c.Id).ToList();
            if (guarded.Count == 0)
            {
                continue;
            }
            if (!_gpus.TryGetValue(deviceId, out var g))
            {
                _gpus[deviceId] = g = new GpuThermalGuard();
            }
            var (state, handBack, resumed) = g.Step(nowMs, gpuTemp.Value, limit.Value);
            if (handBack)
            {
                plan.GpuRelease.AddRange(guarded);
                foreach (var id in guarded)
                {
                    if (cooling.ManualSpeeds.TryGetValue(id, out var manual))
                    {
                        _gpuManualBackup[id] = manual;
                    }
                }
                Console.Error.WriteLine($"[thermal-guard] GPU {name} at {gpuTemp:0.#} C near its {limit} C slowdown threshold: fan handed back to the driver");
            }
            if (resumed)
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

    private static double? GpuLimit(string deviceId)
    {
        var digits = new string(deviceId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }
        return Nvml.GetSlowdownThreshold(index);
    }

    /// <summary>The engine reports the GPU manual duties it must put back after a resume.</summary>
    public void RestoreGpuManual(IEnumerable<string> ids)
    {
        var restore = new List<KeyValuePair<string, int>>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (_gpuManualBackup.Remove(id, out var duty))
                {
                    restore.Add(new(id, duty));
                }
            }
        }
        if (restore.Count > 0)
        {
            _store.Update(s =>
            {
                foreach (var kv in restore)
                {
                    s.Cooling.ManualSpeeds[kv.Key] = kv.Value;
                }
            });
        }
    }

    private void RefreshCpuInfo(long nowMs)
    {
        if (nowMs - _cpuInfoAtMs < CpuInfoRefreshMs)
        {
            return;
        }
        _cpuInfoAtMs = nowMs;
        double? load = null;
        string model = "";
        double? tjMax = null;
        if (_sensors is not null)
        {
            try
            {
                model = _sensors.GetCpuModel();
                tjMax = _sensors.GetCpuTjMaxC();
                var total = _sensors.GetCpuSensors()
                    .FirstOrDefault(s => s.Type == "Load" && s.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase));
                load = total?.Value;
            }
            catch { /* sensor read failed: keep the conservative defaults */ }
        }
        lock (_gate)
        {
            _cpuLoad = load;
            _limit = ThermalLimits.ResolveCpu(model, tjMax);
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
            var text = $"CPU reached {peak:0} C ({cause}). Fans forced to 100%.";
            Raise("CPU thermal guard tripped", text);
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

    private void Raise(string title, string text)
    {
        try { _alerts?.Raise(new DiagnosticsAlertNotice(title, text, "thermalGuard")); }
        catch { /* a subscriber failure must not affect fan control */ }
    }

    private void AppendTimeline(long nowMs, string label, string detail)
    {
        try { _events?.Append(nowMs, "thermalGuard", label, detail, custom: false); }
        catch { /* timeline is best effort */ }
    }

    // ── Heal ──

    internal LintInput BuildLintInput(NexusSettings settings, IReadOnlyList<CurveDocument> curves)
    {
        var cooling = settings.Cooling;
        var channels = _fans.GetFanChannels()
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
            Channels = channels,
            Sources = _fans.GetTemperatureSources(),
            FanRoles = cooling.FanRoles,
            ManualSpeeds = cooling.ManualSpeeds,
            Uncontrolled = cooling.UncontrolledFanChannels,
            LimitC = limit,
        };
    }

    public LintCurvesResponse Lint(SetCurvesBody body)
    {
        var settings = _store.Load();
        var curves = body.Curves.ConvertAll(CurveWireMapper.ToDocument);
        var input = BuildLintInput(settings, curves);
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
        var settings = _store.Load();
        if (!settings.Cooling.ThermalGuardEnabled && automatic)
        {
            return BuildHealState(settings.Cooling);
        }
        var input = BuildLintInput(settings, settings.Cooling.Curves);
        var hazards = CoolingConfigLint.Analyze(input);
        var result = CoolingConfigLint.Heal(input, hazards);
        if (result is null)
        {
            return BuildHealState(settings.Cooling);
        }
        var snapshot = settings.Cooling.Curves.Select(CoolingConfigLint.CloneCurve).ToList();
        var now = _utcNowMs();
        _store.Update(s =>
        {
            s.Cooling.HealSnapshot = snapshot;
            s.Cooling.Curves = result.Curves;
            s.Cooling.HealedAtUtcMs = now;
            s.Cooling.HealedChannels = result.Healed
                .Select(h => new HealedChannelRecord { Id = h.ChannelId, Name = h.ChannelName, Hazard = h.Kind })
                .ToList();
        });
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

    public ThermalGuardResponse SetEnabled(bool enabled)
    {
        _store.Update(s => s.Cooling.ThermalGuardEnabled = enabled);
        if (!enabled)
        {
            lock (_gate)
            {
                _cpu.Reset();
                _guardTemp = null;
                SetPublicState(ThermalGuardStates.Off, null);
            }
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
        var response = new ThermalGuardResponse { Heal = BuildHealState(cooling) };
        lock (_gate)
        {
            if (!cooling.ThermalGuardEnabled)
            {
                response.State = ThermalGuardStates.Off;
            }
            else
            {
                response.State = _publicState;
                response.GuardTempC = _guardTemp;
                response.LimitC = _limit.LimitC;
                response.LimitSource = _limit.Source;
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
