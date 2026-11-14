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

    // Last failure message per channel, so SafeDrive logs a persistent refusal once.
    private readonly Dictionary<string, string> _driveErrors = new(StringComparer.Ordinal);

    // Ids whose persisted manual duty has been replayed onto hardware this
    // run. An id is dropped while its channel is absent so a hub reconnect
    // (which resets the hub's duty state) replays the saved value when the
    // channel returns.
    private readonly HashSet<string> _manualReplayed = new();
    // Bumped under _manualReplayed by InvalidateManual so a replay pass that
    // snapshotted the old duty cannot mark the channel replayed afterwards.
    private int _manualGeneration;

    /// <summary>Test seam: runs after a replay pass snapshots the saved duties, before it commits them.</summary>
    internal Action? ReplaySnapshotTaken { get; set; }

    // Ids released since they were marked not controlled. Dropped while a
    // channel is absent, so a device that reconnects gets its release
    // re-issued: the release at mark time is a no-op against a provider whose
    // hardware is offline, and without this the fan would hold whatever duty
    // Nexus last drove into its firmware indefinitely - the write gate blocks
    // every later correction.
    private readonly HashSet<string> _releasedUncontrolled = new(StringComparer.Ordinal);

    private int _intervalMs = 1000;
    // What the tick is doing, for the watchdog's stall report; _stepCurve names the curve while _step is CurveStep.
    private const string CurveStep = "curve";
    private volatile string _step = "starting";
    private volatile string _stepCurve = "";
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
    private long? _escalatedReassertMs;
    private const long EscalatedReassertIntervalMs = 5000;

    /// <summary>Millisecond clock for write gating and the guard; tests substitute it.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    // Channels whose duty the guard raised above the user's manual value, and
    // curve outputs the guard drove while their curve produced nothing. Both are
    // put back once the guard stops overriding them.
    private readonly HashSet<string> _guardOverriddenManual = new(StringComparer.Ordinal);
    private readonly HashSet<string> _guardDrivenOrphans = new(StringComparer.Ordinal);
    // Hardware-control channels (no curve, no manual duty) the guard is driving; released when it clears.
    private readonly HashSet<string> _guardDrivenHardware = new(StringComparer.Ordinal);
    // Hardware channels that left mid-trip: released when they re-enumerate, or forgotten after a while.
    private readonly Dictionary<string, long> _guardPendingRelease = new(StringComparer.Ordinal);
    private const long PendingReleaseTtlMs = 10 * 60_000;

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

    /// <summary>Fulfills a queued release now; shutdown calls it so a stop before the next tick still hands the fans back.</summary>
    public void DrainPendingRelease() => ReleaseIfPending();

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

    /// <summary>Makes the next tick replay the stored manual duty of each id
    /// through the normal path (feature gate, thermal guard, clamps), for a
    /// caller that just rewrote those duties.</summary>
    public void InvalidateManual(IEnumerable<string> ids)
    {
        lock (_manualReplayed)
        {
            _manualGeneration++;
            foreach (var id in ids)
            {
                _manualReplayed.Remove(id);
            }
        }
        foreach (var id in ids)
        {
            ForgetWrite(id);
        }
    }

    public void ResetSmoothing()
    {
        lock (_lastSpeed) { _lastSpeed.Clear(); }
        lock (_lastRaw) { _lastRaw.Clear(); }
        ClearMissingSourceState();
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
        _guard.StartWatchdog(() => _step == CurveStep ? $"curve '{_stepCurve}'" : _step);

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
            _step = "waiting for the next tick";

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
            // A wireless port Nexus drives keeps its last duty: a release would send the follow-motherboard
            // byte and a receiver with no header signal stops the fan.
            _fans.ReleaseAllKeepingDrivenPorts();
            Console.Error.WriteLine("[curve-engine] shutdown - fans released to BIOS; wireless ports keep their last duty");
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
        try
        {
            TickCore();
        }
        finally
        {
            _guard.CommitFloorApplied();
        }
    }

    private void TickCore()
    {
        if (!_gates.Cooling)
        {
            _guard.NotifyCoolingOff();
            ReleaseIfPending();
            ForgetWritesNotOwned(null);
            ClearManualReplayed();
            _guardOverriddenManual.Clear();
            _guardDrivenOrphans.Clear();
            _guardDrivenHardware.Clear();
            _guardPendingRelease.Clear();
            return;
        }

        // Cached temperatures are not a basis for curve writes: the watchdog's full-speed cooling mode stays in place until the walk delivers fresh readings.
        if (!_guard.ReadingsFresh())
        {
            _step = "waiting for fresh readings";
            return;
        }

        // The watchdog drove cooling mode from its own thread while this loop was stalled:
        // forget what was written so every duty, unchanged ones included, is written again.
        // After repeated stalls it latches instead and cooling mode holds the fans until it clears.
        // Limit detection runs whether or not the guard is enabled, so the settings route can
        // trust what it reports.
        Isolated("guard limit detection", () => _guard.RefreshDetection(Clock()));
        _noWrites = _guard.WatchdogLatched;
        var (watchdogDrove, restoredTrip, driven) = _guard.ConsumeWatchdogOutcome();
        // A user restore ended a trip: the hardware channels the guard was driving go back to hardware once.
        var guardHeld = restoredTrip ? _guardDrivenHardware.ToList() : new List<string>();
        if (watchdogDrove || _noWrites)
        {
            lock (_lastWrite) { _lastWrite.Clear(); }
            ClearManualReplayed();
            _guardOverriddenManual.Clear();
            _guardDrivenOrphans.Clear();
            _guardDrivenHardware.Clear();
            _guardPendingRelease.Clear();
        }

        _step = "loading settings";
        var settings = _store.Load();
        // Channels the user left on hardware control went to full speed with the rest; once the engine runs they go back.
        var watchdogHardware = driven.Union(guardHeld).ToList();
        void ReleaseWatchdogHardware()
        {
            if (watchdogHardware.Count == 0)
            {
                return;
            }
            Isolated("watchdog hardware release", () =>
            {
                var heldOwned = CurveOwnedIds(CoolingSnapshots.Curves(settings.Cooling));
                var heldManual = CoolingSnapshots.ManualSpeeds(settings.Cooling);
                foreach (var id in watchdogHardware)
                {
                    ReleaseGuardHardware(id, heldOwned, heldManual);
                }
            });
        }
        if (settings.Cooling.Curves.Count == 0
            && settings.Cooling.ManualSpeeds.Count == 0
            && settings.Cooling.UncontrolledFanChannels.Count == 0
            && !IdleGuardWake(settings))
        {
            // Idle cooling config: skip the per-tick channel enumeration
            // (on Windows it costs an LHM update + re-discovery).
            ReleaseWatchdogHardware();
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
        _step = "reading fan channels";
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
            _step = "reading temperature sources";
            var sources = _fans.GetTemperatureSources();
            _step = "evaluating the thermal guard";
            plan = _guard.Evaluate(Clock(), settings, curves, manualSnapshot, channelList, sources);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[thermal-guard] evaluation failed: {ex.Message}");
        }
        // A guard that is driving takes these channels itself and hands them back when its trip ends.
        if (plan.Output.State is not (ThermalGuardStates.Tripped or ThermalGuardStates.Escalated))
        {
            ReleaseWatchdogHardware();
        }
        var activePlan = plan;
        Isolated("guard side effects", () => ApplyPlanSideEffects(activePlan));

        Isolated("uncontrolled release", () => ReleaseUncontrolledOnAppearance(settings, present));
        if (!_noWrites)
        {
            Isolated("manual replay", () => ReplayManualDuties(settings, present, owned, activePlan));
        }
        // Re-read: a replay or an import since the snapshot may have changed a duty,
        // and a restore from the stale copy would undo it.
        Isolated("guard manual pass", () => ApplyGuardToUnownedChannels(activePlan, CoolingSnapshots.ManualSpeeds(settings.Cooling), present, owned));

        if (!_noWrites)
        {
            Isolated("guard hardware pass", () => ApplyGuardToHardwareChannels(
                activePlan, present, owned, CoolingSnapshots.ManualSpeeds(settings.Cooling)));
        }

        if (curves.Count == 0)
        {
            ClearMissingSourceState();
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
        var fellBack = new HashSet<string>(StringComparer.Ordinal);

        foreach (var curveDoc in CurveOrdering.Sort(snapshot))
        {
            _stepCurve = curveDoc.Id;
            _step = CurveStep;
            try
            {
                // Flat curves do not need a reading to evaluate, but they still
                // carry a sensor binding that the curve stream reports.
                float? temp = null;
                if (curveDoc.Input.Id.Length > 0)
                {
                    temp = _fans.ReadTemperature(curveDoc.Input.Id);
                }
                // A source that reads nothing (hub unplugged, not detected yet) holds the curve's fans at the
                // last duty for a few ticks, which covers a stalled read, a start-up gap and a one-tick null.
                // After that they are floored at a safe duty: never below it, never cut below what they already
                // run at. A mix is unreadable when none of its members evaluated (missing or dangling). The
                // guard still owns the final duty below, so it wins over this.
                var unreadable = (temp is null && NeedsTemperature(curveDoc.Type))
                    || (curveDoc.Type == "Mixed" && NoMemberEvaluated(curveDoc.Mixed, rawByCurve));
                var missingSource = false;
                if (unreadable)
                {
                    int misses;
                    lock (_missingTicks)
                    {
                        misses = _missingTicks.GetValueOrDefault(curveDoc.Id) + 1;
                        _missingTicks[curveDoc.Id] = misses;
                    }
                    if (misses < MissingSourceGraceTicks)
                    {
                        continue;
                    }
                    missingSource = true;
                    fellBack.Add(curveDoc.Id);
                }
                else
                {
                    lock (_missingTicks) { _missingTicks.Remove(curveDoc.Id); }
                }

                double? rawSpeed = missingSource ? MissingSourceDuty : curveDoc.Type switch
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

                // A fallback duty is not a reading: mixes skip it as a missing member, and the scale
                // and smoothing that shape a curve's output would only delay the safe duty.
                if (!missingSource)
                {
                    rawByCurve[curveDoc.Id] = rawSpeed.Value;
                    lock (_lastRaw) { _lastRaw[curveDoc.Id] = rawSpeed.Value; }
                }

                // Sync mirrors a channel whose duty already carries the global
                // boost; applying it again would compound it. The guard curve is a
                // safety curve and is never scaled.
                var modifiedSpeed = missingSource || CoolingConfigLint.IsGlobalModifierExempt(curveDoc)
                    ? Math.Clamp(rawSpeed.Value, 0, 100)
                    : Math.Clamp(rawSpeed.Value * globalMod, 0, 100);

                var responseTime = GetResponseTime(curveDoc);
                double smoothedSpeed;
                if (missingSource)
                {
                    _lastSpeed[curveDoc.Id] = modifiedSpeed;
                    smoothedSpeed = modifiedSpeed;
                }
                else
                {
                    smoothedSpeed = ApplySmoothing(curveDoc.Id, modifiedSpeed, responseTime);
                }

                // Apply to all output channels. Dedup against the last duty we
                // wrote to that channel and rate-limit per-channel writes so we
                // don't push redundant PWM commands at the firmware.
                var outputStates = new List<CurveOutputState>();
                var nowMs = Clock();
                foreach (var output in curveDoc.Outputs)
                {
                    var offset = offsets.TryGetValue(output.Id, out var off) ? off : 0;
                    var appliedSpeed = missingSource
                        ? CoolingSafety.ClampDuty((int)Math.Round(Math.Max(MissingSourceDuty, channelDuty.GetValueOrDefault(output.Id))))
                        : CoolingSafety.ClampDuty((int)Math.Round(smoothedSpeed) + offset);
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

                if (missingSource)
                {
                    continue;
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

        LogFallbackTransitions(fellBack);

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
            _step = "reading fan channels for the realtime stream";
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

    // An idle config still needs the guard when the CPU runs hot: every channel may be on
    // hardware control. The temperature probe is rate limited so an idle, cool machine pays
    // one read per interval and writes nothing.
    private const long IdleProbeIntervalMs = 5000;
    private long _idleProbeAtMs = long.MinValue / 2;

    private bool IdleGuardWake(NexusSettings settings)
    {
        // Anything the guard still holds must be released, whatever the switch says now.
        if (_guardDrivenHardware.Count > 0 || _guardPendingRelease.Count > 0)
        {
            return true;
        }
        if (!settings.Cooling.ThermalGuardEnabled || _noWrites)
        {
            return false;
        }
        var now = Clock();
        var active = _guard.IdleNeedsGuard(Array.Empty<TemperatureSource>());
        if (active)
        {
            return true;
        }
        if (now - _idleProbeAtMs < IdleProbeIntervalMs)
        {
            return false;
        }
        _idleProbeAtMs = now;
        return _guard.IdleNeedsGuard(_fans.GetTemperatureSources());
    }

    private void Isolated(string what, Action action)
    {
        _step = what;
        try { action(); }
        catch (Exception ex) { Console.Error.WriteLine($"[curve-engine] {what} failed: {ex.Message}"); }
    }

    // A failed write leaves no dedup record, so the next tick retries it.
    private void SafeDrive(string channelId, int duty)
    {
        if (_noWrites || _guard.WatchdogLatched)
        {
            // Cooling mode holds the fans; nothing was written, so no record may claim it was.
            ForgetWrite(channelId);
            return;
        }
        try
        {
            _fans.DriveFanSpeed(channelId, duty);
            lock (_driveErrors)
            {
                _driveErrors.Remove(channelId);
            }
        }
        catch (Exception ex)
        {
            ForgetWrite(channelId);
            // Retried every tick, so a refusal that persists is logged once until it changes.
            bool repeat;
            lock (_driveErrors)
            {
                repeat = _driveErrors.TryGetValue(channelId, out var last) && last == ex.Message;
                _driveErrors[channelId] = ex.Message;
            }
            if (!repeat) Console.Error.WriteLine($"[curve-engine] write to {channelId} failed: {ex.Message}");
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
            Console.Error.WriteLine("[thermal-guard] still heating after the override: cooling mode, every fan driven to full speed again");
        }
        if (plan.Output.State == ThermalGuardStates.Escalated)
        {
            // Writes may not be landing: forget them so every fan is driven again, spaced for per-write USB hubs.
            var now = Clock();
            if (_escalatedReassertMs is not { } last || now - last >= EscalatedReassertIntervalMs)
            {
                _escalatedReassertMs = now;
                lock (_lastWrite) { _lastWrite.Clear(); }
            }
        }
        else
        {
            _escalatedReassertMs = null;
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

    /// <summary>
    /// Channels left on hardware control (no curve, no manual duty) whose provider genuinely hands
    /// them back: while the guard is Tripped they are driven to full speed, and released once it
    /// clears. The Floor ramp never touches them (a board curve may already be above the floor).
    /// </summary>
    private void ApplyGuardToHardwareChannels(
        GuardPlan plan, HashSet<string> present, HashSet<string> owned, IReadOnlyDictionary<string, int> manual)
    {
        var nowMs = Clock();
        var take = plan.Output.State is ThermalGuardStates.Tripped or ThermalGuardStates.Escalated;
        if (take)
        {
            foreach (var id in plan.Hardware)
            {
                if (!present.Contains(id) || owned.Contains(id) || manual.ContainsKey(id))
                {
                    continue;
                }
                _guardPendingRelease.Remove(id);
                _guardDrivenHardware.Add(id);
                if (TryReserveWrite(id, 100, nowMs))
                {
                    SafeDrive(id, 100);
                }
            }
        }
        foreach (var id in _guardDrivenHardware.ToList())
        {
            if (!present.Contains(id))
            {
                // Gone mid-trip: clear the provider's held duty now (a wireless hub would re-send it on
                // rebind), and release again on return in case the hardware kept the last write.
                _guardDrivenHardware.Remove(id);
                ForgetWrite(id);
                if (!owned.Contains(id) && !manual.ContainsKey(id))
                {
                    _fans.ReleaseFan(id);
                }
                _guardPendingRelease[id] = nowMs + PendingReleaseTtlMs;
                continue;
            }
            if (take && plan.Hardware.Contains(id) && !owned.Contains(id) && !manual.ContainsKey(id))
            {
                continue;
            }
            _guardDrivenHardware.Remove(id);
            ReleaseGuardHardware(id, owned, manual);
        }
        foreach (var (id, expiry) in _guardPendingRelease.ToList())
        {
            if (present.Contains(id))
            {
                _guardPendingRelease.Remove(id);
                ReleaseGuardHardware(id, owned, manual);
            }
            else if (nowMs >= expiry)
            {
                _guardPendingRelease.Remove(id);
            }
        }
    }

    // A channel the user gave a manual duty or a curve in the meantime is theirs now: the release
    // would also delete that manual duty on some providers, so only the dedup record is dropped.
    private void ReleaseGuardHardware(string id, HashSet<string> owned, IReadOnlyDictionary<string, int> manual)
    {
        ForgetWrite(id);
        if (!owned.Contains(id) && !manual.ContainsKey(id))
        {
            _fans.ReleaseFan(id);
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

    /// <summary>The duty a temperature-driven curve holds while its source reads nothing.</summary>
    internal const double MissingSourceDuty = 70;

    /// <summary>Consecutive ticks a source must read nothing before the fallback duty applies.</summary>
    internal const int MissingSourceGraceTicks = 3;

    /// <summary>True when a mix has members and none of them produced a value this tick (missing or dangling).</summary>
    internal static bool NoMemberEvaluated(MixedCurveData? mixed, IReadOnlyDictionary<string, double> rawByCurve)
    {
        if (mixed is null || mixed.CurveIds.Count == 0)
        {
            return false;
        }
        foreach (var id in mixed.CurveIds)
        {
            if (rawByCurve.ContainsKey(id))
            {
                return false;
            }
        }
        return true;
    }

    // Consecutive ticks each curve's source has read nothing.
    private readonly Dictionary<string, int> _missingTicks = new(StringComparer.Ordinal);

    // Curves currently on the fallback duty, so entering and leaving log once rather than every tick.
    private readonly HashSet<string> _fallbackActive = new(StringComparer.Ordinal);

    private void ClearMissingSourceState()
    {
        lock (_missingTicks)
        {
            _missingTicks.Clear();
            _fallbackActive.Clear();
        }
    }

    private void LogFallbackTransitions(HashSet<string> fellBack)
    {
        lock (_missingTicks)
        {
            foreach (var id in fellBack)
            {
                if (_fallbackActive.Add(id))
                {
                    Console.Error.WriteLine($"[curve-engine] curve '{id}' source has no reading, flooring its fans at {MissingSourceDuty:0}%");
                }
            }
            _fallbackActive.RemoveWhere(id =>
            {
                if (fellBack.Contains(id))
                {
                    return false;
                }
                Console.Error.WriteLine($"[curve-engine] curve '{id}' source reading is back, resuming the curve");
                return true;
            });
        }
    }

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

        lock (_missingTicks)
        {
            foreach (var id in _missingTicks.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _missingTicks.Remove(id);
            }
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
    internal void ReplayManualDuties(NexusSettings settings, HashSet<string> present, HashSet<string> curveOwned, GuardPlan plan)
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
        int generation;
        lock (_manualReplayed) { generation = _manualGeneration; }
        var snapshot = new List<KeyValuePair<string, int>>(manual.Count);
        try
        {
            foreach (var kv in manual)
            {
                snapshot.Add(kv);
            }
        }
        catch (InvalidOperationException) { return; }
        ReplaySnapshotTaken?.Invoke();

        // Collect under the lock, write hardware outside it: SetFanSpeed
        // re-records the entry via IConfigStore.Update, whose OnChanged
        // handlers must not run while the replay lock is held.
        List<(string Id, int Duty)>? toApply = null;
        lock (_manualReplayed)
        {
            if (generation != _manualGeneration)
            {
                // An import rewrote duties after this snapshot; the next tick re-reads.
                return;
            }
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
                // A channel the guard raises, forces, hands back or stops writing
                // belongs to the guard pass; it stays armed and replays once the
                // override lifts (the trip end also re-arms every channel).
                if (plan.Apply(kv.Key, kv.Value) != kv.Value)
                {
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
                if (!owned.Contains(id) && !_guardOverriddenManual.Contains(id) && !_guardDrivenHardware.Contains(id))
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
