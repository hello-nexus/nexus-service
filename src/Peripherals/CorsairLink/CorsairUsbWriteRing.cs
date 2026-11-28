using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>Lock-free ring of recent iCUE LINK hub transfers for the LCD stall dump; a racing read drops the entry.</summary>
internal sealed class CorsairUsbWriteRing
{
    internal static readonly CorsairUsbWriteRing Shared = new(1024);

    internal const int MaxDumpEntries = 16;
    // Entries that ended shortly before the window can still have caused the stall.
    private const double LeadMs = 100;
    private const long DumpIntervalMs = 5000;

    private struct Slot
    {
        // Zero while the slot is being written; index + 1 once it holds a complete begin record.
        public long Seq;
        public long StartTs;
        // Zero while the transfer is in flight.
        public long EndTs;
        public long WriteTicks;
        public string? Hub;
        public string? Activity;
        public int Command;
        public int ThreadId;
    }

    internal readonly struct Entry
    {
        public Entry(long startTs, long endTs, long writeTicks, string hub, string activity, int command, int threadId)
        {
            StartTs = startTs;
            EndTs = endTs;
            WriteTicks = writeTicks;
            Hub = hub;
            Activity = activity;
            Command = command;
            ThreadId = threadId;
        }

        public long StartTs { get; }
        public long EndTs { get; }
        public long WriteTicks { get; }
        public string Hub { get; }
        public string Activity { get; }
        public int Command { get; }
        public int ThreadId { get; }
    }

    private readonly Slot[] _slots;
    private readonly long _mask;
    private long _next;
    private long _lastDumpMs = long.MinValue;

    internal CorsairUsbWriteRing(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be a power of two");
        }
        _slots = new Slot[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Records the start of a transfer on the calling thread; pass the result to <see cref="End"/>.</summary>
    internal long Begin(string hub, string activity, int command)
    {
        var idx = Interlocked.Increment(ref _next) - 1;
        ref var s = ref _slots[idx & _mask];
        Interlocked.Exchange(ref s.Seq, 0);
        s.StartTs = Stopwatch.GetTimestamp();
        Volatile.Write(ref s.EndTs, 0);
        s.WriteTicks = 0;
        s.Hub = hub;
        s.Activity = activity;
        s.Command = command;
        s.ThreadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref s.Seq, idx + 1);
        return idx;
    }

    /// <summary>Closes the entry opened by <see cref="Begin"/>; a no-op when the ring has since wrapped over it.</summary>
    internal void End(long token, long writeTicks)
    {
        ref var s = ref _slots[token & _mask];
        if (Volatile.Read(ref s.Seq) != token + 1)
        {
            return;
        }
        s.WriteTicks = writeTicks;
        Volatile.Write(ref s.EndTs, Stopwatch.GetTimestamp());
    }

    /// <summary>Entries whose span touches the window, oldest first.</summary>
    internal List<Entry> Overlapping(long windowStartTs, long windowEndTs)
    {
        var result = new List<Entry>();
        var leadTicks = (long)(LeadMs * Stopwatch.Frequency / 1000.0);
        var next = Volatile.Read(ref _next);
        var first = Math.Max(0, next - _slots.Length);
        for (var i = first; i < next; i++)
        {
            ref var s = ref _slots[i & _mask];
            var seq = Volatile.Read(ref s.Seq);
            if (seq != i + 1)
            {
                continue;
            }
            var start = s.StartTs;
            var end = Volatile.Read(ref s.EndTs);
            var writeTicks = s.WriteTicks;
            var hub = s.Hub;
            var activity = s.Activity;
            var command = s.Command;
            var thread = s.ThreadId;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref s.Seq) != seq || hub is null || activity is null)
            {
                continue;
            }
            // An unfinished transfer is still holding the bus at the window's end.
            var effectiveEnd = end == 0 ? windowEndTs : end;
            if (effectiveEnd < windowStartTs - leadTicks || start > windowEndTs)
            {
                continue;
            }
            result.Add(new Entry(start, end, writeTicks, hub, activity, command, thread));
        }
        return result;
    }

    /// <summary>One WRN line per interval naming the longest hub transfers that overlapped the window.</summary>
    internal bool DumpIfDue(long windowStartTs, long windowEndTs, long nowMs)
    {
        var last = Interlocked.Read(ref _lastDumpMs);
        if (last != long.MinValue && nowMs - last < DumpIntervalMs)
        {
            return false;
        }
        if (Interlocked.CompareExchange(ref _lastDumpMs, nowMs, last) != last)
        {
            return false;
        }
        ServiceLog.Warn(FormatDump(windowStartTs, windowEndTs));
        return true;
    }

    internal string FormatDump(long windowStartTs, long windowEndTs)
    {
        var entries = Overlapping(windowStartTs, windowEndTs);
        var windowMs = Stopwatch.GetElapsedTime(windowStartTs, windowEndTs).TotalMilliseconds;
        var total = entries.Count;
        if (total > MaxDumpEntries)
        {
            entries.Sort((a, b) => DurationTicks(b, windowEndTs).CompareTo(DurationTicks(a, windowEndTs)));
            entries.RemoveRange(MaxDumpEntries, total - MaxDumpEntries);
            entries.Sort((a, b) => a.StartTs.CompareTo(b.StartTs));
        }

        var sb = new StringBuilder(256);
        sb.Append("[corsair-usb] hub transfers overlapping lcd stall window=")
            .Append((long)windowMs).Append("ms overlapping=").Append(total)
            .Append(" shown=").Append(entries.Count).Append(" ::");
        foreach (var e in entries)
        {
            var offsetMs = Stopwatch.GetElapsedTime(windowStartTs, e.StartTs).TotalMilliseconds;
            if (e.StartTs < windowStartTs)
            {
                offsetMs = -Stopwatch.GetElapsedTime(e.StartTs, windowStartTs).TotalMilliseconds;
            }
            var spanMs = Stopwatch.GetElapsedTime(e.StartTs, e.EndTs == 0 ? windowEndTs : e.EndTs).TotalMilliseconds;
            var writeMs = Stopwatch.GetElapsedTime(0, e.WriteTicks).TotalMilliseconds;
            sb.Append(" [hub=").Append(e.Hub)
                .Append(" activity=").Append(e.Activity)
                .Append(" cmd=0x").Append(e.Command.ToString("x2"))
                .Append(" thread=").Append(e.ThreadId)
                .Append(" t=").Append((long)offsetMs).Append("ms")
                .Append(" write=").Append((long)writeMs).Append("ms")
                .Append(" total=").Append((long)spanMs).Append("ms")
                .Append(e.EndTs == 0 ? " inflight" : "")
                .Append(']');
        }
        return sb.ToString();
    }

    private static long DurationTicks(Entry e, long windowEndTs) => (e.EndTs == 0 ? windowEndTs : e.EndTs) - e.StartTs;
}
