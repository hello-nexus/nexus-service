using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Diagnostics;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Power;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// Everything about ROG Astral cards that is not hardware access: per-card power
/// detector and Level Sense, the contract state, alerts and the shutdown countdown.
/// It is fed by the existing GPU read (<see cref="AstralGpuSupplement"/>, or the
/// dev-tools simulation) and never polls; its only timer exists while a countdown runs.
/// </summary>
public sealed class AstralMonitor
{
    public const string AlertKind = "astral";
    public const string CountdownKind = "astralCountdown";

    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CalibrationRecheck = TimeSpan.FromSeconds(5);

    private sealed class Card
    {
        public string Id = "";
        public string Name = "";
        public string? CalibrationKey;
        public bool Simulated;
        public readonly AstralPowerDetector Detector = new();
        public readonly float[] Amps = new float[AstralTelemetryParser.PinCount];
        public AstralLevelSense? Level;
        public long CalibratedAtMs;
        public long CountdownId;
        public DateTimeOffset LastFeed;
        public DateTimeOffset NextCalibrationCheck;
    }

    private readonly IConfigStore _store;
    private readonly ISystemPowerProvider _power;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, Card> _cards = new(StringComparer.Ordinal);
    private ITimer? _timer;
    private long _countdownSeq;

    /// <summary>Delivers a notice through the diagnostics alert path.</summary>
    internal Action<DiagnosticsAlertNotice>? AlertSink { get; set; }

    /// <summary>Starts the shutdown; false when it could not. Never reached for a simulated card.</summary>
    internal Func<string, bool> ShutdownSink { get; set; }

    public AstralMonitor(IConfigStore store, ISystemPowerProvider power) : this(store, power, TimeProvider.System)
    {
    }

    internal AstralMonitor(IConfigStore store, ISystemPowerProvider power, TimeProvider time)
    {
        _store = store;
        _power = power;
        _time = time;
        ShutdownSink = reason => OperatingSystem.IsWindows() && _power.ForceShutdown(reason);
    }

