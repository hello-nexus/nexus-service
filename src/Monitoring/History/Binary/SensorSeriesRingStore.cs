using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Per-series history for user-tracked sensors (the rules behind
/// /diagnostics/alerts): one raw per-second RingFile and one minute-rollup
/// RingFile per series id, created on demand. Same slot protocol, minute
/// fold and prune-floor semantics as TempComponentRingStore, but two
/// differences: files are named after the series id (a rule guid) so a
/// deleted rule's history is dropped by deleting its two files, and the
/// minute tier keeps MetricsHistory.TempRetentionDays instead of
/// RetentionDays so a tracked sensor can be charted over a quarter.
/// Values are float32 (native units span fan rpm to millivolts, which a
/// fixed-point i16 cannot hold).
///
/// The dictionary of open rings is swapped whole, never mutated, so a
/// concurrent route read sees either the old or the new set. A reader that
/// holds a ring a delete just disposed gets ObjectDisposedException, which
/// the query methods treat as "series gone".
/// </summary>
internal sealed class SensorSeriesRingStore : IDisposable
{
    // Rules cap at 10; the slack absorbs delete-then-create churn between
    // prunes and keeps a corrupt id list from filling the disk.
    internal const int MaxSeries = 32;

    private const int SecondBodyLength = sizeof(float);
    private const int SumOffset = 0;
    private const int CntOffset = SumOffset + sizeof(double);
    private const int MaxOffset = CntOffset + sizeof(int);
    private const int MinuteBodyLength = MaxOffset + sizeof(float);

    private readonly string _dir;
    private readonly long _secondCapacity;
    private readonly long _minuteCapacity;
    private readonly int _minuteRetentionExtraDays;
    private long _pruneFloorSec;
    private Dictionary<string, (RingFile Second, RingFile Minute)> _rings = new(StringComparer.Ordinal);

    public SensorSeriesRingStore(string dir, long secondCapacity, long minuteCapacity, long initialPruneFloorSec)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _secondCapacity = secondCapacity;
        _minuteCapacity = minuteCapacity;
        _minuteRetentionExtraDays = MetricsHistory.TempRetentionDays - MetricsHistory.RetentionDays;
        _pruneFloorSec = initialPruneFloorSec;
        foreach (var file in Directory.EnumerateFiles(dir, "*.ring"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (IsSafeId(id) && _rings.Count < MaxSeries)
            {
                Open(id, _rings);
            }
        }
    }

    /// <summary>Rule ids are guids; anything else could escape the directory.</summary>
    internal static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // Minute rings retain longer than the raw floor the store hands every
    // ring, so shift the minute floor back by the gap, leaving the
    // never-pruned sentinel alone.
    private long MinuteFloorSec(long cutoffSec) =>
        cutoffSec == RingFile.UnwrittenStamp ? cutoffSec : cutoffSec - _minuteRetentionExtraDays * 86_400L;

    private void Open(string id, Dictionary<string, (RingFile Second, RingFile Minute)> into)
    {
        var second = RingFile.CreateOrOpen(Path.Combine(_dir, id + ".ring"), _secondCapacity, SecondBodyLength, _pruneFloorSec);
        var minute = RingFile.CreateOrOpen(
            Path.Combine(_dir, id + ".min"), _minuteCapacity, MinuteBodyLength, MinuteTier.ToPruneFloorIndex(MinuteFloorSec(_pruneFloorSec)));
        into[id] = (second, minute);
    }

    private (RingFile Second, RingFile Minute)? GetOrCreate(string id)
    {
        var current = Volatile.Read(ref _rings);
        if (current.TryGetValue(id, out var existing))
        {
            return existing;
        }
        if (current.Count >= MaxSeries || !IsSafeId(id))
        {
            return null;
        }
        var next = new Dictionary<string, (RingFile, RingFile)>(current, StringComparer.Ordinal);
        Open(id, next);
        Volatile.Write(ref _rings, next);
        return next[id];
    }

    public void Append(IReadOnlyList<MetricSample> samples)
    {
        var touched = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        Span<byte> body = stackalloc byte[SecondBodyLength];

        foreach (var s in samples)
        {
            foreach (var r in s.SensorReadings)
            {
                if (!double.IsFinite(r.Value) || GetOrCreate(r.Id) is not { } rings)
                {
                    continue;
                }
                BinaryPrimitives.WriteSingleLittleEndian(body, (float)r.Value);
                rings.Second.WriteSlot(s.TsSec, body);
                if (!touched.TryGetValue(r.Id, out var minutes))
                {
                    minutes = new HashSet<long>();
                    touched[r.Id] = minutes;
                }
                minutes.Add(MinuteTier.FloorToMinuteSec(s.TsSec));
            }
        }

        var open = Volatile.Read(ref _rings);
        foreach (var (id, minutes) in touched)
        {
            foreach (var minute in minutes)
            {
                RebuildMinute(open[id], minute);
            }
        }
        foreach (var (second, minute) in open.Values)
        {
            second.Flush();
            minute.Flush();
        }
    }

    private static void RebuildMinute((RingFile Second, RingFile Minute) rings, long minuteFloorSec)
    {
        var raw = RingFileScan.Enumerate(rings.Second, minuteFloorSec, minuteFloorSec + 59);
        var agg = FieldAgg.FromReadings(raw.Select(r => (double?)BinaryPrimitives.ReadSingleLittleEndian(r.Body)));
        Span<byte> body = stackalloc byte[MinuteBodyLength];
        BinaryPrimitives.WriteDoubleLittleEndian(body[SumOffset..], agg.Sum);
        BinaryPrimitives.WriteInt32LittleEndian(body[CntOffset..], agg.Cnt);
        BinaryPrimitives.WriteSingleLittleEndian(body[MaxOffset..], (float)agg.Max);
        rings.Minute.WriteSlot(MinuteTier.ToIndex(minuteFloorSec), body);
    }

    public bool RaisePruneFloor(long cutoffSec)
    {
        var changed = false;
        if (cutoffSec > _pruneFloorSec)
        {
            _pruneFloorSec = cutoffSec;
        }
        var minuteFloorIndex = MinuteTier.ToPruneFloorIndex(MinuteFloorSec(cutoffSec));
        foreach (var (second, minute) in Volatile.Read(ref _rings).Values)
        {
            if (cutoffSec > second.PruneFloorSec)
            {
                second.PruneFloorSec = cutoffSec;
                changed = true;
            }
            if (minuteFloorIndex > minute.PruneFloorSec)
            {
                minute.PruneFloorSec = minuteFloorIndex;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Slot-aggregated avg/max per series. A step of a whole minute or more folds the minute rollup; a narrower one decimates the raw ring.</summary>
    public IReadOnlyList<SensorSeriesDecimatedSlot> QueryDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var result = new List<SensorSeriesDecimatedSlot>();
        if (toSec < fromSec)
        {
            return result;
        }
        var rollup = stepSeconds >= 60 && stepSeconds % 60 == 0;
        foreach (var (id, rings) in Volatile.Read(ref _rings))
        {
            try
            {
                if (rollup)
                {
                    AddRollup(result, id, rings.Minute, fromSec, toSec, stepSeconds);
                }
                else
                {
                    AddRaw(result, id, rings.Second, fromSec, toSec, stepSeconds);
                }
            }
            catch (ObjectDisposedException)
            {
                // Deleted while this query ran.
            }
        }
        return result;
    }

    private static void AddRaw(List<SensorSeriesDecimatedSlot> output, string id, RingFile ring, long fromSec, long toSec, int stepSeconds)
    {
        var rows = RingFileScan.Enumerate(ring, fromSec, toSec);
        var points = MetricsDecimation.Decimate(
            rows.Select(r => new MetricSamplePoint(r.Key, BinaryPrimitives.ReadSingleLittleEndian(r.Body))),
            fromSec, toSec, stepSeconds);
        foreach (var p in points)
        {
            output.Add(new SensorSeriesDecimatedSlot(id, p.T, p.Avg, p.Max));
        }
    }

    private static void AddRollup(List<SensorSeriesDecimatedSlot> output, string id, RingFile ring, long fromSec, long toSec, int stepSeconds)
    {
        var first = MinuteTier.ToIndex(MinuteTier.FloorToMinuteSec(fromSec));
        var last = MinuteTier.ToIndex(MinuteTier.FloorToMinuteSec(toSec));
        var bySlot = new SortedDictionary<long, FieldAgg>();
        foreach (var (minuteIndex, body) in RingFileScan.Enumerate(ring, first, last))
        {
            var slot = minuteIndex * MinuteTier.SecondsPerMinute / stepSeconds * stepSeconds;
            var agg = new FieldAgg(
                BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(SumOffset)),
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(CntOffset)),
                BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(MaxOffset)));
            bySlot[slot] = bySlot.TryGetValue(slot, out var acc) ? acc.Combine(agg) : agg;
        }
        foreach (var (slot, agg) in bySlot)
        {
            if (agg.Avg is { } avg && agg.MaxOrNull is { } max)
            {
                output.Add(new SensorSeriesDecimatedSlot(id, slot, avg, max));
            }
        }
    }

    /// <summary>Drops one series' rings and files. False when it had none.</summary>
    public bool Delete(string id)
    {
        var current = Volatile.Read(ref _rings);
        if (!current.TryGetValue(id, out var rings))
        {
            return false;
        }
        var next = new Dictionary<string, (RingFile, RingFile)>(current, StringComparer.Ordinal);
        next.Remove(id);
        Volatile.Write(ref _rings, next);
        rings.Second.Dispose();
        rings.Minute.Dispose();
        TryDelete(Path.Combine(_dir, id + ".ring"));
        TryDelete(Path.Combine(_dir, id + ".min"));
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Drops every series. Returns the number of ring files removed.</summary>
    public int Clear()
    {
        var count = 0;
        foreach (var id in Volatile.Read(ref _rings).Keys.ToList())
        {
            Delete(id);
            count += 2;
        }
        return count;
    }

    public void Dispose()
    {
        foreach (var (second, minute) in Volatile.Read(ref _rings).Values)
        {
            second.Dispose();
            minute.Dispose();
        }
    }
}
