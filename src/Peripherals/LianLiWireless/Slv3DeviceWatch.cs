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
    private const long EpisodeWindowMs = 60_000;
    private const int EpisodesPerWindow = 3;

    internal readonly record struct Summary(
        int Sent, int Confirmed, int Unconfirmed, long LatencyAvgMs, long LatencyMaxMs,
        int Reports, int IdxChanges, string LastReportedIdx, long SinceConfirmMs, long PendingMs, int StuckEvents, int Suppressed, int SendFailed);

    private sealed class Chain
    {
        public readonly string[] Ring = new string[SentRingSize];
        public readonly long[] RingMs = new long[SentRingSize];
        public int RingNext;
        public string Reported = "";
        public long LastConfirmMs;
        public long UnconfirmedSinceMs;
        public bool Stuck;
        public bool StuckSilent;
        public long EpisodeWindowStartMs;
        public int EpisodesInWindow, Suppressed, SendFailed, FailSinceConfirm;
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

    /// <summary>An upload carrying <paramref name="idxHex"/> was fully written to the TX.</summary>
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

    /// <summary>An upload to the chain failed before it was fully written.</summary>
    internal void NoteSendFailed(string mac)
    {
        lock (_gate)
        {
            var c = For(mac);
            c.SendFailed++;
            c.FailSinceConfirm++;
        }
    }

    /// <summary>Forgets every chain, for a link that went down.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _chains.Clear();
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
            var oldest = 0L;
            for (var i = 0; i < SentRingSize; i++)
            {
                if (c.Ring[i].Length > 0 && (oldest == 0 || c.RingMs[i] < oldest))
                {
                    oldest = c.RingMs[i];
                }
            }
            c.UnconfirmedSinceMs = oldest;
            c.FailSinceConfirm = 0;
            if (c.Stuck)
            {
                c.Stuck = false;
                if (c.StuckSilent)
                {
                    return;
                }
                log($"[lianli-wireless] chain {mac} RECOVERED at {Stamp()}: device echoed idx={idxHex} after {nowMs - c.StuckSinceMs} ms stuck");
            }
        }
    }

    /// <summary>Raises a stuck event for each chain whose oldest unconfirmed upload passed the threshold.</summary>
    internal void Check(long nowMs, Func<string, bool> isBound, Action<string> log)
    {
        lock (_gate)
        {
            foreach (var (mac, c) in _chains)
            {
                if (c.Stuck || c.UnconfirmedSinceMs == 0 || nowMs - c.UnconfirmedSinceMs < StuckAfterMs || !isBound(mac))
                {
                    continue;
                }
                c.Stuck = true;
                c.StuckSinceMs = c.UnconfirmedSinceMs;
                c.StuckEvents++;
                if (nowMs - c.EpisodeWindowStartMs > EpisodeWindowMs)
                {
                    c.EpisodeWindowStartMs = nowMs;
                    c.EpisodesInWindow = 0;
                }
                c.StuckSilent = ++c.EpisodesInWindow > EpisodesPerWindow;
                if (c.StuckSilent)
                {
                    c.Suppressed++;
                    continue;
                }
                log($"[lianli-wireless] chain {mac} STUCK at {Stamp()}: no device echo for {nowMs - c.UnconfirmedSinceMs} ms (uploads written={c.Sent} this period, write failures since last echo={c.FailSinceConfirm}, device idx={(c.Reported.Length > 0 ? c.Reported : "none")}, last confirmed {(c.LastConfirmMs == 0 ? "never" : $"{nowMs - c.LastConfirmMs} ms ago")}, reports={c.Reports})");
            }
        }
    }

    /// <summary>The chain left the list; a stuck state ends with it.</summary>
    internal void Remove(string mac, long nowMs, Action<string> log)
    {
        lock (_gate)
        {
            if (_chains.Remove(mac, out var c) && c.Stuck && !c.StuckSilent)
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
                c.StuckEvents, c.Suppressed, c.SendFailed);
            c.Sent = c.Confirmed = c.Unconfirmed = c.Reports = c.IdxChanges = c.StuckEvents = c.Suppressed = c.SendFailed = 0;
            c.LatencySum = c.LatencyMax = 0;
            return s;
        }
    }

    private static string Stamp() => DateTime.UtcNow.ToString("HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
