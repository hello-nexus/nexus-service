using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Cooling;

/// <summary>
/// Background service that evaluates fan curves and drives fan speeds.
/// Default tick is 1 s (configurable via SetInterval, minimum 500 ms):
/// reads temperatures, evaluates curves, writes fan duty cycles, replays
/// persisted manual duties onto channels as they become drivable, and
/// broadcasts state via WebSocket hubs.
///
/// On shutdown, releases all fans back to BIOS control to prevent fans
/// from being stuck at a low speed after the service exits. Persisted
/// manual intent (Cooling.ManualSpeeds) survives the release; the replay
/// re-applies it on the next run.
/// </summary>
public sealed class CurveEngine : BackgroundService
{
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;

    // Last calculated speed per curve ID (for ResponseTime smoothing)
    private readonly Dictionary<string, double> _lastSpeed = new();

    // Last raw (pre global-modifier) output per curve ID. Trigger and Auto feed
    // their own previous command back in, and Mixed reads its members', so both
    // need the value before the global boost or it compounds every tick.
    private readonly Dictionary<string, double> _lastRaw = new();

    // Latch/trend state for the curve types that are not pure functions of the
    // current temperature.
    private readonly Dictionary<string, TriggerCurveState> _triggerStates = new();
    private readonly Dictionary<string, AutoCurveState> _autoStates = new();

    // Last duty written per channel + timestamp. Skips a write when the duty
    // is unchanged, and gates the minimum interval between hardware writes per
    // channel so PWM lines don't get hammered if the tick interval is lowered.
    private readonly Dictionary<string, (int Duty, long TickCountMs)> _lastWrite = new();
    private const int MinChannelWriteIntervalMs = 250;

    // Ids whose persisted manual duty has been replayed onto hardware this
    // run. An id is dropped while its channel is absent so a hub reconnect
    // (which resets the hub's duty state) replays the saved value when the
    // channel returns.
    private readonly HashSet<string> _manualReplayed = new();

    // Ids released since they were marked not controlled. Dropped while a
    // channel is absent, so a device that reconnects gets its release
    // re-issued: the release at mark time is a no-op against a provider whose
    // hardware is offline, and without this the fan would hold whatever duty
    // Nexus last drove into its firmware indefinitely - the write gate blocks
    // every later correction.
    private readonly HashSet<string> _releasedUncontrolled = new(StringComparer.Ordinal);

    private int _intervalMs = 1000;
    private readonly FeatureGates _gates;

    // Set by FeatureReconciler on the Cooling ON->OFF edge, consumed by the
    // next disabled Tick (or StopAsync, if shutdown lands first) so the
    // release is serialized through the same single-threaded tick loop as
    // every curve write - it can never race ahead of or behind an in-flight
    // tick that already read the gate as enabled this cycle.
    private int _pendingRelease;

    private readonly ThermalGuardController _guard;

    // Set per tick while the watchdog has latched after repeated stalls: no fan is written.
    private bool _noWrites;
    private bool _latchReleased;

    /// <summary>Millisecond clock for write gating and the guard; tests substitute it.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    // Channels whose duty the guard raised above the user's manual value, and
    // curve outputs the guard drove while their curve produced nothing. Both are
    // put back once the guard stops overriding them.
    private readonly HashSet<string> _guardOverriddenManual = new(StringComparer.Ordinal);
    private readonly HashSet<string> _guardDrivenOrphans = new(StringComparer.Ordinal);

    public CurveEngine(
        IFanControlProvider fans,
        IConfigStore store,
        MultiplexHub hub,
        FeatureGates? gates = null,
        ThermalGuardController? guard = null)
    {
        _fans = fans;
        _store = store;
        _hub = hub;
        _gates = gates ?? FeatureGates.AllEnabled;
        _guard = guard ?? new ThermalGuardController(fans, store, hub);
    }

    public void RequestRelease() => Interlocked.Exchange(ref _pendingRelease, 1);

    private void ReleaseIfPending()
    {
        if (Interlocked.Exchange(ref _pendingRelease, 0) != 1)
        {
            return;
        }
        try
        {
            _fans.ReleaseAll();
            Console.Error.WriteLine("[curve-engine] released all fans (Cooling disabled)");
        }
        catch { /* swallow */ }
    }

    public int GetInterval() => _intervalMs;
    public void SetInterval(int ms) => _intervalMs = Math.Max(500, ms);

