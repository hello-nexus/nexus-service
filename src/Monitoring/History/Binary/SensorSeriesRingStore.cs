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
/// concurrent route read sees either the old or the new set. RingFile reads
/// through a raw pointer with no disposed check, so each series' rings are
/// reference counted: a query holds a reference for its scan and Delete or
/// Dispose only drop the owner's, so the unmap (and file delete) happens when
/// the last reader leaves and a delete never waits on a query.
/// </summary>
internal sealed class SensorSeriesRingStore : IDisposable
{
    // Above the rule cap (built-ins included); the slack absorbs delete-then-create churn between
    // prunes and keeps a corrupt id list from filling the disk.
    internal const int MaxSeries = 48;

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
    private Dictionary<string, SeriesRings> _rings = new(StringComparer.Ordinal);

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

    private void Open(string id, Dictionary<string, SeriesRings> into)
    {
        var second = RingFile.CreateOrOpen(Path.Combine(_dir, id + ".ring"), _secondCapacity, SecondBodyLength, _pruneFloorSec);
        var minute = RingFile.CreateOrOpen(
            Path.Combine(_dir, id + ".min"), _minuteCapacity, MinuteBodyLength, MinuteTier.ToPruneFloorIndex(MinuteFloorSec(_pruneFloorSec)));
        into[id] = new SeriesRings(second, minute, Path.Combine(_dir, id + ".ring"), Path.Combine(_dir, id + ".min"), () => !Volatile.Read(ref _rings).ContainsKey(id));
    }

    private SeriesRings? GetOrCreate(string id)
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
        var next = new Dictionary<string, SeriesRings>(current, StringComparer.Ordinal);
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
        foreach (var id in touched.Keys)
        {
            open[id].Second.Flush();
            open[id].Minute.Flush();
        }
    }

    private static void RebuildMinute(SeriesRings rings, long minuteFloorSec)
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
    public IReadOnlyList<SensorSeriesDecimatedSlot> QueryDecimated(long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? seriesIds = null)
    {
        var result = new List<SensorSeriesDecimatedSlot>();
        if (toSec < fromSec)
        {
            return result;
        }
        var rollup = stepSeconds >= 60 && stepSeconds % 60 == 0;
        foreach (var (id, rings) in Volatile.Read(ref _rings))
        {
            if ((seriesIds is not null && !seriesIds.Contains(id)) || !rings.TryAcquire())
            {
                continue;
            }
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
            finally
            {
                rings.Release();
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
        var next = new Dictionary<string, SeriesRings>(current, StringComparer.Ordinal);
        next.Remove(id);
        Volatile.Write(ref _rings, next);
        // Unmaps now when no query holds the rings. A mapped file cannot be removed on Windows, so a failed delete is retried by the last reader.
        rings.Retire();
        if (!(TryDelete(Path.Combine(_dir, id + ".ring")) & TryDelete(Path.Combine(_dir, id + ".min"))))
        {
            rings.DeleteWhenReleased();
        }
        return true;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

    /// <summary>Deletes every series whose id is not in <paramref name="liveIds"/>, open or not, so files orphaned by a crash or an older build neither linger nor count toward <see cref="MaxSeries"/>.</summary>
    public void PruneExcept(IReadOnlySet<string> liveIds)
    {
        foreach (var id in Volatile.Read(ref _rings).Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            Delete(id);
        }
        foreach (var file in Directory.EnumerateFiles(_dir).Where(f => Path.GetExtension(f) is ".ring" or ".min").ToList())
        {
            if (!liveIds.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                TryDelete(file);
            }
        }
    }

    public void Dispose()
    {
        foreach (var rings in Volatile.Read(ref _rings).Values)
        {
            rings.Retire();
        }
    }

    /// <summary>One series' two rings, unmapped when the owner and every in-flight query have let go.</summary>
    private sealed class SeriesRings(RingFile second, RingFile minute, string secondPath, string minutePath, Func<bool> isOrphaned)
    {
        private int _refs = 1;
        private volatile bool _deleteFiles;

        public RingFile Second { get; } = second;
        public RingFile Minute { get; } = minute;

        public void Deconstruct(out RingFile secondRing, out RingFile minuteRing)
        {
            secondRing = Second;
            minuteRing = Minute;
        }

        public bool TryAcquire()
        {
            while (true)
            {
                var refs = Volatile.Read(ref _refs);
                if (refs <= 0)
                {
                    return false;
                }
                if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs)
                {
                    return true;
                }
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0)
            {
                return;
            }
            Second.Dispose();
            Minute.Dispose();
            if (_deleteFiles)
            {
                DeleteFiles();
            }
        }

        /// <summary>Drops the owner's reference; the unmap follows once no query holds one.</summary>
        public void Retire() => Release();

        /// <summary>Removes the files once unmapped; a delete that failed while a query held the mapping is retried here.</summary>
        public void DeleteWhenReleased()
        {
            _deleteFiles = true;
            if (Volatile.Read(ref _refs) <= 0)
            {
                DeleteFiles();
            }
        }

        private void DeleteFiles()
        {
            // A series recreated under the same id since the delete owns those paths now.
            if (isOrphaned())
            {
                TryDelete(secondPath);
                TryDelete(minutePath);
            }
        }
    }
}
