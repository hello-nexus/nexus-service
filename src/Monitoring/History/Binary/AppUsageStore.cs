using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Binary-file-backed IAppUsageHistoryStore, the storage-dominant per-app
/// tier: a global AppNameDictionary shared by every metric, plus independent
/// metric kinds (cpu, memory, gpu, vram, storage, net, storage-read,
/// storage-write, net-down, net-up) each stored as append-only per-UTC-day
/// segment files
/// under their own directory. storage/net and their read-write/down-up
/// splits carry no gpu dimension and share one wire format: raw bytes/sec
/// stored as an int64 (StorageRecordWidth), wider than the x10 fixed-point
/// int32 SimpleRecordWidth uses for cpu/mem, so a disk or network throughput
/// reading past roughly 2 GB/s stays representable instead of overflowing.
/// Retention drops whole day files once
/// every tick they hold is older than the prune cutoff; PruneFloorSec then
/// hides whatever remains of a day file straddling that cutoff at read
/// time, so a query never sees a pruned tick even though the day itself is
/// still on disk.
///
/// No day-seal sorted-block table: a day segment is a flat append log of
/// tick records (see WriteTicks), and every query does a linear scan over
/// the days it touches, translating each record's per-day local app id back
/// to the global id via that day's AppLocalIdMap. Query windows are bounded
/// by MetricsHistory.RetentionDays (7 days), and even a fully-saturated day
/// segment is a few MB, so this trades a small amount of read-side CPU for
/// a format an order of magnitude simpler than a sorted per-app block table
/// - see AppUsageStorageEstimateTests for the measured footprint. A scan
/// reads each day file once into a pooled buffer and walks its records in
/// place (SegmentCursor), so a query allocates in proportion to its result,
/// not to the bytes it scanned.
///
/// AppUsageStore is the sole writer (Append), the same single-writer
/// assumption the rest of the binary store is built on. The writer keeps a
/// per-(kind, day) SegmentWriter - that day's AppLocalIdMap plus the byte
/// length it has verified clean - so a flush appends without re-reading the
/// segment: the first touch of a day validates and truncates any torn tail
/// (a crash mid-append, see ComputeCleanLength), and every append after it
/// is known clean because this store wrote and fsynced it. A segment whose
/// on-disk length no longer matches what the writer recorded (deleted or
/// truncated underneath a running service) is revalidated from disk.
/// </summary>
internal sealed class AppUsageStore : IDisposable
{
    private enum AppMetricKind
    {
        Cpu = 0, Mem = 1, Gpu = 2, Vram = 3, Storage = 4, Net = 5,
        StorageRead = 6, StorageWrite = 7, NetDown = 8, NetUp = 9,
    }

    private enum SegmentFormat { Simple, Gpu, Vram, Storage }

    private static readonly string[] KindDirNames =
        { "cpu", "mem", "gpu", "vram", "storage", "net", "storage-read", "storage-write", "net-down", "net-up" };
    private static readonly AppMetricKind[] AllKinds =
    {
        AppMetricKind.Cpu, AppMetricKind.Mem, AppMetricKind.Gpu, AppMetricKind.Vram, AppMetricKind.Storage, AppMetricKind.Net,
        AppMetricKind.StorageRead, AppMetricKind.StorageWrite, AppMetricKind.NetDown, AppMetricKind.NetUp,
    };

    private const int SecondsPerDay = 86_400;

    // Tick record: ts:i64 | count:u16 | count * entry | crc:u32 over ts..entries.
    private const int TickHeaderBytes = 10;
    private const int TickCrcBytes = 4;

    // Per-entry byte width for each day-segment format.
    private const int SimpleRecordWidth = 6;
    private const int GpuRecordWidth = 10;
    private const int VramRecordWidth = 8;
    // Storage/net and their read-write/down-up splits all carry raw
    // bytes/sec as an int64, rather than the x10 fixed-point percent
    // SimpleRecordWidth's int32 slot holds - disk/network throughput on a
    // fast NVMe drive or NIC exceeds int32 range.
    private const int StorageRecordWidth = 10;

    // Writers for days older than this many days behind the newest touched
    // day are dropped; a flush only ever carries the current day and, at
    // midnight, the one before it.
    private const long WriterRetentionDays = 1;

    private readonly record struct AppEntry(int GlobalId, int GpuIndex, double Value, double? VramMb);

    // One pending entry for a day segment, already resolved to a per-day
    // local id; Value/Vram carry the already-scaled on-disk integers.
    private readonly record struct PendingEntry(ushort LocalId, ushort GpuIndex, long Value, int Vram);

    private sealed class SegmentWriter
    {
        public SegmentWriter(AppLocalIdMap idMap) { IdMap = idMap; }
        public AppLocalIdMap IdMap { get; }
        // Byte length verified clean (every record through it passes its
        // bounds/Crc check); negative until validated.
        public long CleanLength { get; set; } = -1;
    }

    private readonly string _dir;
    private readonly Func<string, int?> _resolveGpuIndex;
    private readonly AppNameDictionary _names;
    private readonly string _pruneFloorPath;
    private readonly Dictionary<(AppMetricKind Kind, long Day), SegmentWriter> _writers = new();
    private long _pruneFloorSec;

