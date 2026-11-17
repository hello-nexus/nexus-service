using System;
using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>Channels whose next write first hands the header back to its default, so the driver re-asserts
/// software mode. LHM writes the mode register only on the transition, so a duty write alone cannot undo a
/// firmware reset that happened during sleep. A channel stays armed until a write to it succeeds.</summary>
internal sealed class ResumeRetakeTracker
{
    /// <summary>Failed hand-backs after which a channel stops retrying; its duty writes carry on either way.</summary>
    internal const int MaxDefaultFailures = 3;

    private sealed class ArmState
    {
        public long Generation;
        public int Failures;
    }

    private readonly Dictionary<string, ArmState> _armed = new(StringComparer.Ordinal);
    private long _generation;

    internal void Arm(IEnumerable<string> ids)
    {
        lock (_armed)
        {
            foreach (var id in ids)
            {
                _armed[id] = new ArmState { Generation = ++_generation };
            }
        }
    }

    internal void Forget(string id)
    {
        lock (_armed) { _armed.Remove(id); }
    }

    internal void Clear()
    {
        lock (_armed) { _armed.Clear(); }
    }

    internal bool IsArmed(string id)
    {
        lock (_armed) { return _armed.ContainsKey(id); }
    }

    /// <summary>Runs one duty write. An armed channel is first handed back to default unless the driver takes the
    /// channel over by itself on every set. The channel is disarmed only after the hand-back and the duty write
    /// both succeeded, and only if it was not armed again meanwhile. A failing hand-back never blocks the duty
    /// write: the channel stays armed and retries, up to <see cref="MaxDefaultFailures"/> times.</summary>
    internal bool Write(string id, bool takesOverItself, Action writeDefault, Func<bool> writeDuty)
    {
        ArmState? taken;
        long generation;
        lock (_armed)
        {
            _armed.TryGetValue(id, out taken);
            generation = taken?.Generation ?? 0;
        }
        var handedBack = true;
        if (taken is not null && !takesOverItself)
        {
            try
            {
                writeDefault();
            }
            catch (Exception ex)
            {
                handedBack = false;
                RecordFailure(id, taken, generation, ex);
            }
        }
        var ok = writeDuty();
        if (ok && taken is not null && handedBack)
        {
            lock (_armed)
            {
                if (_armed.TryGetValue(id, out var now) && now.Generation == generation)
                {
                    _armed.Remove(id);
                }
            }
        }
        return ok;
    }

    private void RecordFailure(string id, ArmState taken, long generation, Exception ex)
    {
        bool gaveUp;
        lock (_armed)
        {
            taken.Failures++;
            gaveUp = taken.Failures >= MaxDefaultFailures;
            if (gaveUp && _armed.TryGetValue(id, out var now) && now.Generation == generation)
            {
                _armed.Remove(id);
            }
        }
        ServiceLog.Warn($"[fan-control] {id}: resume hand-back to default failed ({ex.Message}){(gaveUp ? "; giving up, duty writes continue" : "")}");
    }
}
