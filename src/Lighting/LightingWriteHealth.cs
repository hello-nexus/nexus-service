using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Nexus.Service.Lighting;

/// <summary>
/// Sustained hardware write failures per lighting owner key (a card id, a device id, or a hub id).
/// Drivers report from their existing failure paths; the OpenRGB poller reports the daemon's counter deltas.
/// A key flips to failing on a burst that dominates its successes and clears after a quiet spell.
/// </summary>
public sealed class LightingWriteHealth
{
    public const string Failing = "failing";

    /// <summary>Sliding window the failure rule counts over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>Failures inside <see cref="Window"/> needed to flag a key.</summary>
    public const int MinFailures = 5;

    /// <summary>Distinct reports carrying a failure needed to flag a key; one bad poll alone never flags.</summary>
    public const int MinFailureReports = 2;

    /// <summary>Failure reports must keep arriving past the sustain time, at least the minimum number of them, so a single instant burst never flags.</summary>
    public static readonly TimeSpan SustainedFor = TimeSpan.FromSeconds(15);

    /// <summary>A failure after this much quiet starts a new streak.</summary>
    public static readonly TimeSpan StreakGap = TimeSpan.FromSeconds(10);

    /// <summary>A flagged key clears once this long passes with no failure.</summary>
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(20);

    /// <summary>The instance drivers report into, which DI also serves; drivers are built outside the container in places.</summary>
    public static LightingWriteHealth Shared { get; } = new();

    private static readonly TimeSpan SuccessMerge = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Func<TimeSpan> _now;

    private static readonly long Origin = Stopwatch.GetTimestamp();

    /// <summary>Monotonic, so a wall-clock step cannot stretch or collapse the windows.</summary>
    public LightingWriteHealth() : this(() => Stopwatch.GetElapsedTime(Origin))
    {
    }

    public LightingWriteHealth(Func<TimeSpan> now)
    {
        _now = now;
    }

    /// <summary>Raised by <see cref="Sweep"/>, on the sweeping thread only, when a key's failing state flipped since the last sweep. Reporting threads never run handlers.</summary>
    public event Action? Changed;

    private int _dirty;

    private sealed class State
    {
        public readonly List<Sample> Samples = new();
        public TimeSpan LastFailure = Never;
        public TimeSpan StreakStart;
        public bool Failing;
        public string? HandlerId;
    }

    private static readonly TimeSpan Never = TimeSpan.FromDays(-1);

    private readonly record struct Sample(TimeSpan At, int Failures, int Successes);

    public void NoteFailure(string key, string? handlerId = null) => Report(key, 1, 0, handlerId);

    public void NoteSuccess(string key, string? handlerId = null) => Report(key, 0, 1, handlerId);

    /// <param name="handlerId">The Devices-page handler this key belongs to, matched exactly by <see cref="IsHandlerFailing"/>.</param>
    public void Report(string key, int failures, int successes, string? handlerId = null)
    {
        if (string.IsNullOrEmpty(key) || (failures <= 0 && successes <= 0))
        {
            return;
        }
        lock (_lock)
        {
            var now = _now();
            if (!_states.TryGetValue(key, out var st))
            {
                st = new State();
                _states[key] = st;
            }
            if (handlerId is not null)
            {
                st.HandlerId = handlerId;
            }
            var last = st.Samples.Count - 1;
            if (failures <= 0 && last >= 0 && st.Samples[last].Failures == 0 && now - st.Samples[last].At < SuccessMerge)
            {
                // Steady per-frame successes fold into one sample per merge interval.
                var merged = st.Samples[last];
                st.Samples[last] = merged with { Successes = merged.Successes + successes };
            }
            else
            {
                st.Samples.Add(new Sample(now, Math.Max(failures, 0), Math.Max(successes, 0)));
            }
            if (failures > 0)
            {
                if (now - st.LastFailure >= StreakGap)
                {
                    st.StreakStart = now;
                }
                st.LastFailure = now;
            }
            if ((failures > 0 || st.Failing) && Evaluate(st, now))
            {
                Volatile.Write(ref _dirty, 1);
            }
        }
    }