    public void ResetSmoothing()
    {
        lock (_lastSpeed) { _lastSpeed.Clear(); }
        lock (_lastRaw) { _lastRaw.Clear(); }
        lock (_triggerStates) { _triggerStates.Clear(); }
        lock (_autoStates) { _autoStates.Clear(); }
        lock (_lastWrite) { _lastWrite.Clear(); }
        // Re-arm the manual replay so the incoming profile's saved duties are
        // applied on the next tick (the profile switch released all fans).
        lock (_manualReplayed) { _manualReplayed.Clear(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let hardware init finish before starting curve evaluation
        await Task.Delay(3000, stoppingToken);
        _guard.StartWatchdog();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[curve-engine] tick failed: {ex.Message}");
            }
            _guard.TickCompleted();

            try
            { await Task.Delay(_intervalMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _guard.StopWatchdog();
        await base.StopAsync(cancellationToken);
        if (!_gates.Cooling)
        {
            // Fulfills a still-pending release if shutdown lands before the
            // next disabled tick would have; otherwise a prior tick already
            // released and this is a no-op write the Cooling-off contract forbids.
            ReleaseIfPending();
            return;
        }
        try
        {
            _fans.ReleaseAll();
            Console.Error.WriteLine("[curve-engine] shutdown - all fans released to BIOS");
        }
        catch { /* swallow */ }
    }

    /// <summary>Drops every id whose persisted manual duty has been replayed
    /// this run, so the next enabled tick replays every manual duty from
    /// scratch. Called from the disabled branch below so a Cooling re-enable
    /// always re-drives, the same guarantee the idle branch gives a curve
    /// re-attach.</summary>
    private void ClearManualReplayed()
    {
        lock (_manualReplayed) { _manualReplayed.Clear(); }
    }

    internal void Tick()
    {
        if (!_gates.Cooling)
        {
            _guard.NotifyCoolingOff();
            ReleaseIfPending();
            ForgetWritesNotOwned(null);
            ClearManualReplayed();
            _guardOverriddenManual.Clear();
            _guardDrivenOrphans.Clear();
            return;
        }

        // The watchdog released every fan from its own thread while this loop was stalled:
        // forget what was written so every duty, unchanged ones included, is written again.
        // After repeated stalls it latches instead and the BIOS keeps the fans.
        _noWrites = _guard.WatchdogLatched;
        if (_noWrites && !_latchReleased)
        {
            // The single writer hands the fans over once itself: a stalled tick may have
            // written after the watchdog timer's release, leaving a stale Nexus duty.
            _latchReleased = true;
            Isolated("latch release", () => _fans.ReleaseAll());
        }
        else if (!_noWrites)
        {
            _latchReleased = false;
        }
        if (_guard.ConsumeWatchdogRelease() || _noWrites)
        {
            lock (_lastWrite) { _lastWrite.Clear(); }
            ClearManualReplayed();
            _guardOverriddenManual.Clear();
            _guardDrivenOrphans.Clear();
        }

        var settings = _store.Load();
        if (settings.Cooling.Curves.Count == 0
            && settings.Cooling.ManualSpeeds.Count == 0
            && settings.Cooling.UncontrolledFanChannels.Count == 0)
        {
            // Idle cooling config: skip the per-tick channel enumeration
            // (on Windows it costs an LHM update + re-discovery).
            _guard.NotifyIdle();
            ForgetWritesNotOwned(null);
            return;
        }

        // Snapshots: the store hands back its live objects and route threads edit them.
        var curves = CoolingSnapshots.Curves(settings.Cooling);
        var manualSnapshot = CoolingSnapshots.ManualSpeeds(settings.Cooling);
        var owned = CurveOwnedIds(curves);

        // A dedup record for a channel no curve references anymore would
        // suppress the first write after the fan is re-attached: a hub can
        // reset (losing its duty state) while unreferenced, and an
        // unchanged duty would then dedup away the re-drive.
        ForgetWritesNotOwned(owned);

        // Channels the providers can drive right now. Hub channels appear
        // seconds after boot (USB connect) and LHM discovery can surface a
        // partial list at first, so per-tick presence gates both the curve
        // write dedup and the manual-duty replay below.
        var present = new HashSet<string>(StringComparer.Ordinal);
        var channelList = _fans.GetFanChannels();
        // Seeded from hardware so a Sync curve pointed at a manual or BIOS fan
        // still has something to follow; curve-driven channels overwrite their
        // entry as they are evaluated below.
        var channelDuty = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var ch in channelList)
        {
            present.Add(ch.Id);
            channelDuty[ch.Id] = ch.DutyPercent;
        }

        // The guard runs before any curve work, in its own try/catch: a curve that
        // throws below must not be able to skip it, and a guard failure must not
        // stop the curves.
        var plan = GuardPlan.None;
        try
        {
            plan = _guard.Evaluate(Clock(), settings, curves, manualSnapshot, channelList, _fans.GetTemperatureSources());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[thermal-guard] evaluation failed: {ex.Message}");
        }
        var activePlan = plan;
        Isolated("guard side effects", () => ApplyPlanSideEffects(activePlan));

        Isolated("uncontrolled release", () => ReleaseUncontrolledOnAppearance(settings, present));
        if (!_noWrites)
        {
            Isolated("manual replay", () => ReplayManualDuties(settings, present, owned));
        }
        Isolated("guard manual pass", () => ApplyGuardToUnownedChannels(activePlan, manualSnapshot, present, owned));

        if (curves.Count == 0)
        {
            return;
        }

        var globalMod = settings.Cooling.GlobalSpeedModifier;
        var calculations = new List<CurveCalculation>();
        var drivenChannels = new HashSet<string>();

        // Snapshot both: the store hands back its live objects, and a route
        // thread can edit curves or offsets while this tick walks them.
        var snapshot = curves;
        var exemptFromGlobal = new HashSet<string>(
            snapshot.Where(CoolingConfigLint.IsGlobalModifierExempt).Select(c => c.Id), StringComparer.Ordinal);
        var offsets = new Dictionary<string, int>(settings.Cooling.FanOffsets, StringComparer.Ordinal);
        ForgetStateNotIn(snapshot);

        // Mixed reads other curves' output and Sync reads a channel another
        // curve may drive, so evaluate in dependency order.
        var rawByCurve = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var curveDoc in CurveOrdering.Sort(snapshot))
        {
            try
            {
                // Flat curves do not need a reading to evaluate, but they still
                // carry a sensor binding that the curve stream reports.
                float? temp = null;
                if (curveDoc.Input.Id.Length > 0)
                {
                    temp = _fans.ReadTemperature(curveDoc.Input.Id);
                }
                if (temp is null && NeedsTemperature(curveDoc.Type))
                {
                    continue;
                }

                double? rawSpeed = curveDoc.Type switch
                {
                    "Flat" => EvaluateFlat(curveDoc.Flat),
                    "Linear" => EvaluateLinear(curveDoc.Linear, temp!.Value),
                    "Graph" => EvaluateGraph(curveDoc.Graph, temp!.Value),
                    "Mixed" => EvaluateMix(curveDoc.Mixed, MixView(rawByCurve, exemptFromGlobal, globalMod)),
                    "Sync" => EvaluateSync(curveDoc.Sync, channelDuty, curveDoc.Outputs),
                    "Trigger" => EvaluateTrigger(curveDoc, temp!.Value),
                    "Auto" => EvaluateAuto(curveDoc, temp!.Value),
                    _ => null,
                };
                if (rawSpeed is null)
                {
                    continue;
                }

                rawByCurve[curveDoc.Id] = rawSpeed.Value;
                lock (_lastRaw) { _lastRaw[curveDoc.Id] = rawSpeed.Value; }

                // Sync mirrors a channel whose duty already carries the global
                // boost; applying it again would compound it.
                var modifiedSpeed = curveDoc.Type == "Sync"
                    ? Math.Clamp(rawSpeed.Value, 0, 100)
                    : Math.Clamp(rawSpeed.Value * globalMod, 0, 100);

                var responseTime = GetResponseTime(curveDoc);
                var smoothedSpeed = ApplySmoothing(curveDoc.Id, modifiedSpeed, responseTime);

                // Apply to all output channels. Dedup against the last duty we
                // wrote to that channel and rate-limit per-channel writes so we
                // don't push redundant PWM commands at the firmware.
                var outputStates = new List<CurveOutputState>();
                var nowMs = Clock();
                foreach (var output in curveDoc.Outputs)
                {
                    var offset = offsets.TryGetValue(output.Id, out var off) ? off : 0;
                    var appliedSpeed = CoolingSafety.ClampDuty((int)Math.Round(smoothedSpeed) + offset);
                    // The guard's floor / override is the last word on a guarded channel's
                    // duty; null means it forbids writing the channel this tick.
                    var guarded = plan.Apply(output.Id, appliedSpeed);
                    if (guarded is int g)
                    {
                        appliedSpeed = g;
                    }
                    channelDuty[output.Id] = appliedSpeed;
                    if (present.Contains(output.Id))
                    {
                        if (guarded is not null && TryReserveWrite(output.Id, appliedSpeed, nowMs))
                        {
                            SafeDrive(output.Id, appliedSpeed);
                        }
                    }
                    else
                    {
                        // No dedup record for a channel that can't take the write:
                        // the first tick after it appears must drive it even at an
                        // unchanged duty. Also covers reconnects - the hub loses
                        // its duty state, so the stale record must not suppress
                        // the re-drive.
                        ForgetWrite(output.Id);
                    }
                    drivenChannels.Add(output.Id);
                    outputStates.Add(new CurveOutputState
                    {
                        ChannelId = output.Id,
                        AppliedSpeed = appliedSpeed,
                    });
                }

                calculations.Add(new CurveCalculation
                {
                    CurveId = curveDoc.Id,
                    InputSensorId = curveDoc.Input.Id,
                    InputTemperature = temp ?? 0f,
                    CalculatedSpeed = modifiedSpeed,
                    ActualSpeed = smoothedSpeed,
                    Outputs = outputStates,
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[curve-engine] curve '{curveDoc.Id}' failed: {ex.Message}");
            }
        }

        Isolated("guard orphan pass", () => ApplyGuardToOrphanOutputs(activePlan, manualSnapshot, present, owned, drivenChannels));

        // Broadcast to multiplexed WebSocket (only if topic has subscribers)
        if (_hub.TopicHasSubscribers("cooling-curves"))
        {
            var payload = new CurveCalculationsFrame
            {
                GlobalSpeedModifier = globalMod,
                Calculations = calculations,
            };
            var env = WsEnvelope.Build("cooling-curves", payload,
                AppJsonContext.Default.CurveCalculationsFrame);
            _ = _hub.BroadcastTopicAsync("cooling-curves", env);
        }

        // "cooling-realtime" is the 1 Hz fan-channel stream consumed by the
        // realtime RPM/duty displays. The "cooling" topic (PanelTopics.BroadcastCooling)
        // is reserved for event-driven state-change notifications fired by
        // route mutations - subscribers there only refetch on real changes
        // instead of every tick.
        if (_hub.TopicHasSubscribers("cooling-realtime"))
        {
            var channels = _fans.GetFanChannels();
            var component = new CoolingComponent
            {
                Id = "fans",
                Name = "Fan Control",
                Type = "Motherboard",
                Devices = new List<CoolingDevice>(),
            };
            foreach (var ch in channels)
            {
                component.Devices.Add(new CoolingDevice
                {
                    Id = ch.Id,
                    Name = ch.Name,
                    Type = "Fan",
                    Speed = ch.DutyPercent,
                    Rpm = ch.Rpm,
                    Pwm = ch.DutyPercent,
                });
            }
            var payload = new GetAllCoolingResponse
            {
                CoolingComponents = new List<CoolingComponent> { component },
            };
            var env = WsEnvelope.Build("cooling-realtime", payload,
                AppJsonContext.Default.GetAllCoolingResponse);
            _ = _hub.BroadcastTopicAsync("cooling-realtime", env);
        }
    }

    // ── Thermal guard ──

    private static void Isolated(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Console.Error.WriteLine($"[curve-engine] {what} failed: {ex.Message}"); }
    }

    // A failed write leaves no dedup record, so the next tick retries it.
    private void SafeDrive(string channelId, int duty)
    {
        if (_noWrites)
        {
            // Nothing was written, so no record may claim it was.
            ForgetWrite(channelId);
            return;
        }
        try { _fans.DriveFanSpeed(channelId, duty); }
        catch (Exception ex)
        {
            ForgetWrite(channelId);
            Console.Error.WriteLine($"[curve-engine] write to {channelId} failed: {ex.Message}");
        }
    }

    /// <summary>Member values as a Mixed sees them: exempt members are divided by the global modifier the Mixed then re-applies.</summary>
    private static IReadOnlyDictionary<string, double> MixView(
        Dictionary<string, double> raw, HashSet<string> exempt, double globalMod)
    {
        if (exempt.Count == 0 || globalMod <= 0 || Math.Abs(globalMod - 1.0) < 1e-9)
        {
            return raw;
        }
        var view = new Dictionary<string, double>(raw, StringComparer.Ordinal);
        foreach (var id in exempt)
        {
            if (view.TryGetValue(id, out var v))
            {
                view[id] = v / globalMod;
            }
        }
        return view;
    }

    /// <summary>One-shot actions the guard asks of the single writer.</summary>
    private void ApplyPlanSideEffects(GuardPlan plan)
    {
        if (plan.Output.TripStarted)
        {
            // No dedup or rate-gate record: the first override write always lands.
            lock (_lastWrite)
            {
                foreach (var id in plan.Eligible)
                {
                    _lastWrite.Remove(id);
                }
            }
        }
        if (plan.Output.ReleaseAllNow)
        {
            // Writes may not be landing: hand every fan to the BIOS and forget what we wrote.
            _fans.ReleaseAll();
            lock (_lastWrite) { _lastWrite.Clear(); }
            Console.Error.WriteLine("[thermal-guard] still heating after the override: all fans released to BIOS");
        }
        if (plan.Output.TripEnded)
        {
            lock (_lastWrite) { _lastWrite.Clear(); }
            ClearManualReplayed();
        }
        foreach (var id in plan.GpuRelease)
        {
            _fans.ReleaseFan(id);
            ForgetWrite(id);
        }
        if (plan.GpuResume.Count > 0)
        {
            _guard.RestoreGpuManual(plan.GpuResume);
            lock (_manualReplayed)
            {
                foreach (var id in plan.GpuResume)
                {
                    _manualReplayed.Remove(id);
                }
            }
            foreach (var id in plan.GpuResume)
            {
                ForgetWrite(id);
            }
        }
    }

    /// <summary>
    /// Guard handling for channels no curve owns: persisted manual duties (raised to the
    /// floor or to 100, restored afterwards) and GPU fans the guard is driving at 100.
    /// </summary>
    private void ApplyGuardToUnownedChannels(GuardPlan plan, IReadOnlyDictionary<string, int> manual, HashSet<string> present, HashSet<string> owned)
    {
        var candidates = new HashSet<string>(manual.Keys, StringComparer.Ordinal);
        candidates.UnionWith(plan.GpuForced);
        candidates.UnionWith(_guardOverriddenManual);
        var nowMs = Clock();
        foreach (var id in candidates)
        {
            if (!present.Contains(id) || owned.Contains(id))
            {
                continue;
            }
            var hasManual = manual.TryGetValue(id, out var saved);
            int? desired = plan.GpuForced.Contains(id) ? 100 : (hasManual ? plan.Apply(id, saved) : null);
            if (desired is null)
            {
                continue;
            }
            if (!hasManual || desired.Value != saved)
            {
                _guardOverriddenManual.Add(id);
                if (TryReserveWrite(id, desired.Value, nowMs))
                {
                    SafeDrive(id, desired.Value);
                }
            }
            else if (_guardOverriddenManual.Remove(id))
            {
                ForgetWrite(id);
                SafeDrive(id, saved);
            }
        }
    }

    /// <summary>
    /// Curve outputs whose curve produced nothing this tick (sync source off, sensor
    /// missing): the guard still raises them while it overrides, and hands them back to
    /// their driver as soon as it stops, including when the guard is switched off.
    /// </summary>
    private void ApplyGuardToOrphanOutputs(
        GuardPlan plan,
        IReadOnlyDictionary<string, int> manual,
        HashSet<string> present,
        HashSet<string> owned,
        HashSet<string> driven)
    {
        var nowMs = Clock();
        foreach (var id in _guardDrivenOrphans.ToList())
        {
            var stillOverridden = plan.Output.FloorDuty > 0 && plan.Eligible.Contains(id) && !driven.Contains(id) && owned.Contains(id);
            if (stillOverridden || !present.Contains(id))
            {
                continue;
            }
            _guardDrivenOrphans.Remove(id);
            ForgetWrite(id);
            if (!manual.ContainsKey(id))
            {
                _fans.ReleaseFan(id);
            }
        }
        if (plan.Output.FloorDuty <= 0)
        {
            return;
        }
        foreach (var id in owned)
        {
            if (driven.Contains(id) || !present.Contains(id) || !plan.Eligible.Contains(id))
            {
                continue;
            }
            if (plan.Apply(id, 0) is int d)
            {
                _guardDrivenOrphans.Add(id);
                if (TryReserveWrite(id, d, nowMs))
                {
                    SafeDrive(id, d);
                }
            }
        }
    }

    // ── Curve evaluation ──

    internal static double? EvaluateFlat(FlatCurveData? flat)
    {
        return flat?.Speed;
    }

    internal static double? EvaluateLinear(LinearCurveData? linear, float temp)
    {
        if (linear is null)
        {
            return null;
        }

        if (temp <= linear.MinTemp)
        {
            return linear.MinSpeed;
        }

        if (temp >= linear.MaxTemp)
        {
            return linear.MaxSpeed;
        }

        var ratio = (temp - linear.MinTemp) / (linear.MaxTemp - linear.MinTemp);
        return linear.MinSpeed + ratio * (linear.MaxSpeed - linear.MinSpeed);
    }

    internal static double? EvaluateGraph(GraphCurveData? graph, float temp)
    {
        if (graph is null || graph.Points.Count == 0)
        {
            return null;
        }

        var points = graph.Points;
        points.Sort((a, b) => a.Temp.CompareTo(b.Temp));

        if (temp <= points[0].Temp)
        {
            return points[0].Speed * graph.SpeedModifier;
        }

        if (temp >= points[points.Count - 1].Temp)
        {
            return points[points.Count - 1].Speed * graph.SpeedModifier;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            if (temp >= points[i].Temp && temp <= points[i + 1].Temp)
            {
                var range = points[i + 1].Temp - points[i].Temp;
                if (range <= 0)
                {
                    return points[i].Speed * graph.SpeedModifier;
                }

                var ratio = (temp - points[i].Temp) / range;
                var speed = points[i].Speed + ratio * (points[i + 1].Speed - points[i].Speed);
                return speed * graph.SpeedModifier;
            }
        }

        return points[points.Count - 1].Speed * graph.SpeedModifier;
    }

    /// <summary>Curve types driven by a temperature sensor. Flat, Mixed and Sync have no input of their own.</summary>
    internal static bool NeedsTemperature(string type) =>
        type is "Linear" or "Graph" or "Trigger" or "Auto";

    /// <summary>
    /// Combine member curves' raw output. A member that did not evaluate this
    /// tick (missing sensor, dangling id) is skipped; no member at all yields
    /// null so the curve drives nothing rather than falling to 0.
    /// </summary>
    internal static double? EvaluateMix(MixedCurveData? mixed, IReadOnlyDictionary<string, double> rawByCurve)
    {
        if (mixed is null || mixed.CurveIds.Count == 0)
        {
            return null;
        }

        var values = new List<double>(mixed.CurveIds.Count);
        foreach (var id in mixed.CurveIds)
        {
            if (rawByCurve.TryGetValue(id, out var v))
            {
                values.Add(v);
            }
        }
        if (values.Count == 0)
        {
            return null;
        }

        return mixed.Fn switch
        {
            "min" => values.Min(),
            "avg" => values.Average(),
            "sum" => Math.Clamp(values.Sum(), 0, 100),
            "subtract" => Math.Clamp(SubtractAll(values), 0, 100),
            _ => values.Max(),
        };
    }

    private static double SubtractAll(List<double> values)
    {
        var result = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            result -= values[i];
        }
        return result;
    }

    /// <summary>Mirror another channel's duty, scaled or offset. Null while that channel is absent.</summary>
    internal static double? EvaluateSync(
        SyncCurveData? sync,
        IReadOnlyDictionary<string, double> channelDuty,
        IReadOnlyList<CurveOutputDocument>? outputs = null)
    {
        if (sync is null || string.IsNullOrEmpty(sync.SourceChannelId))
        {
            return null;
        }
        // Following a channel this same curve drives would feed its offset back
        // into itself every tick and ramp to a limit.
        if (outputs is not null)
        {
            foreach (var o in outputs)
            {
                if (string.Equals(o.Id, sync.SourceChannelId, StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }
        if (!channelDuty.TryGetValue(sync.SourceChannelId, out var source))
        {
            return null;
        }
        return sync.Proportional
            ? source * (1.0 + sync.Offset / 100.0)
            : source + sync.Offset;
    }

    private double? EvaluateTrigger(CurveDocument doc, float temp)
    {
        if (doc.Trigger is null)
        {
            return null;
        }
        TriggerCurveState state;
        lock (_triggerStates)
        {
            if (!_triggerStates.TryGetValue(doc.Id, out state!))
            {
                state = new TriggerCurveState();
                _triggerStates[doc.Id] = state;
            }
        }
        return state.Evaluate(doc.Trigger, temp, PreviousRaw(doc.Id), ToTicks(doc.Trigger.ResponseTime));
    }

    private double? EvaluateAuto(CurveDocument doc, float temp)
    {
        if (doc.Auto is null)
        {
            return null;
        }
        AutoCurveState state;
        lock (_autoStates)
        {
            if (!_autoStates.TryGetValue(doc.Id, out state!))
            {
                state = new AutoCurveState();
                _autoStates[doc.Id] = state;
            }
        }
        return state.Evaluate(doc.Auto, temp, PreviousRaw(doc.Id), ToTicks(doc.Auto.ResponseTime));
    }

    /// <summary>
    /// Drops per-curve state for curves that no longer exist, so a deleted (or
    /// retyped) curve cannot hand its old output back to a new state machine
    /// that reuses the id.
    /// </summary>
    private void ForgetStateNotIn(IReadOnlyList<CurveDocument> curves)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in curves)
        {
            live.Add(c.Id);
        }

        lock (_lastRaw)
        {
            foreach (var id in _lastRaw.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _lastRaw.Remove(id);
            }
        }
        lock (_triggerStates)
        {
            foreach (var id in _triggerStates.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _triggerStates.Remove(id);
            }
        }
        lock (_autoStates)
        {
            foreach (var id in _autoStates.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _autoStates.Remove(id);
            }
        }
    }

    private double? PreviousRaw(string curveId)
    {
        lock (_lastRaw)
        {
            return _lastRaw.TryGetValue(curveId, out var v) ? v : null;
        }
    }

    /// <summary>Response time in seconds to whole engine ticks, floor 1.</summary>
    private int ToTicks(double seconds) =>
        Math.Max(1, (int)Math.Round(seconds * 1000.0 / _intervalMs));

    private static double GetResponseTime(CurveDocument doc)
    {
        return doc.Type switch
        {
            "Linear" => doc.Linear?.ResponseTime ?? 1.0,
            "Graph" => doc.Graph?.ResponseTime ?? 1.0,
            "Mixed" => doc.Mixed?.ResponseTime ?? 1.0,
            // Trigger latches, Auto steps and Sync mirrors an already-smoothed
            // channel: each owns its own timing, so no slew limit on top.
            "Trigger" or "Auto" or "Sync" => 0.0,
            _ => 1.0,
        };
    }

    /// <summary>
    /// Hands every present, not-controlled channel back to its provider once
    /// per appearance. Marking a channel uncontrolled releases it there and
    /// then, but that call reaches nothing when the owning device is offline,
    /// and the write gate blocks every later attempt - so the release has to
    /// be re-issued when the channel comes back.
    /// </summary>
    private void ReleaseUncontrolledOnAppearance(NexusSettings settings, HashSet<string> present)
    {
        var uncontrolled = settings.Cooling.UncontrolledFanChannels;
        if (uncontrolled.Count == 0)
        {
            _releasedUncontrolled.Clear();
            return;
        }
        // Forget an id that went absent (so it releases again on return) or
        // that the user handed back to Nexus.
        foreach (var id in _releasedUncontrolled.ToList())
        {
            if (!present.Contains(id) || !uncontrolled.Contains(id))
            {
                _releasedUncontrolled.Remove(id);
            }
        }
        foreach (var id in uncontrolled)
        {
            if (!present.Contains(id)) continue;
            if (!_releasedUncontrolled.Add(id)) continue;
            _fans.ReleaseFan(id);
        }
    }

    /// <summary>
    /// Replays persisted user manual duties (Cooling.ManualSpeeds) onto
    /// hardware as their channels become drivable. Runs every tick: hub
    /// channels connect seconds after boot and LHM discovery can return a
    /// partial channel list at first, so a one-shot restore pass misses
    /// late channels. Ids owned by a curve output are skipped and re-armed:
    /// the curve drives them for now, and the saved duty must replay when
    /// the curve releases the channel (leaving a preset for Custom).
    /// </summary>
    private void ReplayManualDuties(NexusSettings settings, HashSet<string> present, HashSet<string> curveOwned)
    {
        var manual = settings.Cooling.ManualSpeeds;
        if (manual.Count == 0)
        {
            return;
        }

        // Snapshot: SetFanSpeed below writes back into this dictionary, and
        // route threads mutate it via store.Update without a shared lock.
        // The enumerator's version check turns a racing structural change
        // into InvalidOperationException (Dictionary.ToArray would take the
        // ICollection fast path instead, whose race failures are an
        // ArgumentException or torn null-key entries). Abort only the
        // replay, not the tick's curve work; the next tick retries.
        var snapshot = new List<KeyValuePair<string, int>>(manual.Count);
        try
        {
            foreach (var kv in manual)
            {
                snapshot.Add(kv);
            }
        }
        catch (InvalidOperationException) { return; }

        // Collect under the lock, write hardware outside it: SetFanSpeed
        // re-records the entry via IConfigStore.Update, whose OnChanged
        // handlers must not run while the replay lock is held.
        List<(string Id, int Duty)>? toApply = null;
        lock (_manualReplayed)
        {
            foreach (var kv in snapshot)
            {
                if (!present.Contains(kv.Key))
                {
                    _manualReplayed.Remove(kv.Key);
                    continue;
                }
                if (curveOwned.Contains(kv.Key))
                {
                    _manualReplayed.Remove(kv.Key);
                    continue;
                }
                if (!_manualReplayed.Add(kv.Key))
                {
                    continue;
                }
                (toApply ??= new()).Add((kv.Key, kv.Value));
            }
        }

        if (toApply is null)
        {
            return;
        }
        foreach (var (id, duty) in toApply)
        {
            _fans.SetFanSpeed(id, duty);
            ServiceLog.Info($"[curve-engine] manual duty restored: {id} -> {duty}%");
        }
    }

    private static HashSet<string> CurveOwnedIds(List<CurveDocument> curves)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var curve in curves)
        {
            foreach (var o in curve.Outputs)
            {
                owned.Add(o.Id);
            }
        }
        return owned;
    }