    /// <param name="dir">Directory this store owns exclusively (matches
    /// GpuRingStore/TempComponentRingStore's own per-store directory
    /// convention).</param>
    /// <param name="resolveGpuIndex">Resolves a sanitized gpu id to the
    /// scalar side's GpuRingStore ring index, or null if the scalar store has
    /// never registered it: an app-usage gpu/vram sample for a gid with no
    /// scalar registration is dropped rather than minting an orphaned
    /// identity for it.</param>
    public AppUsageStore(string dir, Func<string, int?> resolveGpuIndex)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _resolveGpuIndex = resolveGpuIndex;
        _names = AppNameDictionary.Open(Path.Combine(dir, "apps.dict"));
        _pruneFloorPath = Path.Combine(dir, "prune.floor");
        _pruneFloorSec = LoadPruneFloor(_pruneFloorPath);

        foreach (var name in KindDirNames)
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
        }
    }

    /// <summary>Timestamps below this floor read as absent regardless of
    /// what a boundary day file still physically holds - the read-time half
    /// of retention, matching RingFile.PruneFloorSec's role for the
    /// fixed-slot tiers (whole day files, not individual rows, are what
    /// Append below actually deletes).</summary>
    public long PruneFloorSec
    {
        get => Volatile.Read(ref _pruneFloorSec);
        private set => Volatile.Write(ref _pruneFloorSec, value);
    }

    private static long FloorToDay(long tsSec) => tsSec / SecondsPerDay;

    private string KindDir(AppMetricKind kind) => Path.Combine(_dir, KindDirNames[(int)kind]);
    private string SegPath(AppMetricKind kind, long day) => Path.Combine(KindDir(kind), $"{day}.seg");
    private string IdsPath(AppMetricKind kind, long day) => Path.Combine(KindDir(kind), $"{day}.ids");

    private static SegmentFormat FormatOf(AppMetricKind kind) => kind switch
    {
        AppMetricKind.Cpu or AppMetricKind.Mem => SegmentFormat.Simple,
        AppMetricKind.Gpu => SegmentFormat.Gpu,
        AppMetricKind.Vram => SegmentFormat.Vram,
        _ => SegmentFormat.Storage,
    };

    private static int RecordWidthOf(SegmentFormat format) => format switch
    {
        SegmentFormat.Simple => SimpleRecordWidth,
        SegmentFormat.Gpu => GpuRecordWidth,
        SegmentFormat.Vram => VramRecordWidth,
        _ => StorageRecordWidth,
    };

    // ----- write -----

    public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec)
    {
        if (ticks.Count == 0 && pruneCutoffSec is null)
        {
            return;
        }

        if (ticks.Count > 0)
        {
            var byKindDay = new Dictionary<(AppMetricKind, long), List<(long Ts, List<(int GlobalId, int GpuIndex, long Value, int Vram)> Apps)>>();

            // Name resolution happens here, in tick-array order (not sorted
            // by ts) - first-seen casing depends on iteration order, not
            // chronological order, when a batch itself carries out-of-order
            // ticks. Every "gpu:<gid>"/"vram:<gid>" sample in one tick folds
            // into that tick's single gpu/vram record, so a bare gpu/vram
            // query sees one record per ts to pre-aggregate across adapters.
            var perTick = new List<(int GlobalId, int GpuIndex, long Value, int Vram)>?[AllKinds.Length];
            foreach (var tick in ticks)
            {
                var day = FloorToDay(tick.TsSec);
                Array.Clear(perTick);
                foreach (var metric in tick.Metrics)
                {
                    if (metric.Apps.Count == 0)
                    {
                        continue;
                    }
                    if (ResolveWriteKind(metric.Metric) is not var (kind, gpuIndex))
                    {
                        continue;
                    }

                    var entries = perTick[(int)kind] ??= new List<(int, int, long, int)>(metric.Apps.Count);
                    switch (FormatOf(kind))
                    {
                        case SegmentFormat.Simple:
                            foreach (var a in metric.Apps)
                            {
                                entries.Add((_names.RegisterOrGet(a.Name), 0, ScaleX10ToInt32(a.Value), 0));
                            }
                            break;
                        case SegmentFormat.Gpu:
                            foreach (var a in metric.Apps)
                            {
                                entries.Add((_names.RegisterOrGet(a.Name), gpuIndex, FixedPointCodec.ScaleX10(a.Value), ScaleVramMb(a.VramMb)));
                            }
                            break;
                        case SegmentFormat.Vram:
                            foreach (var a in metric.Apps)
                            {
                                entries.Add((_names.RegisterOrGet(a.Name), gpuIndex, RoundToMb(a.Value), 0));
                            }
                            break;
                        default:
                            foreach (var a in metric.Apps)
                            {
                                entries.Add((_names.RegisterOrGet(a.Name), 0, ScaleWholeToInt64(a.Value), 0));
                            }
                            break;
                    }
                }

                for (var k = 0; k < perTick.Length; k++)
                {
                    if (perTick[k] is not { Count: > 0 } entries)
                    {
                        continue;
                    }
                    var kind = AllKinds[k];
                    if (!byKindDay.TryGetValue((kind, day), out var dayTicks))
                    {
                        dayTicks = new List<(long, List<(int, int, long, int)>)>();
                        byKindDay[(kind, day)] = dayTicks;
                    }
                    dayTicks.Add((tick.TsSec, entries));
                }
            }

            // Kind order is fixed so a day written for several kinds lands in
            // the same directory order every flush.
            foreach (var kind in AllKinds)
            {
                foreach (var ((k, day), dayTicks) in byKindDay)
                {
                    if (k == kind)
                    {
                        WriteDay(kind, day, dayTicks);
                    }
                }
            }
        }

        if (pruneCutoffSec is { } cutoff)
        {
            var newFloor = Math.Max(PruneFloorSec, cutoff);
            if (newFloor != PruneFloorSec)
            {
                PruneFloorSec = newFloor;
                PersistPruneFloor(newFloor);
            }
            DeleteDaysBefore(FloorToDay(cutoff));
        }
    }

    // "gpu:<gid>"/"vram:<gid>" resolve through resolveGpuIndex: a gid with
    // no scalar-side registration this flush has no app rows either, since
    // both come from the same sensors.GetGpus() read.
    private (AppMetricKind Kind, int GpuIndex)? ResolveWriteKind(string metric)
    {
        switch (metric)
        {
            case "cpu": return (AppMetricKind.Cpu, 0);
            case "memory": return (AppMetricKind.Mem, 0);
            case "storage": return (AppMetricKind.Storage, 0);
            case "net": return (AppMetricKind.Net, 0);
            case "storage-read": return (AppMetricKind.StorageRead, 0);
            case "storage-write": return (AppMetricKind.StorageWrite, 0);
            case "net-down": return (AppMetricKind.NetDown, 0);
            case "net-up": return (AppMetricKind.NetUp, 0);
        }
        if (metric.StartsWith("gpu:", StringComparison.Ordinal))
        {
            return _resolveGpuIndex(metric[4..]) is { } idx ? (AppMetricKind.Gpu, idx) : null;
        }
        if (metric.StartsWith("vram:", StringComparison.Ordinal))
        {
            return _resolveGpuIndex(metric[5..]) is { } idx ? (AppMetricKind.Vram, idx) : null;
        }
        return null;
    }

    private SegmentWriter GetWriter(AppMetricKind kind, long day)
    {
        if (_writers.TryGetValue((kind, day), out var writer))
        {
            return writer;
        }

        writer = new SegmentWriter(AppLocalIdMap.LoadForWrite(IdsPath(kind, day)));
        _writers[(kind, day)] = writer;

        var stale = new List<(AppMetricKind, long)>();
        foreach (var key in _writers.Keys)
        {
            if (key.Day < day - WriterRetentionDays)
            {
                stale.Add(key);
            }
        }
        foreach (var key in stale)
        {
            _writers.Remove(key);
        }
        return writer;
    }

    private void WriteDay(AppMetricKind kind, long day, List<(long Ts, List<(int GlobalId, int GpuIndex, long Value, int Vram)> Apps)> ticks)
    {
        var format = FormatOf(kind);
        var recordWidth = RecordWidthOf(format);
        var writer = GetWriter(kind, day);

        var resolvedTicks = new List<(long Ts, List<PendingEntry> Apps)>(ticks.Count);
        var totalBytes = 0;
        foreach (var (ts, apps) in ticks)
        {
            var resolved = new List<PendingEntry>(apps.Count);
            foreach (var (globalId, gpuIndex, value, vram) in apps)
            {
                if (writer.IdMap.GetOrAdd(globalId) is { } localId)
                {
                    resolved.Add(new PendingEntry((ushort)localId, (ushort)gpuIndex, value, vram));
                }
            }
            if (resolved.Count > 0)
            {
                resolvedTicks.Add((ts, resolved));
                totalBytes += TickHeaderBytes + resolved.Count * recordWidth + TickCrcBytes;
            }
        }
        if (resolvedTicks.Count == 0)
        {
            return;
        }

        // The id map must reach disk before the segment that references its
        // local ids - see AppLocalIdMap's class doc for why this order keeps
        // a crash between the two safe (an orphaned surplus id, never a
        // segment record pointing past the map).
        writer.IdMap.Flush();

        var buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            var pos = 0;
            foreach (var (ts, apps) in resolvedTicks)
            {
                pos += EncodeTick(buffer.AsSpan(pos), format, ts, apps);
            }

            var segPath = SegPath(kind, day);
            using var fs = new FileStream(segPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (fs.Length != writer.CleanLength)
            {
                // Unvalidated, or changed underneath the writer: a torn
                // trailing record must go before new records land after it.
                var clean = ComputeCleanLength(fs, recordWidth);
                if (clean != fs.Length)
                {
                    fs.SetLength(clean);
                }
                writer.CleanLength = clean;
            }
            fs.Seek(0, SeekOrigin.End);
            fs.Write(buffer, 0, pos);
            fs.Flush(flushToDisk: true);
            writer.CleanLength = fs.Length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int EncodeTick(Span<byte> buf, SegmentFormat format, long ts, List<PendingEntry> apps)
    {
        var recordWidth = RecordWidthOf(format);
        var bodyLength = TickHeaderBytes + apps.Count * recordWidth;
        BinaryPrimitives.WriteInt64LittleEndian(buf, ts);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[8..], (ushort)apps.Count);

        var pos = TickHeaderBytes;
        foreach (var e in apps)
        {
            var entry = buf.Slice(pos, recordWidth);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, e.LocalId);
            switch (format)
            {
                case SegmentFormat.Simple:
                    BinaryPrimitives.WriteInt32LittleEndian(entry[2..], (int)e.Value);
                    break;
                case SegmentFormat.Gpu:
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], e.GpuIndex);
                    BinaryPrimitives.WriteInt16LittleEndian(entry[4..], (short)e.Value);
                    BinaryPrimitives.WriteInt32LittleEndian(entry[6..], e.Vram);
                    break;
                case SegmentFormat.Vram:
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], e.GpuIndex);
                    BinaryPrimitives.WriteInt32LittleEndian(entry[4..], (int)e.Value);
                    break;
                default:
                    BinaryPrimitives.WriteInt64LittleEndian(entry[2..], e.Value);
                    break;
            }
            pos += recordWidth;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf[bodyLength..], Crc32.Compute(buf[..bodyLength]));
        return bodyLength + TickCrcBytes;
    }

    // Reads the whole open segment and returns the byte length through the
    // last record that passes its bounds/Crc check.
    private static long ComputeCleanLength(FileStream fs, int recordWidth)
    {
        var length = checked((int)fs.Length);
        if (length == 0)
        {
            return 0;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            fs.Seek(0, SeekOrigin.Begin);
            var read = ReadFully(fs, buffer, length);
            var cursor = new SegmentCursor(buffer.AsSpan(0, read), recordWidth);
            while (cursor.MoveNext())
            {
            }
            return cursor.Position;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadFully(FileStream fs, byte[] buffer, int length)
    {
        var read = 0;
        while (read < length)
        {
            var n = fs.Read(buffer, read, length - read);
            if (n <= 0)
            {
                break;
            }
            read += n;
        }
        return read;
    }

    /// <summary>Deletes every day segment across every metric kind. Returns
    /// the number of .seg files removed. AppNameDictionary/AppLocalIdMap
    /// mappings are left in place - cheap metadata, harmless to keep.</summary>
    public int ClearAll()
    {
        _writers.Clear();
        var removed = 0;
        foreach (var kind in AllKinds)
        {
            var dir = KindDir(kind);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.GetFiles(dir, "*.seg"))
            {
                TryDelete(file);
                TryDelete(Path.ChangeExtension(file, ".ids"));
                removed++;
            }
        }
        return removed;
    }

    private void DeleteDaysBefore(long cutoffDay)
    {
        var staleWriters = new List<(AppMetricKind, long)>();
        foreach (var key in _writers.Keys)
        {
            if (key.Day < cutoffDay)
            {
                staleWriters.Add(key);
            }
        }
        foreach (var key in staleWriters)
        {
            _writers.Remove(key);
        }

        foreach (var kind in AllKinds)
        {
            var dir = KindDir(kind);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.GetFiles(dir, "*.seg"))
            {
                if (ParseDay(file) is { } day && day < cutoffDay)
                {
                    TryDelete(file);
                    TryDelete(Path.ChangeExtension(file, ".ids"));
                }
            }

            // A crash between idMap.Flush() and the .seg FileStream being
            // created (WriteDay) can leave an .ids file with no matching
            // .seg - dead weight regardless of its day, since ReadDay only
            // ever opens an .ids file after finding its .seg first. Swept
            // here rather than given its own pass, since this already runs
            // once per prune.
            foreach (var idsFile in Directory.GetFiles(dir, "*.ids"))
            {
                if (!File.Exists(Path.ChangeExtension(idsFile, ".seg")))
                {
                    TryDelete(idsFile);
                }
            }

            // A crash between AppLocalIdMap's rewrite of <day>.ids.tmp and
            // its rename leaves the .tmp behind; the next rewrite recreates
            // it, so any one found here is dead.
            foreach (var tmpFile in Directory.GetFiles(dir, "*.ids.tmp"))
            {
                TryDelete(tmpFile);
            }
        }
    }

    private static long? ParseDay(string segPath) =>
        long.TryParse(Path.GetFileNameWithoutExtension(segPath), out var day) ? day : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    // ----- read -----

    /// <summary>Walks the tick records of one day segment in place. A record
    /// whose bounds or Crc fail ends the walk: everything after a torn
    /// record is unreachable garbage until the writer truncates it.</summary>
    private ref struct SegmentCursor
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly int _recordWidth;

        public SegmentCursor(ReadOnlySpan<byte> bytes, int recordWidth)
        {
            _bytes = bytes;
            _recordWidth = recordWidth;
            Position = 0;
            Ts = 0;
            Count = 0;
            Entries = default;
        }

        /// <summary>Byte offset just past the last record that validated.</summary>
        public int Position { get; private set; }

        public long Ts { get; private set; }
        public int Count { get; private set; }
        public ReadOnlySpan<byte> Entries { get; private set; }

        public bool MoveNext()
        {
            var pos = Position;
            if (pos + TickHeaderBytes > _bytes.Length)
            {
                return false;
            }
            var ts = BinaryPrimitives.ReadInt64LittleEndian(_bytes.Slice(pos, 8));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.Slice(pos + 8, 2));
            var bodyLength = TickHeaderBytes + count * _recordWidth;
            if (pos + bodyLength + TickCrcBytes > _bytes.Length)
            {
                return false;
            }
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.Slice(pos + bodyLength, TickCrcBytes));
            if (Crc32.Compute(_bytes.Slice(pos, bodyLength)) != expectedCrc)
            {
                return false;
            }

            Ts = ts;
            Count = count;
            Entries = _bytes.Slice(pos + TickHeaderBytes, count * _recordWidth);
            Position = pos + bodyLength + TickCrcBytes;
            return true;
        }
    }

    // Per-day decode context a visitor needs to turn an entry's bytes into
    // an AppEntry: the segment format and that day's local -> global id map.
    // Field reads are unaligned loads off the entry's base reference rather
    // than re-sliced spans: SegmentCursor has already bounds-checked and
    // Crc-validated the whole record, and this runs once per entry over
    // every entry a query scans.
    private readonly record struct DayContext(SegmentFormat Format, int RecordWidth, int[] IdMap)
    {
        /// <summary>Decodes entry <paramref name="i"/>; false when its local
        /// id points past this day's id map (an id the map never persisted -
        /// see AppLocalIdMap's write-order contract).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadEntry(ReadOnlySpan<byte> entries, int i, out AppEntry entry)
        {
            ref var e = ref Unsafe.Add(ref MemoryMarshal.GetReference(entries), i * RecordWidth);
            int localId = ReadU16(ref e, 0);
            if ((uint)localId >= (uint)IdMap.Length)
            {
                entry = default;
                return false;
            }
            var globalId = IdMap[localId];
            switch (Format)
            {
                case SegmentFormat.Simple:
                    entry = new AppEntry(globalId, -1, ReadI32(ref e, 2) / 10.0, null);
                    return true;
                case SegmentFormat.Gpu:
                    entry = new AppEntry(
                        globalId,
                        ReadU16(ref e, 2),
                        FixedPointCodec.UnscaleX10(ReadI16(ref e, 4)) ?? 0,
                        UnscaleVramMb(ReadI32(ref e, 6)));
                    return true;
                case SegmentFormat.Vram:
                    entry = new AppEntry(globalId, ReadU16(ref e, 2), ReadI32(ref e, 4), null);
                    return true;
                default:
                    entry = new AppEntry(globalId, -1, ReadI64(ref e, 2), null);
                    return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ReadU16(ref byte e, int offset)
        {
            var v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref e, offset));
            return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static short ReadI16(ref byte e, int offset)
        {
            var v = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref e, offset));
            return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadI32(ref byte e, int offset)
        {
            var v = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref e, offset));
            return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ReadI64(ref byte e, int offset)
        {
            var v = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref e, offset));
            return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
        }
    }

    private interface ITickVisitor
    {
        void OnTick(in DayContext day, long ts, ReadOnlySpan<byte> entries, int count);
    }

    // Scans every existing day of kind overlapping [fromSec, toSec], oldest
    // day first, handing each in-window tick record to the visitor. Each
    // day file is read once into a pooled buffer; a day whose files vanish
    // between the listing and the read (a concurrent prune) is skipped, the
    // same as if it had never existed.
    private void ScanDays<TVisitor>(AppMetricKind kind, long fromSec, long toSec, ref TVisitor visitor)
        where TVisitor : struct, ITickVisitor
    {
        var format = FormatOf(kind);
        var recordWidth = RecordWidthOf(format);
        foreach (var day in DaysToScan(kind, fromSec, toSec))
        {
            ScanDay(kind, day, format, recordWidth, fromSec, toSec, ref visitor);
        }
    }

    private void ScanDay<TVisitor>(AppMetricKind kind, long day, SegmentFormat format, int recordWidth, long fromSec, long toSec, ref TVisitor visitor)
        where TVisitor : struct, ITickVisitor
    {
        var segPath = SegPath(kind, day);
        byte[]? buffer = null;
        try
        {
            if (!File.Exists(segPath))
            {
                return;
            }
            var idMap = AppLocalIdMap.ReadOnly(IdsPath(kind, day));
            int length;
            using (var fs = new FileStream(segPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.SequentialScan))
            {
                var fileLength = checked((int)fs.Length);
                buffer = ArrayPool<byte>.Shared.Rent(Math.Max(fileLength, 1));
                length = ReadFully(fs, buffer, fileLength);
            }

            var context = new DayContext(format, recordWidth, idMap);
            var cursor = new SegmentCursor(buffer.AsSpan(0, length), recordWidth);
            while (cursor.MoveNext())
            {
                if (cursor.Ts < fromSec || cursor.Ts > toSec)
                {
                    continue;
                }
                visitor.OnTick(in context, cursor.Ts, cursor.Entries, cursor.Count);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private IEnumerable<long> ExistingDays(AppMetricKind kind)
    {
        var dir = KindDir(kind);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<long>();
        }
        var days = new List<long>();
        foreach (var file in Directory.GetFiles(dir, "*.seg"))
        {
            if (ParseDay(file) is { } day)
            {
                days.Add(day);
            }
        }
        days.Sort();
        return days;
    }

    // Every existing day file for kind whose day number falls in
    // [FloorToDay(fromSec), FloorToDay(toSec)] - NOT every day number in
    // that range: a caller window can be (and routinely is, e.g. a query
    // with toSec=long.MaxValue meaning "everything") far wider than any data
    // that exists, and walking every day number in between would iterate
    // effectively forever. Bounding by ExistingDays keeps the walk to
    // however many days this kind actually has on disk, capped by
    // MetricsHistory.RetentionDays regardless of how wide fromSec/toSec are.
    private IEnumerable<long> DaysToScan(AppMetricKind kind, long fromSec, long toSec)
    {
        var firstDay = FloorToDay(fromSec);
        var lastDay = FloorToDay(toSec);
        return ExistingDays(kind).Where(d => d >= firstDay && d <= lastDay);
    }

    // "cpu"/"memory" resolve directly; "gpu"/"vram" (bare, no adapter id)
    // resolve with a null filter so the caller aggregates across every
    // adapter instead of one; "gpu:<gid>"/"vram:<gid>" resolve through
    // resolveGpuIndex - a gid it has never seen yields no kind at all, not
    // an error.
    private (AppMetricKind? Kind, int? GpuIndexFilter) ResolveMetric(string metric)
    {
        switch (metric)
        {
            case "cpu": return (AppMetricKind.Cpu, null);
            case "memory": return (AppMetricKind.Mem, null);
            case "storage": return (AppMetricKind.Storage, null);
            case "net": return (AppMetricKind.Net, null);
            case "storage-read": return (AppMetricKind.StorageRead, null);
            case "storage-write": return (AppMetricKind.StorageWrite, null);
            case "net-down": return (AppMetricKind.NetDown, null);
            case "net-up": return (AppMetricKind.NetUp, null);
            case "gpu": return (AppMetricKind.Gpu, null);
            case "vram": return (AppMetricKind.Vram, null);
        }
        if (metric.StartsWith("gpu:", StringComparison.Ordinal))
        {
            return _resolveGpuIndex(metric[4..]) is { } idx ? (AppMetricKind.Gpu, idx) : (null, null);
        }
        if (metric.StartsWith("vram:", StringComparison.Ordinal))
        {
            return _resolveGpuIndex(metric[5..]) is { } idx ? (AppMetricKind.Vram, idx) : (null, null);
        }
        return (null, null);
    }

    // Window ranking: per-app sum/max over the ticks the metric was sampled.
    // app_gpu_seconds/app_vram_seconds carry a gpu dimension: a bare query
    // (gpuFilter null) can have more than one adapter's entry per (app, ts)
    // - pre-aggregating per tick collapses those to one summed value before
    // ranking, so the eventual max reflects the combined-adapter peak in a
    // single tick. Per-app state lives in arrays indexed by global id (ids
    // are dense, assigned in registration order) rather than dictionaries:
    // a saturated week is tens of millions of entries, and the lookup per
    // entry is what the scan's cost is made of. Order keeps first-seen app
    // order, which is what breaks ties in the final ranking.
    private struct RankVisitor : ITickVisitor
    {
        private readonly int? _gpuFilter;
        private double[] _sum;
        private double[] _max;
        private bool[] _seen;
        private double[] _tickValue;
        private int[] _tickStamp;
        private int _stamp;
        private readonly List<int> _tickIds;
        public readonly List<int> Order;
        public readonly List<long> SampledTicks;

        public RankVisitor(int? gpuFilter, int appCapacity)
        {
            _gpuFilter = gpuFilter;
            var size = Math.Max(appCapacity, 1);
            _sum = new double[size];
            _max = new double[size];
            _seen = new bool[size];
            _tickValue = new double[size];
            _tickStamp = new int[size];
            _stamp = 0;
            _tickIds = new List<int>();
            Order = new List<int>();
            SampledTicks = new List<long>();
        }

        public double SumOf(int globalId) => _sum[globalId];
        public double MaxOf(int globalId) => _max[globalId];

        // A day file can reference an id registered after this visitor
        // sized its arrays (a flush landing mid-query); grow rather than
        // drop it.
        private void EnsureCapacity(int globalId)
        {
            if (globalId < _sum.Length)
            {
                return;
            }
            var size = Math.Max(globalId + 1, _sum.Length * 2);
            Array.Resize(ref _sum, size);
            Array.Resize(ref _max, size);
            Array.Resize(ref _seen, size);
            Array.Resize(ref _tickValue, size);
            Array.Resize(ref _tickStamp, size);
        }

        public void OnTick(in DayContext day, long ts, ReadOnlySpan<byte> entries, int count)
        {
            var stamp = ++_stamp;
            for (var i = 0; i < count; i++)
            {
                if (!day.TryReadEntry(entries, i, out var a) || (_gpuFilter is { } gf && a.GpuIndex != gf))
                {
                    continue;
                }
                EnsureCapacity(a.GlobalId);
                if (_tickStamp[a.GlobalId] != stamp)
                {
                    _tickStamp[a.GlobalId] = stamp;
                    _tickValue[a.GlobalId] = a.Value;
                    _tickIds.Add(a.GlobalId);
                }
                else
                {
                    _tickValue[a.GlobalId] += a.Value;
                }
            }
            if (_tickIds.Count == 0)
            {
                return;
            }

            SampledTicks.Add(ts);
            foreach (var id in _tickIds)
            {
                var v = _tickValue[id];
                if (!_seen[id])
                {
                    _seen[id] = true;
                    Order.Add(id);
                    _sum[id] = v;
                    _max[id] = v;
                }
                else
                {
                    _sum[id] += v;
                    _max[id] = Math.Max(_max[id], v);
                }
            }
            _tickIds.Clear();
        }
    }

    // Distinct ticks with at least one decodable entry passing the gpu
    // filter - the same tick set RankVisitor counts, so QuerySampledTicks
    // and QueryWindow agree.
    private struct SampledTicksVisitor : ITickVisitor
    {
        private readonly int? _gpuFilter;
        public readonly List<long> Ticks;

        public SampledTicksVisitor(int? gpuFilter)
        {
            _gpuFilter = gpuFilter;
            Ticks = new List<long>();
        }

        public void OnTick(in DayContext day, long ts, ReadOnlySpan<byte> entries, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (day.TryReadEntry(entries, i, out var a) && (_gpuFilter is not { } gf || a.GpuIndex == gf))
                {
                    Ticks.Add(ts);
                    return;
                }
            }
        }
    }

    // Raw per-tick points for a fixed set of global ids, one series per id;
    // an app's entries across adapters within one tick fold into one point
    // (value summed, vram summed when any side has it). Target lookup is an
    // array indexed by global id, for the same reason RankVisitor's is.
    private struct SeriesVisitor : ITickVisitor
    {
        private readonly int? _gpuFilter;
        private readonly int[] _slotByGlobalId;
        private readonly double[] _valueSum;
        private readonly double?[] _vramSum;
        private readonly bool[] _any;
        private readonly List<int> _touched;
        public readonly List<AppRawPoint>[] Points;

        public SeriesVisitor(int? gpuFilter, IReadOnlyList<int> globalIds, int appCapacity)
        {
            _gpuFilter = gpuFilter;
            var size = Math.Max(appCapacity, 1);
            foreach (var id in globalIds)
            {
                size = Math.Max(size, id + 1);
            }
            _slotByGlobalId = new int[size];
            Array.Fill(_slotByGlobalId, -1);
            for (var i = 0; i < globalIds.Count; i++)
            {
                _slotByGlobalId[globalIds[i]] = i;
            }
            _valueSum = new double[globalIds.Count];
            _vramSum = new double?[globalIds.Count];
            _any = new bool[globalIds.Count];
            _touched = new List<int>();
            Points = new List<AppRawPoint>[globalIds.Count];
            for (var i = 0; i < Points.Length; i++)
            {
                Points[i] = new List<AppRawPoint>();
            }
        }

        public void OnTick(in DayContext day, long ts, ReadOnlySpan<byte> entries, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!day.TryReadEntry(entries, i, out var a))
                {
                    continue;
                }
                // An id registered after this visitor sized its table is
                // never one of the requested (already-registered) targets.
                if ((uint)a.GlobalId >= (uint)_slotByGlobalId.Length)
                {
                    continue;
                }
                var slot = _slotByGlobalId[a.GlobalId];
                if (slot < 0 || (_gpuFilter is { } gf && a.GpuIndex != gf))
                {
                    continue;
                }
                if (!_any[slot])
                {
                    _any[slot] = true;
                    _valueSum[slot] = 0;
                    _vramSum[slot] = null;
                    _touched.Add(slot);
                }
                _valueSum[slot] += a.Value;
                _vramSum[slot] = MetricsHistory.SumNullable(_vramSum[slot], a.VramMb);
            }
            if (_touched.Count == 0)
            {
                return;
            }
            foreach (var slot in _touched)
            {
                Points[slot].Add(new AppRawPoint(ts, _valueSum[slot], _vramSum[slot]));
                _any[slot] = false;
            }
            _touched.Clear();
        }
    }

    // Earliest in-window tick carrying the target app.
    private struct FirstSeenVisitor : ITickVisitor
    {
        private readonly int _globalId;
        public long? Earliest;

        public FirstSeenVisitor(int globalId)
        {
            _globalId = globalId;
            Earliest = null;
        }

        public void OnTick(in DayContext day, long ts, ReadOnlySpan<byte> entries, int count)
        {
            if (Earliest is { } e && ts >= e)
            {
                return;
            }
            for (var i = 0; i < count; i++)
            {
                if (day.TryReadEntry(entries, i, out var a) && a.GlobalId == _globalId)
                {
                    Earliest = ts;
                    return;
                }
            }
        }
    }

    public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps) =>
        QueryWindow(metric, fromSec, toSec, maxApps).TopApps;

    /// <summary>Sampled ticks and the ranked top apps in one pass over the
    /// day files - the same two answers QuerySampledTicks + QueryTopApps
    /// give, scanned once instead of twice.</summary>
    public AppUsageWindow QueryWindow(string metric, long fromSec, long toSec, int maxApps)
    {
        var (kindOpt, gpuFilter) = ResolveMetric(metric);
        if (kindOpt is not { } kind)
        {
            return AppUsageWindow.Empty;
        }

        var effectiveFrom = Math.Max(fromSec, PruneFloorSec);
        if (toSec < effectiveFrom)
        {
            return AppUsageWindow.Empty;
        }

        var visitor = new RankVisitor(gpuFilter, _names.Names.Count);
        ScanDays(kind, effectiveFrom, toSec, ref visitor);

        var sampled = DistinctSorted(visitor.SampledTicks);
        var expectedTicks = sampled.Count;
        if (expectedTicks == 0)
        {
            return AppUsageWindow.Empty;
        }

        var stats = new List<AppWindowStat>(visitor.Order.Count);
        foreach (var id in visitor.Order)
        {
            stats.Add(new AppWindowStat(_names.GetName(id), visitor.SumOf(id) / expectedTicks, visitor.MaxOf(id)));
        }
        var top = stats.OrderByDescending(a => a.Avg).Take(maxApps).ToList();
        return new AppUsageWindow(sampled, top);
    }

    public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec)
    {
        var batch = QueryAppSeriesBatch(metric, new[] { appName }, fromSec, toSec);
        return batch.TryGetValue(appName, out var points) ? points : Array.Empty<AppRawPoint>();
    }

    /// <summary>Raw series for every name in <paramref name="appNames"/>
    /// in one pass over the day files; a name the store has never seen maps
    /// to an empty series.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<AppRawPoint>> QueryAppSeriesBatch(
        string metric, IReadOnlyCollection<string> appNames, long fromSec, long toSec)
    {
        var result = new Dictionary<string, IReadOnlyList<AppRawPoint>>(appNames.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var name in appNames)
        {
            result[name] = Array.Empty<AppRawPoint>();
        }

        var (kindOpt, gpuFilter) = ResolveMetric(metric);
        if (kindOpt is not { } kind)
        {
            return result;
        }

        var effectiveFrom = Math.Max(fromSec, PruneFloorSec);
        if (toSec < effectiveFrom)
        {
            return result;
        }

        var names = new List<string>(appNames.Count);
        var globalIds = new List<int>(appNames.Count);
        var seen = new HashSet<int>();
        foreach (var name in appNames)
        {
            if (_names.TryGetId(name) is { } globalId && seen.Add(globalId))
            {
                names.Add(name);
                globalIds.Add(globalId);
            }
        }
        if (globalIds.Count == 0)
        {
            return result;
        }

        var visitor = new SeriesVisitor(gpuFilter, globalIds, _names.Names.Count);
        ScanDays(kind, effectiveFrom, toSec, ref visitor);

        for (var i = 0; i < names.Count; i++)
        {
            var points = visitor.Points[i];
            points.Sort((a, b) => a.TsSec.CompareTo(b.TsSec));
            result[names[i]] = points;
        }
        return result;
    }

    public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec)
    {
        var (kindOpt, gpuFilter) = ResolveMetric(metric);
        if (kindOpt is not { } kind)
        {
            return Array.Empty<long>();
        }

        var effectiveFrom = Math.Max(fromSec, PruneFloorSec);
        if (toSec < effectiveFrom)
        {
            return Array.Empty<long>();
        }

        var visitor = new SampledTicksVisitor(gpuFilter);
        ScanDays(kind, effectiveFrom, toSec, ref visitor);
        return DistinctSorted(visitor.Ticks);
    }

    public long? QueryFirstSeen(string appName)
    {
        if (_names.TryGetId(appName) is not { } globalId)
        {
            return null;
        }

        long? earliest = null;
        foreach (var kind in AllKinds)
        {
            var format = FormatOf(kind);
            var recordWidth = RecordWidthOf(format);
            foreach (var day in ExistingDays(kind))
            {
                var visitor = new FirstSeenVisitor(globalId);
                ScanDay(kind, day, format, recordWidth, PruneFloorSec, long.MaxValue, ref visitor);
                if (visitor.Earliest is { } found)
                {
                    earliest = earliest is { } e ? Math.Min(e, found) : found;
                    break; // days are ascending: a later day can't be earlier.
                }
            }
        }
        return earliest;
    }

    private static List<long> DistinctSorted(List<long> ticks)
    {
        ticks.Sort();
        var write = 0;
        for (var read = 0; read < ticks.Count; read++)
        {
            if (write == 0 || ticks[read] != ticks[write - 1])
            {
                ticks[write++] = ticks[read];
            }
        }
        ticks.RemoveRange(write, ticks.Count - write);
        return ticks;
    }

    private static int ScaleX10ToInt32(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var scaled = Math.Round(value * 10);
        if (scaled <= int.MinValue)
        {
            return int.MinValue + 1;
        }
        if (scaled >= int.MaxValue)
        {
            return int.MaxValue;
        }
        return (int)scaled;
    }

    private static long ScaleWholeToInt64(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var rounded = Math.Round(value);
        if (rounded <= long.MinValue)
        {
            return long.MinValue + 1;
        }
        if (rounded >= long.MaxValue)
        {
            return long.MaxValue;
        }
        return (long)rounded;
    }

    private static int RoundToMb(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var rounded = Math.Round(value);
        if (rounded <= int.MinValue)
        {
            return int.MinValue + 1;
        }
        if (rounded >= int.MaxValue)
        {
            return int.MaxValue;
        }
        return (int)rounded;
    }

    private static int ScaleVramMb(double? value) => value is { } v ? RoundToMb(v) : int.MinValue;

    private static double? UnscaleVramMb(int raw) => raw == int.MinValue ? null : raw;

    private void PersistPruneFloor(long floor)
    {
        // Write-then-atomic-rename rather than an in-place write: File.Move
        // with overwrite is an atomic rename on both Windows and POSIX, so a
        // crash mid-write only ever leaves the OLD complete value in place,
        // never a torn one.
        var tmp = _pruneFloorPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buf, floor);
            fs.Write(buf);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _pruneFloorPath, overwrite: true);
    }

    private static long LoadPruneFloor(string path)
    {
        if (!File.Exists(path))
        {
            return RingFile.UnwrittenStamp;
        }
        var bytes = File.ReadAllBytes(path);
        return bytes.Length == 8 ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : RingFile.UnwrittenStamp;
    }

    public void Dispose() => _names.Dispose();
}
