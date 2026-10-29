using System;
using System.Collections.Generic;

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

    /// <summary>A flagged key clears once this long passes with no failure.</summary>
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(20);

    /// <summary>The instance drivers report into, which DI also serves; drivers are built outside the container in places.</summary>
    public static LightingWriteHealth Shared { get; } = new();

    private static readonly TimeSpan SuccessMerge = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now;

    public LightingWriteHealth() : this(() => DateTime.UtcNow)
    {
    }

    public LightingWriteHealth(Func<DateTime> now)
    {
        _now = now;
    }

    /// <summary>Raised after a key's failing state flips, outside the lock.</summary>
    public event Action? Changed;

    private sealed class State
    {
        public readonly List<Sample> Samples = new();
        public DateTime LastFailure;
        public bool Failing;
    }

    private readonly record struct Sample(DateTime At, int Failures, int Successes);

    public void NoteFailure(string key) => Report(key, 1, 0);

    public void NoteSuccess(string key) => Report(key, 0, 1);

    public void Report(string key, int failures, int successes)
    {
        if (string.IsNullOrEmpty(key) || (failures <= 0 && successes <= 0))
        {
            return;
        }
        var flipped = false;
        lock (_lock)
        {
            var now = _now();
            if (!_states.TryGetValue(key, out var st))
            {
                st = new State();
                _states[key] = st;
            }
            var last = st.Samples.Count - 1;
            if (failures <= 0 && last >= 0 && st.Samples[last].Failures == 0 && now - st.Samples[last].At < SuccessMerge)
            {
                // Steady per-frame successes fold into one sample a second.
                var merged = st.Samples[last];
                st.Samples[last] = merged with { Successes = merged.Successes + successes };
            }
            else
            {
                st.Samples.Add(new Sample(now, Math.Max(failures, 0), Math.Max(successes, 0)));
            }
            if (failures > 0)
            {
                st.LastFailure = now;
            }
            if (failures > 0 || st.Failing)
            {
                flipped = Evaluate(st, now);
            }
        }
        if (flipped)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Re-evaluates every key against the clock so a quiet key clears with no new report.</summary>
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
            Changed?.Invoke();
        }
    }

    /// <summary>Drops a key, for a device Nexus no longer controls or that left.</summary>
    public void Forget(string key)
    {
        bool flipped;
        lock (_lock)
        {
            flipped = _states.Remove(key, out var st) && st.Failing;
        }
        if (flipped)
        {
            Changed?.Invoke();
        }
    }

    public bool IsFailing(string key)
    {
        lock (_lock)
        {
            return _states.TryGetValue(key, out var st) && st.Failing;
        }
    }

    /// <summary>True when a key equal to the handler id, or "handlerId:" followed by a serial, is failing.</summary>
    public bool IsHandlerFailing(string handlerId)
    {
        if (string.IsNullOrEmpty(handlerId))
        {
            return false;
        }
        var prefix = handlerId + ":";
        lock (_lock)
        {
            foreach (var kv in _states)
            {
                if (kv.Value.Failing && (kv.Key == handlerId || kv.Key.StartsWith(prefix, StringComparison.Ordinal)))
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
    private static bool Evaluate(State st, DateTime now)
    {
        var cutoff = now - Window;
        st.Samples.RemoveAll(s => s.At < cutoff);
        var failures = 0;
        var successes = 0;
        var failureReports = 0;
        foreach (var s in st.Samples)
        {
            failures += s.Failures;
            successes += s.Successes;
            if (s.Failures > 0)
            {
                failureReports++;
            }
        }
        var was = st.Failing;
        if (!was)
        {
            st.Failing = failures >= MinFailures && failureReports >= MinFailureReports && failures > successes;
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
