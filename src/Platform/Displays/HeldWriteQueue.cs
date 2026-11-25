using System;
using System.Collections.Generic;

namespace Nexus.Service.Platform.Displays;

/// <summary>A display write the DDC gate held back. A null <see cref="Code"/> is a brightness percent.</summary>
internal readonly record struct HeldWrite(string Id, byte? Code, int Value, int Attempts = 0);

/// <summary>Outcome of one <see cref="HeldWriteQueue.Replay"/>.</summary>
internal readonly record struct HeldReplayResult(int Applied, int Total, HeldIncoming Incoming);

/// <summary>What <see cref="HeldWriteQueue.Replay"/> did with the new write passed to it.</summary>
internal enum HeldIncoming
{
    /// <summary>Left for the caller to write directly.</summary>
    NotHandled,
    /// <summary>Written during the replay, in its predecessor's position.</summary>
    Written,
    /// <summary>Held, in order, behind an earlier failed write for the same display.</summary>
    Queued,
}

/// <summary>
/// Writes a closed DDC gate held back: the latest value per display and register, replayed in the order
/// first held so a Y70 Touch preset still lands before its gains. Not thread-safe; the owner serializes.
/// </summary>
internal sealed class HeldWriteQueue
{
    private readonly List<HeldWrite> _held = new();
    private readonly int _maxAttempts;

    public HeldWriteQueue(int maxAttempts) => _maxAttempts = maxAttempts;

    public int Count => _held.Count;

    /// <summary>Holds a write, replacing an earlier one for the same display and register in place.</summary>
    public void Hold(HeldWrite write)
    {
        var i = _held.FindIndex(h => h.Id == write.Id && h.Code == write.Code);
        if (i >= 0)
        {
            _held[i] = write;
        }
        else
        {
            _held.Add(write);
        }
    }

    /// <summary>
    /// Replays every held write in order through <paramref name="write"/>. Once an entry for a display fails, every
    /// later entry for that display stays held behind it, so each retry keeps that display's original order.
    /// <paramref name="countAttempt"/> is true only for the timer and gate-open replays: a failed head is then
    /// charged an attempt and dropped at the cap, letting the entries behind it proceed. A replay triggered by a
    /// new write passes false so a burst of writes cannot exhaust the attempts. <paramref name="incoming"/> is
    /// that new write: it takes its held predecessor's position, where it is written, or held if that write fails
    /// or the display is blocked at that point. With no predecessor it is appended only when the display ended up
    /// blocked; otherwise it is left for the caller (<see cref="HeldIncoming.NotHandled"/>).
    /// </summary>
    public HeldReplayResult Replay(Func<HeldWrite, bool> write, bool countAttempt, HeldWrite? incoming = null)
    {
        var writes = _held.ToArray();
        _held.Clear();
        var applied = 0;
        var state = HeldIncoming.NotHandled;
        var blocked = new HashSet<string>();
        foreach (var w in writes)
        {
            if (incoming is { } inc && w.Id == inc.Id && w.Code == inc.Code)
            {
                var entry = inc with { Attempts = 0 };
                if (!blocked.Contains(entry.Id) && write(entry))
                {
                    applied++;
                    state = HeldIncoming.Written;
                    continue;
                }
                blocked.Add(entry.Id);
                _held.Add(entry);
                state = HeldIncoming.Queued;
                continue;
            }
            if (blocked.Contains(w.Id))
            {
                _held.Add(w);
                continue;
            }
            if (write(w))
            {
                applied++;
                continue;
            }
            blocked.Add(w.Id);
            if (!countAttempt)
            {
                _held.Add(w);
            }
            else if (w.Attempts + 1 < _maxAttempts)
            {
                _held.Add(w with { Attempts = w.Attempts + 1 });
            }
            else
            {
                // Dropped at the cap: it no longer holds back the entries behind it.
                blocked.Remove(w.Id);
            }
        }
        if (incoming is { } last && state == HeldIncoming.NotHandled && blocked.Contains(last.Id))
        {
            _held.Add(last with { Attempts = 0 });
            state = HeldIncoming.Queued;
        }
        return new HeldReplayResult(applied, writes.Length, state);
    }
}