    /// <summary>Name of the first card read recently, or null; the Devices entry exists while this is set.</summary>
    public string? CardName()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            foreach (var c in _cards.Values)
            {
                if (now - c.LastFeed < FreshFor)
                {
                    return c.Name;
                }
            }
        }
        return null;
    }

    public void Feed(string id, string name, string? calibrationKey, ReadOnlySpan<float> amps, bool simulated = false)
    {
        var now = _time.GetUtcNow();
        AstralStep step;
        var settings = _store.Load().Astral;
        AstralAlertKind kind;
        DiagnosticsAlertNotice? countdown = null;
        lock (_lock)
        {
            if (!_cards.TryGetValue(id, out var card))
            {
                card = _cards[id] = new Card { Id = id };
            }
            (card.Name, card.CalibrationKey, card.Simulated, card.LastFeed) = (name, calibrationKey, simulated, now);
            amps[..card.Amps.Length].CopyTo(card.Amps);
            step = card.Detector.Update(card.Amps, settings, now);
            kind = step.Alert;
            if (step.CountdownStarted)
            {
                card.CountdownId = ++_countdownSeq;
                countdown = CountdownNotice(card, now);
            }
            if (step.CountdownStarted || step.CountdownCancelled)
            {
                Rearm(now);
            }
        }

        if (countdown is not null)
        {
            Send(countdown);
        }
        else if (kind != AstralAlertKind.None && settings.Alerts)
        {
            Send(AlertNotice(name, step));
        }
    }

    public void FeedAngles(string id, AstralAngles angles, AstralAngles? seed = null)
    {
        var now = _time.GetUtcNow();
        string? saveKey = null;
        AstralCalibration? save = null;
        lock (_lock)
        {
            if (!_cards.TryGetValue(id, out var card))
            {
                return;
            }
            if (card.Level is null)
            {
                card.Level = new AstralLevelSense();
                if (seed is { } s)
                {
                    card.Level.Load(s);
                    card.CalibratedAtMs = now.ToUnixTimeMilliseconds();
                }
                else
                {
                    ApplySavedCalibration(card, true);
                }
            }
            else if (!card.Level.IsCalibrating && card.CalibrationKey is not null && now >= card.NextCalibrationCheck)
            {
                card.NextCalibrationCheck = now + CalibrationRecheck;
                ApplySavedCalibration(card, false);
            }

            if (card.Level.Add(angles) is { } done)
            {
                card.CalibratedAtMs = now.ToUnixTimeMilliseconds();
                if (card.CalibrationKey is not null)
                {
                    saveKey = card.CalibrationKey;
                    save = new AstralCalibration { X = done.X, Y = done.Y, Z = done.Z, CalibratedAtMs = card.CalibratedAtMs };
                }
                ServiceLog.Info($"[astral] {card.Id} Level Sense calibrated: x={done.X:F2} y={done.Y:F2} z={done.Z:F2}");
            }
        }

        if (saveKey is not null)
        {
            _store.Update(s => s.AstralLevelSense[saveKey] = save!);
        }
    }

    // The settings file is the source of truth: a restore or import that changes the entry is picked up here.
    private void ApplySavedCalibration(Card card, bool initial)
    {
        AstralCalibration? saved = null;
        var key = card.CalibrationKey;
        if (key is not null)
        {
            _store.Read(s => s.AstralLevelSense.TryGetValue(key, out saved));
        }
        if (saved is { } c)
        {
            if (initial || c.CalibratedAtMs != card.CalibratedAtMs)
            {
                card.Level!.Load(new AstralAngles(c.X, c.Y, c.Z));
                card.CalibratedAtMs = c.CalibratedAtMs;
            }
        }
        else if (initial || card.CalibratedAtMs != 0)
        {
            card.CalibratedAtMs = 0;
            card.Level!.StartCalibration();
        }
    }

    public void AppendLevelSensors(string id, string name, List<HardwareSensor> result)
    {
        lock (_lock)
        {
            if (_cards.TryGetValue(id, out var card) && card.Level is { Deviation: { } d, Posture: { } p })
            {
                AstralSensorBuilder.AppendLevel(id, name, d.X, d.Y, p.Sag, p.Lean, result);
            }
        }
    }

    // Caller holds _lock. Only an overcurrent countdown carries a cancel action.
    private static DiagnosticsAlertNotice CountdownNotice(Card card, DateTimeOffset now)
    {
        var d = card.Detector;
        var seconds = Math.Max(0, (int)Math.Round((d.EndsUtc - now).TotalSeconds));
        return d.Critical
            ? new DiagnosticsAlertNotice(
                "ROG Astral shutdown", $"{card.Name}: shutting down in {seconds} s because a pin reached its critical current.", CountdownKind)
            : new DiagnosticsAlertNotice(
                "ROG Astral shutdown", $"{card.Name}: shutting down in {seconds} s because a pin stayed over its current limit. Cancel to stop it.",
                CountdownKind, card.CountdownId);
    }

    /// <summary>The notice for the countdown that ends first, or null; resent when a helper connects mid-countdown.</summary>
    public DiagnosticsAlertNotice? CurrentCountdownNotice()
    {
        lock (_lock)
        {
            Card? soonest = null;
            foreach (var card in _cards.Values)
            {
                var d = card.Detector;
                if (d.Running && !d.ShutdownIssued && (soonest is null || d.EndsUtc < soonest.Detector.EndsUtc))
                {
                    soonest = card;
                }
            }
            return soonest is null ? null : CountdownNotice(soonest, _time.GetUtcNow());
        }
    }

    /// <summary>Cancels the countdown a notice's Cancel button was made for; a button from an earlier countdown does nothing.</summary>
    public void CancelCountdown(long id)
    {
        lock (_lock)
        {
            var any = false;
            foreach (var card in _cards.Values)
            {
                any |= card.CountdownId == id && card.Detector.Cancel();
            }
            if (any)
            {
                Rearm(_time.GetUtcNow());
            }
        }
    }

    /// <summary>Set by the dev-tools simulation to add its card to every GPU list.</summary>
    internal Action<List<GpuReadout>>? SimulatedGpus { get; set; }

    public void AppendSimulatedGpus(List<GpuReadout> gpus) => SimulatedGpus?.Invoke(gpus);

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_cards.Remove(id))
            {
                Rearm(_time.GetUtcNow());
            }
        }
    }

    /// <summary>Restarts calibration on one card, or on every card with a G-sensor when <paramref name="gpuId"/> is null.</summary>
    public void Recalibrate(string? gpuId)
    {
        lock (_lock)
        {
            foreach (var card in _cards.Values)
            {
                if (card.Level is { } level && (gpuId is null || card.Id == gpuId))
                {
                    level.StartCalibration();
                }
            }
        }
    }

    public AstralStateDto Patch(AstralSettingsPatch patch)
    {
        _store.Update(s =>
        {
            var a = s.Astral;
            a.Alerts = patch.Alerts ?? a.Alerts;
            a.Equalizer = patch.Equalizer ?? a.Equalizer;
            a.OvercurrentShutdown = patch.OvercurrentShutdown ?? a.OvercurrentShutdown;
            a.CriticalShutdown = patch.CriticalShutdown ?? a.CriticalShutdown;
            a.OvercurrentSeconds = AstralPowerDetector.ClampOvercurrentSeconds(patch.OvercurrentSeconds ?? a.OvercurrentSeconds);
            a.CountdownSeconds = AstralPowerDetector.ClampCountdownSeconds(patch.CountdownSeconds ?? a.CountdownSeconds);
        });
        return GetState();
    }

    public AstralStateDto Cancel()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var any = false;
            foreach (var card in _cards.Values)
            {
                any |= card.Detector.Cancel();
            }
            if (any)
            {
                Rearm(now);
            }
        }
        return GetState();
    }

    public AstralStateDto ShutdownNow()
    {
        List<Card>? due = null;
        lock (_lock)
        {
            foreach (var card in _cards.Values)
            {
                if (card.Detector.ShutdownNow())
                {
                    (due ??= new()).Add(card);
                }
            }
        }
        due?.ForEach(Shutdown);
        return GetState();
    }

    /// <summary>Runs the shutdown for every countdown that has run out; the countdown timer calls this.</summary>
    internal void ExpireIfDue()
    {
        var now = _time.GetUtcNow();
        List<Card>? due = null;
        lock (_lock)
        {
            foreach (var card in _cards.Values)
            {
                if (card.Detector.TakeExpired(now))
                {
                    (due ??= new()).Add(card);
                }
            }
            Rearm(now);
        }
        due?.ForEach(Shutdown);
    }

    private void Shutdown(Card card)
    {
        string reason;
        lock (_lock)
        {
            reason = $"Nexus: {card.Name} 12V-2x6 connector {(card.Detector.Critical ? "critical" : "overcurrent")} protection";
        }
        var ok = true;
        if (card.Simulated)
        {
            ServiceLog.Info($"[astral] simulated shutdown, nothing done: {reason}");
        }
        else
        {
            ServiceLog.Warn($"[astral] shutting down: {reason}");
            ok = ShutdownSink(reason);
        }
        if (!ok || card.Simulated)
        {
            lock (_lock)
            {
                card.Detector.ShutdownFailed();
            }
            if (!ok)
            {
                Send(new DiagnosticsAlertNotice("ROG Astral shutdown failed", $"{card.Name}: the PC could not be shut down. Check the connector.", AlertKind));
            }
        }
    }

    // Caller holds _lock. One timer for the soonest running countdown; none when nothing counts down.
    private void Rearm(DateTimeOffset now)
    {
        DateTimeOffset? soonest = null;
        foreach (var card in _cards.Values)
        {
            var d = card.Detector;
            if (d.Running && !d.ShutdownIssued && (soonest is null || d.EndsUtc < soonest))
            {
                soonest = d.EndsUtc;
            }
        }
        _timer?.Dispose();
        _timer = soonest is { } ends
            ? _time.CreateTimer(_ => ExpireIfDue(), null, ends > now ? ends - now : TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            : null;
    }

    public AstralStateDto GetState()
    {
        var settings = _store.Load().Astral;
        var state = new AstralStateDto
        {
            Settings = new AstralSettings
            {
                Alerts = settings.Alerts,
                Equalizer = settings.Equalizer,
                OvercurrentShutdown = settings.OvercurrentShutdown,
                OvercurrentSeconds = settings.OvercurrentSeconds,
                CountdownSeconds = settings.CountdownSeconds,
                CriticalShutdown = settings.CriticalShutdown,
            },
        };
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var card in _cards.Values)
            {
                if (now - card.LastFeed >= FreshFor)
                {
                    card.Detector.Clear();
                    continue;
                }
                state.Simulated |= card.Simulated;
                state.HeldOff |= card.Detector.HeldOff;
                if (card.Detector.Running && !card.Detector.ShutdownIssued
                    && (state.Countdown is null || card.Detector.EndsUtc.ToUnixTimeMilliseconds() < state.Countdown.EndsAtMs))
                {
                    state.Countdown = new AstralCountdownDto
                    {
                        Reason = card.Detector.Critical ? "critical" : "overcurrent",
                        EndsAtMs = card.Detector.EndsUtc.ToUnixTimeMilliseconds(),
                    };
                }
                state.Cards.Add(ToDto(card, settings.Equalizer));
            }
        }
        state.Supported = OperatingSystem.IsWindows() || state.Simulated;
        return state;
    }

    private static AstralCardDto ToDto(Card card, bool equalizer)
    {
        var max = 0f;
        foreach (var a in card.Amps)
        {
            max = Math.Max(max, a);
        }
        var dto = new AstralCardDto
        {
            GpuId = card.Id,
            Name = card.Name,
            Level = new AstralLevelDto
            {
                Available = card.Level is not null,
                Calibrating = card.Level?.IsCalibrating ?? false,
                CalibratedAtMs = card.CalibratedAtMs == 0 ? null : card.CalibratedAtMs,
            },
        };
        var worst = AstralPinState.Ok;
        foreach (var a in card.Amps)
        {
            var s = AstralPowerDetector.Classify(a, max, equalizer);
            worst = s > worst ? s : worst;
            dto.Pins.Add(new AstralPinDto { Amps = a, State = s.ToString().ToLowerInvariant() });
        }
        dto.State = worst.ToString().ToLowerInvariant();
        return dto;
    }

    private static DiagnosticsAlertNotice AlertNotice(string name, AstralStep s) => s.Alert switch
    {
        AstralAlertKind.Critical => new("ROG Astral critical current", $"{name}: pin {s.Pin + 1} at {s.Amps:F1} A, past the {s.LimitAmps:F1} A critical limit.", AlertKind),
        AstralAlertKind.Overcurrent => new("ROG Astral overcurrent", $"{name}: pin {s.Pin + 1} at {s.Amps:F1} A, over the {s.LimitAmps:F1} A limit.", AlertKind),
        AstralAlertKind.Dead => new("ROG Astral dead pin", $"{name}: pin {s.Pin + 1} carries no current while the others do. Reseat the cable.", AlertKind),
        _ => new("ROG Astral high pin current", $"{name}: pin {s.Pin + 1} at {s.Amps:F1} A, over its {s.LimitAmps:F1} A rating.", AlertKind),
    };

    private void Send(DiagnosticsAlertNotice notice)
    {
        try
        {
            AlertSink?.Invoke(notice);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[astral] alert failed: {ex.Message}");
        }
    }
}
