using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Diagnostic only. Pairs each effect index we upload with the effect index the chain
/// reports back in its device-list record, so a chain that stops echoing shows up even
/// while every USB write succeeds. Nothing reads this to decide behavior.
/// </summary>
internal sealed class Slv3DeviceWatch
{
    /// <summary>Unconfirmed this long reads as a stuck chain.</summary>
    internal const long StuckAfterMs = 3_000;

    private const int SentRingSize = 6;

    internal readonly record struct Summary(
        int Sent, int Confirmed, int Unconfirmed, long LatencyAvgMs, long LatencyMaxMs,
        int Reports, int IdxChanges, string LastReportedIdx, long SinceConfirmMs, long PendingMs, int StuckEvents);

    private sealed class Chain
    {
        public readonly string[] Ring = new string[SentRingSize];
        public readonly long[] RingMs = new long[SentRingSize];
        public int RingNext;
        public string Reported = "";
        public long LastConfirmMs;
        public long UnconfirmedSinceMs;
        public bool Stuck;
        public long StuckSinceMs;
        public int Sent, Confirmed, Unconfirmed, Reports, IdxChanges, StuckEvents;
        public long LatencySum, LatencyMax;
    }

    private readonly Dictionary<string, Chain> _chains = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private Chain For(string mac)
    {
        if (!_chains.TryGetValue(mac, out var c))
        {
            c = new Chain();
            Array.Fill(c.Ring, "");
            _chains[mac] = c;
        }
        return c;
    }

    /// <summary>An upload carrying <paramref name="idxHex"/> was handed to the radio.</summary>
    internal void NoteSent(string mac, string idxHex, long nowMs)
    {
        lock (_gate)
        {
            var c = For(mac);
            c.Sent++;
            if (c.UnconfirmedSinceMs == 0)
            {
                c.UnconfirmedSinceMs = nowMs;
            }
            c.Ring[c.RingNext] = idxHex;
            c.RingMs[c.RingNext] = nowMs;
            c.RingNext = (c.RingNext + 1) % SentRingSize;
        }
    }

    /// <summary>The chain's record in a device-list reply reported <paramref name="idxHex"/>.</summary>
    internal void NoteReported(string mac, string idxHex, long nowMs, Action<string> log)
    {
        lock (_gate)
        {
            var c = For(mac);
            c.Reports++;
            if (string.Equals(c.Reported, idxHex, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            c.Reported = idxHex;
            c.IdxChanges++;
            var slot = -1;
            for (var i = 0; i < SentRingSize; i++)
            {
                if (c.Ring[i].Length > 0 && string.Equals(c.Ring[i], idxHex, StringComparison.OrdinalIgnoreCase))
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
            {
                return;
            }
            var latency = nowMs - c.RingMs[slot];
            c.Confirmed++;
            c.LatencySum += latency;
            c.LatencyMax = Math.Max(c.LatencyMax, latency);
            // Uploads older than the confirmed one were overtaken, so they never will.
            for (var i = 0; i < SentRingSize; i++)
            {
                if (c.Ring[i].Length > 0 && c.RingMs[i] <= c.RingMs[slot])
                {
                    if (i != slot)
                    {
                        c.Unconfirmed++;
                    }
                    c.Ring[i] = "";
                }
            }
            c.LastConfirmMs = nowMs;
            var pending = false;
            for (var i = 0; i < SentRingSize; i++)
            {
                pending |= c.Ring[i].Length > 0;
            }
            c.UnconfirmedSinceMs = pending ? nowMs : 0;
            if (c.Stuck)
            {
                c.Stuck = false;
                log($"[lianli-wireless] chain {mac} RECOVERED at {Stamp()}: device echoed idx={idxHex} after {nowMs - c.StuckSinceMs} ms stuck");
            }
        }
    }

    /// <summary>Raises a stuck event for each chain whose oldest unconfirmed upload passed the threshold.</summary>
    internal void Check(long nowMs, Action<string> log)
    {
        lock (_gate)
        {
            foreach (var (mac, c) in _chains)
            {
                if (c.Stuck || c.UnconfirmedSinceMs == 0 || nowMs - c.UnconfirmedSinceMs < StuckAfterMs)
                {
                    continue;
                }
                c.Stuck = true;
                c.StuckSinceMs = c.UnconfirmedSinceMs;
                c.StuckEvents++;
                log($"[lianli-wireless] chain {mac} STUCK at {Stamp()}: no device echo for {nowMs - c.UnconfirmedSinceMs} ms (uploads ok, device idx={(c.Reported.Length > 0 ? c.Reported : "none")}, last confirmed {(c.LastConfirmMs == 0 ? "never" : $"{nowMs - c.LastConfirmMs} ms ago")}, reports={c.Reports})");
            }
        }
    }

    /// <summary>The chain left the list; a stuck state ends with it.</summary>
    internal void Remove(string mac, long nowMs, Action<string> log)
    {
        lock (_gate)
        {
            if (_chains.Remove(mac, out var c) && c.Stuck)
            {
                log($"[lianli-wireless] chain {mac} STUCK ended at {Stamp()}: chain dropped after {nowMs - c.StuckSinceMs} ms stuck");
            }
        }
    }

    /// <summary>Counters since the last call, then reset. Null for a chain never seen.</summary>
    internal Summary? Take(string mac, long nowMs)
    {
        lock (_gate)
        {
            if (!_chains.TryGetValue(mac, out var c))
            {
                return null;
            }
            var s = new Summary(
                c.Sent, c.Confirmed, c.Unconfirmed,
                c.Confirmed > 0 ? c.LatencySum / c.Confirmed : 0, c.LatencyMax,
                c.Reports, c.IdxChanges, c.Reported,
                c.LastConfirmMs == 0 ? -1 : nowMs - c.LastConfirmMs,
                c.UnconfirmedSinceMs == 0 ? 0 : nowMs - c.UnconfirmedSinceMs,
                c.StuckEvents);
            c.Sent = c.Confirmed = c.Unconfirmed = c.Reports = c.IdxChanges = c.StuckEvents = 0;
            c.LatencySum = c.LatencyMax = 0;
            return s;
        }
    }

    private static string Stamp() => DateTime.UtcNow.ToString("HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
