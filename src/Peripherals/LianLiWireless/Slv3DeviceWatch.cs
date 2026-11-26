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

    /// <summary>Unconfirmed this long opens a gap line, well below the stuck threshold.</summary>
    internal const long GapAfterMs = 1_000;

    private const long LineWindowMs = 60_000;
    private const int GapLinesPerWindow = 20;
    // Anomalies only (never echoed, overtaken, slow); the rest of the echo record rolls into the per-chain minute line.
    private const int EchoLinesPerWindow = 20;
    /// <summary>An echo later than this gets its own line.</summary>
    internal const long SlowEchoMs = 1_000;

    internal readonly record struct Summary(
        int Sent, int Confirmed, int Unconfirmed, long LatencyAvgMs, long LatencyMaxMs,
        int Reports, int IdxChanges, string LastReportedIdx, long SinceConfirmMs, long PendingMs, int StuckEvents, int Suppressed, int SendFailed);

    private sealed class Chain
    {
        public readonly string[] Ring = new string[SentRingSize];
        public readonly long[] RingMs = new long[SentRingSize];
        public readonly bool[] RingLive = new bool[SentRingSize];
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
        public string OldestIdx = "";
        public bool GapOpen;
        public long GapStartMs;
        public string GapStartIdx = "";
        public int GapSent, GapReports, GapFails;
        public long LineWindowStartMs;
        public int GapLines, GapSuppressed, EchoLines, EchoSuppressed;
        public long RevertWindowStartMs;
        public int RevertLines;
    }

    private const int RevertLinesPerWindow = 10;

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
    internal void NoteSent(string mac, string idxHex, long nowMs, Action<string>? log = null, bool live = false)
    {
        lock (_gate)
        {
            var c = For(mac);
            c.Sent++;
            if (c.GapOpen)
            {
                c.GapSent++;
            }
            if (c.UnconfirmedSinceMs == 0)
            {
                c.UnconfirmedSinceMs = nowMs;
                c.OldestIdx = idxHex;
            }
            if (log is not null && c.Ring[c.RingNext].Length > 0 && !c.RingLive[c.RingNext])
            {
                // The slot is reused, so this upload was never echoed.
                LogEcho(c, mac, "never", c.Ring[c.RingNext], c.RingMs[c.RingNext], nowMs, log);
            }
            c.Ring[c.RingNext] = idxHex;
            c.RingMs[c.RingNext] = nowMs;
            c.RingLive[c.RingNext] = live;
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
            if (c.GapOpen)
            {
                c.GapFails++;
            }
        }
    }

    /// <summary>Forgets every chain, for a link that went down.</summary>
    internal void Clear(long nowMs = 0, Action<string>? log = null)
    {
        lock (_gate)
        {
            if (log is not null)
            {
                foreach (var (mac, c) in _chains)
                {
                    EndGap(c, mac, nowMs, "link", "", log);
                }
            }
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
            if (c.GapOpen)
            {
                c.GapReports++;
            }
            if (string.Equals(c.Reported, idxHex, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var previous = c.Reported;
            c.Reported = idxHex;
            c.IdxChanges++;
            LogRevert(c, mac, previous, idxHex, nowMs, log);
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
            LogEcho(c, mac, "ok", c.Ring[slot], c.RingMs[slot], nowMs, log, latency);
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
                        if (!c.RingLive[i]) LogEcho(c, mac, "superseded", c.Ring[i], c.RingMs[i], nowMs, log);
                    }
                    c.Ring[i] = "";
                }
            }
            c.LastConfirmMs = nowMs;
            var oldest = 0L;
            var oldestIdx = "";
            for (var i = 0; i < SentRingSize; i++)
            {
                if (c.Ring[i].Length > 0 && (oldest == 0 || c.RingMs[i] < oldest))
                {
                    oldest = c.RingMs[i];
                    oldestIdx = c.Ring[i];
                }
            }
            EndGap(c, mac, nowMs, "echo", idxHex, log);
            c.UnconfirmedSinceMs = oldest;
            c.OldestIdx = oldestIdx;
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
                if (!c.GapOpen && c.UnconfirmedSinceMs != 0 && nowMs - c.UnconfirmedSinceMs >= GapAfterMs && isBound(mac))
                {
                    c.GapOpen = true;
                    c.GapStartMs = c.UnconfirmedSinceMs;
                    c.GapStartIdx = c.OldestIdx;
                    c.GapSent = c.GapReports = c.GapFails = 0;
                }
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
            if (_chains.TryGetValue(mac, out var gapChain))
            {
                EndGap(gapChain, mac, nowMs, "drop", "", log);
            }
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

    // Caller holds _gate. Opens a fresh budget window once the previous one has elapsed.
    private static void RollLineWindow(Chain c, long nowMs)
    {
        if (nowMs - c.LineWindowStartMs > LineWindowMs)
        {
            c.LineWindowStartMs = nowMs;
            c.GapLines = c.EchoLines = 0;
        }
    }

    // Caller holds _gate. One line per anomalous window upload: when it was sent, and when
    // (or whether) its echo landed.
    // A chain that held a real effect index and now reports a bare small value (0, 1) lost the effect on its own
    // side. Names how long after our last upload and last echo it happened, so a reset that follows our writes
    // reads differently from one that follows nothing.
    private static void LogRevert(Chain c, string mac, string previous, string current, long nowMs, Action<string> log)
    {
        if (previous.Length == 0 || IsBareIdx(previous) || !IsBareIdx(current))
        {
            return;
        }
        if (nowMs - c.RevertWindowStartMs >= LineWindowMs)
        {
            c.RevertWindowStartMs = nowMs;
            c.RevertLines = 0;
        }
        if (++c.RevertLines > RevertLinesPerWindow)
        {
            return;
        }
        var lastSend = 0L;
        for (var i = 0; i < SentRingSize; i++)
        {
            lastSend = Math.Max(lastSend, c.RingMs[i]);
        }
        var sinceSend = lastSend == 0 ? "none" : $"{nowMs - lastSend} ms";
        var sinceConfirm = c.LastConfirmMs == 0 ? "never" : $"{nowMs - c.LastConfirmMs} ms";
        log($"[slv3-revert] chain {mac} idx {previous} -> {current} at {Stamp()}: last upload {sinceSend} ago, last echo {sinceConfirm} ago, uploads={c.Sent} echoes={c.Confirmed}");
    }

    private static bool IsBareIdx(string idxHex) => idxHex.StartsWith("000000", StringComparison.Ordinal);

    private static void LogEcho(Chain c, string mac, string result, string idx, long sentMs, long nowMs, Action<string> log, long latencyMs = -1)
    {
        if (result == "ok" && latencyMs < SlowEchoMs) return;
        RollLineWindow(c, nowMs);
        if (++c.EchoLines > EchoLinesPerWindow)
        {
            c.EchoSuppressed++;
            return;
        }
        var skipped = c.EchoSuppressed;
        c.EchoSuppressed = 0;
        var lat = latencyMs >= 0 ? $" latMs={latencyMs}" : $" ageMs={nowMs - sentMs}";
        log($"[slv3-echo] chain {mac} {result} idx={idx} sent={WallStamp(sentMs, nowMs)} now={WallStamp(nowMs, nowMs)}{lat}{(skipped > 0 ? $" suppressedBefore={skipped}" : "")}");
    }

    // Caller holds _gate. Closes an open gap with one line carrying both ends.
    private static void EndGap(Chain c, string mac, long nowMs, string endedBy, string confirmedIdx, Action<string> log)
    {
        if (!c.GapOpen)
        {
            return;
        }
        c.GapOpen = false;
        RollLineWindow(c, nowMs);
        if (++c.GapLines > GapLinesPerWindow)
        {
            c.GapSuppressed++;
            return;
        }
        var skipped = c.GapSuppressed;
        c.GapSuppressed = 0;
        var beforeMs = c.LastConfirmMs == 0 ? "never" : $"{c.GapStartMs - c.LastConfirmMs}";
        log($"[slv3-gap] chain {mac} start={WallStamp(c.GapStartMs, nowMs)} end={WallStamp(nowMs, nowMs)} durMs={nowMs - c.GapStartMs} sentIdx={c.GapStartIdx} confirmedIdx={(confirmedIdx.Length > 0 ? confirmedIdx : "none")} endedBy={endedBy} sentInGap={c.GapSent} reportsInGap={c.GapReports} sendFailInGap={c.GapFails} lastConfirmBeforeMs={beforeMs}{(skipped > 0 ? $" suppressedBefore={skipped}" : "")}");
    }

    // Wall clock with milliseconds for a hub tick time, anchored on the current tick.
    private static string WallStamp(long tickMs, long nowTickMs) =>
        DateTime.UtcNow.AddMilliseconds(tickMs - nowTickMs).ToString("HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Stamp() => DateTime.UtcNow.ToString("HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
