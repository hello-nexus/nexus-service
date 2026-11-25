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
    /// <summary>Held, in order, behind an earlier failed write for the same display; nothing was attempted.</summary>
    Queued,
    /// <summary>Attempted in its predecessor's position and failed; held there for retry. The caller reports the failure.</summary>
    HeldAfterFailure,
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

        // Failure policy for a replayed (not incoming) entry: charged an attempt only on counted replays, dropped at the cap.
        void HoldFailed(HeldWrite entry)
        {
            if (!countAttempt)
            {
                _held.Add(entry);
            }
            else if (entry.Attempts + 1 < _maxAttempts)
            {
                _held.Add(entry with { Attempts = entry.Attempts + 1 });
            }
            else
            {
                // Dropped at the cap: it no longer holds back the entries behind it.
                blocked.Remove(entry.Id);
            }
        }

        for (var i = 0; i < writes.Length; i++)
        {
            var w = writes[i];
            var isIncoming = incoming is { } inc && w.Id == inc.Id && w.Code == inc.Code;
            var entry = isIncoming ? incoming!.Value with { Attempts = 0 } : w;
            if (blocked.Contains(entry.Id))
            {
                _held.Add(entry);
                if (isIncoming)
                {
                    state = HeldIncoming.Queued;
                }
                continue;
            }
            bool ok;
            try
            {
                ok = write(entry);
            }
            catch
            {
                // A throwing write must not lose the rest of the queue: the current entry counts as failed and
                // everything not yet processed stays held, with the new write in its predecessor's slot.
                if (isIncoming)
                {
                    _held.Add(entry);
                }
                else
                {
                    blocked.Add(entry.Id);
                    HoldFailed(entry);
                }
                var placed = isIncoming || state != HeldIncoming.NotHandled;
                for (var j = i + 1; j < writes.Length; j++)
                {
                    if (!placed && incoming is { } inc2 && writes[j].Id == inc2.Id && writes[j].Code == inc2.Code)
                    {
                        _held.Add(inc2 with { Attempts = 0 });
                        placed = true;
                    }
                    else
                    {
                        _held.Add(writes[j]);
                    }
                }
                if (!placed && incoming is { } pending)
                {
                    _held.Add(pending with { Attempts = 0 });
                }
                throw;
            }
            if (ok)
            {
                applied++;
                if (isIncoming)
                {
                    state = HeldIncoming.Written;
                }
                continue;
            }
            blocked.Add(entry.Id);
            if (isIncoming)
            {
                _held.Add(entry);
                state = HeldIncoming.HeldAfterFailure;
            }
            else
            {
                HoldFailed(entry);
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