    private void ForgetWrite(string channelId)
    {
        lock (_lastWrite) { _lastWrite.Remove(channelId); }
    }

    /// <summary>Drop dedup records for channels not owned by any curve
    /// (null = no curves, drop all), so a fan re-attached later is driven
    /// on its first tick even at an unchanged duty.</summary>
    private void ForgetWritesNotOwned(HashSet<string>? owned)
    {
        lock (_lastWrite)
        {
            if (_lastWrite.Count == 0)
            {
                return;
            }
            if (owned is null)
            {
                _lastWrite.Clear();
                return;
            }
            List<string>? stale = null;
            foreach (var id in _lastWrite.Keys)
            {
                // A manual channel the guard is overriding keeps its record, or the
                // override would be rewritten every tick.
                if (!owned.Contains(id) && !_guardOverriddenManual.Contains(id))
                {
                    (stale ??= new()).Add(id);
                }
            }
            if (stale is not null)
            {
                foreach (var id in stale)
                {
                    _lastWrite.Remove(id);
                }
            }
        }
    }

    // Atomic check-and-reserve: returns true and records the write iff the
    // duty changed and the per-channel rate limit has elapsed. Holds _lastWrite
    // under lock so a concurrent ResetSmoothing.Clear cannot race the indexer.
    private bool TryReserveWrite(string channelId, int appliedSpeed, long nowMs)
    {
        lock (_lastWrite)
        {
            if (!_lastWrite.TryGetValue(channelId, out var prev))
            {
                _lastWrite[channelId] = (appliedSpeed, nowMs);
                return true;
            }

            if (prev.Duty == appliedSpeed)
            {
                return false;
            }

            if (nowMs - prev.TickCountMs < MinChannelWriteIntervalMs)
            {
                return false;
            }

            _lastWrite[channelId] = (appliedSpeed, nowMs);
            return true;
        }
    }

    private double ApplySmoothing(string curveId, double targetSpeed, double responseTimeSec)
    {
        if (!_lastSpeed.TryGetValue(curveId, out var lastSpeed))
        {
            _lastSpeed[curveId] = targetSpeed;
            return targetSpeed;
        }

        if (responseTimeSec <= 0)
        {
            _lastSpeed[curveId] = targetSpeed;
            return targetSpeed;
        }

        var tickSeconds = _intervalMs / 1000.0;
        var maxDelta = (100.0 / responseTimeSec) * tickSeconds;
        var delta = targetSpeed - lastSpeed;

        if (Math.Abs(delta) <= maxDelta)
        {
            _lastSpeed[curveId] = targetSpeed;
            return targetSpeed;
        }

        var smoothed = lastSpeed + Math.Sign(delta) * maxDelta;
        _lastSpeed[curveId] = smoothed;
        return smoothed;
    }
}
