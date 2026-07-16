using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    private int _intervalMs = 1000;

    public CurveEngine(
        IFanControlProvider fans,
        IConfigStore store,
        MultiplexHub hub)
    {
        _fans = fans;
        _store = store;
        _hub = hub;
    }

    public int GetInterval() => _intervalMs;
    public void SetInterval(int ms) => _intervalMs = Math.Max(500, ms);

    public void ResetSmoothing()
    {
        lock (_lastSpeed) { _lastSpeed.Clear(); }
        lock (_lastWrite) { _lastWrite.Clear(); }
        // Re-arm the manual replay so the incoming profile's saved duties are
        // applied on the next tick (the profile switch released all fans).
        lock (_manualReplayed) { _manualReplayed.Clear(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let hardware init finish before starting curve evaluation
        await Task.Delay(3000, stoppingToken);

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

            try
            { await Task.Delay(_intervalMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            _fans.ReleaseAll();
            Console.Error.WriteLine("[curve-engine] shutdown - all fans released to BIOS");
        }
        catch { /* swallow */ }
    }

    internal void Tick()
    {
        var settings = _store.Load();
        var curves = settings.Cooling.Curves;
        if (curves.Count == 0 && settings.Cooling.ManualSpeeds.Count == 0)
        {
            // Idle cooling config: skip the per-tick channel enumeration
            // (on Windows it costs an LHM update + re-discovery).
            ForgetWritesNotOwned(null);
            return;
        }

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
        foreach (var ch in _fans.GetFanChannels())
        {
            present.Add(ch.Id);
        }

        ReplayManualDuties(settings, present, owned);

        if (curves.Count == 0)
        {
            return;
        }

        var globalMod = settings.Cooling.GlobalSpeedModifier;
        var calculations = new List<CurveCalculation>();
        var drivenChannels = new HashSet<string>();

        foreach (var curveDoc in curves)
        {
            var temp = _fans.ReadTemperature(curveDoc.Input.Id);
            if (temp is null)
            {
                continue;
            }

            double? rawSpeed = curveDoc.Type switch
            {
                "Flat" => EvaluateFlat(curveDoc.Flat),
                "Linear" => EvaluateLinear(curveDoc.Linear, temp.Value),
                "Graph" => EvaluateGraph(curveDoc.Graph, temp.Value),
                _ => null,
            };
            if (rawSpeed is null)
            {
                continue;
            }

            var modifiedSpeed = Math.Clamp(rawSpeed.Value * globalMod, 0, 100);

            var responseTime = GetResponseTime(curveDoc);
            var smoothedSpeed = ApplySmoothing(curveDoc.Id, modifiedSpeed, responseTime);

            // Apply to all output channels. Dedup against the last duty we
            // wrote to that channel and rate-limit per-channel writes so we
            // don't push redundant PWM commands at the firmware.
            var outputStates = new List<CurveOutputState>();
            var nowMs = Environment.TickCount64;
            foreach (var output in curveDoc.Outputs)
            {
                var appliedSpeed = (int)Math.Round(smoothedSpeed);
                if (present.Contains(output.Id))
                {
                    if (TryReserveWrite(output.Id, appliedSpeed, nowMs))
                    {
                        _fans.DriveFanSpeed(output.Id, appliedSpeed);
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
                InputTemperature = temp.Value,
                CalculatedSpeed = modifiedSpeed,
                ActualSpeed = smoothedSpeed,
                Outputs = outputStates,
            });
        }

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

    private static double GetResponseTime(CurveDocument doc)
    {
        return doc.Type switch
        {
            "Linear" => doc.Linear?.ResponseTime ?? 1.0,
            "Graph" => doc.Graph?.ResponseTime ?? 1.0,
            "Mixed" => doc.Mixed?.ResponseTime ?? 1.0,
            _ => 1.0,
        };
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
            if (owned is null || owned.Count == 0)
            {
                _lastWrite.Clear();
                return;
            }
            List<string>? stale = null;
            foreach (var id in _lastWrite.Keys)
            {
                if (!owned.Contains(id))
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