    /// <summary>Re-evaluates every key against the clock so a quiet key clears with no new report, then raises <see cref="Changed"/> if anything flipped since the last sweep.</summary>
    public void Sweep()
    {
        var flipped = false;
        lock (_lock)
        {
            var now = _now();
            List<string>? drop = null;
            foreach (var kv in _states)
            {
                flipped |= Evaluate(kv.Value, now);
                if (kv.Value.Samples.Count == 0 && !kv.Value.Failing)
                {
                    (drop ??= new List<string>()).Add(kv.Key);
                }
            }
            if (drop is not null)
            {
                foreach (var key in drop)
                {
                    _states.Remove(key);
                }
            }
        }
        if (flipped)
        {
            Volatile.Write(ref _dirty, 1);
        }
        if (Interlocked.Exchange(ref _dirty, 0) != 0)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Drops a key, for a device Nexus no longer controls or that left.</summary>
    public void Forget(string key)
    {
        lock (_lock)
        {
            if (_states.Remove(key, out var st) && st.Failing)
            {
                Volatile.Write(ref _dirty, 1);
            }
        }
    }

    public bool IsFailing(string key)
    {
        lock (_lock)
        {
            return _states.TryGetValue(key, out var st) && st.Failing;
        }
    }

    /// <summary>True when a failing key is the handler id itself or was reported under it.</summary>
    public bool IsHandlerFailing(string handlerId)
    {
        if (string.IsNullOrEmpty(handlerId))
        {
            return false;
        }
        lock (_lock)
        {
            foreach (var kv in _states)
            {
                if (kv.Value.Failing && (kv.Key == handlerId || kv.Value.HandlerId == handlerId))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Failing flag for a card, matched on its own id, its owning device id, its group id or its control handler id.</summary>
    public bool IsFailing(string? id, string? deviceId, string? parentDeviceId, string? controlHandlerId = null)
    {
        lock (_lock)
        {
            return Match(id) || Match(deviceId) || Match(parentDeviceId) || Match(controlHandlerId);
        }

        bool Match(string? key) => !string.IsNullOrEmpty(key) && _states.TryGetValue(key, out var st) && st.Failing;
    }

    /// <summary>Sets <see cref="Nexus.Service.Models.Devices.LightingDevice.WriteHealth"/> on the controlled cards whose key is failing; clears it elsewhere.</summary>
    public void Apply(IEnumerable<Nexus.Service.Models.Devices.LightingDevice> devices)
    {
        foreach (var d in devices)
        {
            d.WriteHealth = d.Controlled && IsFailing(d.Id, d.DeviceId, d.ParentDeviceId, d.ControlHandlerId) ? Failing : null;
        }
    }

    // Caller holds _lock. Returns true when the failing flag flipped.
    private static bool Evaluate(State st, TimeSpan now)
    {
        var cutoff = now - Window;
        st.Samples.RemoveAll(s => s.At < cutoff);
        var failures = 0;
        var successes = 0;
        var failureReports = 0;
        var lateReports = 0;
        var lateFrom = st.StreakStart + SustainedFor;
        foreach (var s in st.Samples)
        {
            failures += s.Failures;
            successes += s.Successes;
            if (s.Failures > 0)
            {
                failureReports++;
                if (s.At >= lateFrom)
                {
                    lateReports++;
                }
            }
        }
        var was = st.Failing;
        if (!was)
        {
            st.Failing = failures >= MinFailures && failureReports >= MinFailureReports && failures > successes
                && lateReports >= MinFailureReports;
        }
        else if (now - st.LastFailure >= ClearAfter)
        {
            // Samples go too: the window still holds the burst that flagged it, which would re-flag at once.
            st.Failing = false;
            st.Samples.Clear();
        }
        return st.Failing != was;
    }
}
