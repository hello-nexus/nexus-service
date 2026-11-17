using System;
using System.Collections.Generic;

namespace Nexus.Service.Cooling;

/// <summary>Channels whose next write first hands the header back to its default, so the driver re-asserts
/// software mode. LHM writes the mode register only on the transition, so a duty write alone cannot undo a
/// firmware reset that happened during sleep. A channel stays armed until a write to it succeeds.</summary>
internal sealed class ResumeRetakeTracker
{
    private readonly HashSet<string> _armed = new(StringComparer.Ordinal);

    internal void Arm(IEnumerable<string> ids)
    {
        lock (_armed)
        {
            foreach (var id in ids)
            {
                _armed.Add(id);
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
        lock (_armed) { return _armed.Contains(id); }
    }

    /// <summary>Runs one duty write. An armed channel is first handed back to default unless the driver takes the
    /// channel over by itself on every set. The channel is disarmed only after the write succeeds; a throw or a
    /// false result keeps it armed for the next attempt.</summary>
    internal bool Write(string id, bool takesOverItself, Action writeDefault, Func<bool> writeDuty)
    {
        var armed = IsArmed(id);
        if (armed && !takesOverItself)
        {
            writeDefault();
        }
        var ok = writeDuty();
        if (ok && armed)
        {
            Forget(id);
        }
        return ok;
    }
}
