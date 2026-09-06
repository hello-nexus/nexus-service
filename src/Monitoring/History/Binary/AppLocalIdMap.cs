using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Per-(metric kind, UTC day) remap of AppNameDictionary's global app id to a
/// compact local id, assigned in first-seen-that-day order - the "16-bit
/// per-day local app id" size lever the app-data design calls for: a day
/// segment's tick records store this 2-byte local id instead of a wider
/// global id, and the remap resets every day so the local id space never
/// depends on how many distinct names the service has ever seen in its
/// lifetime, only how many it saw that one day.
///
/// On-disk format is a flat array of 4-byte global ids, append-only, index
/// == local id (no header, no length prefix - the file length alone gives
/// the record count). AppUsageStore is the only writer, and always follows
/// the write order in <see cref="LoadForWrite"/>'s doc: flush this map
/// BEFORE the day segment that references its local ids, so a crash between
/// the two can only leave this map with unreferenced extra entries, never a
/// segment record pointing past the end of it.
/// </summary>
internal sealed class AppLocalIdMap
{
    private const int RecordBytes = 4;

    private readonly string _path;
    private readonly List<int> _globalIds;
    private readonly Dictionary<int, int> _localByGlobal;
    private int _flushedCount;

    private AppLocalIdMap(string path, List<int> globalIds)
    {
        _path = path;
        _globalIds = globalIds;
        _flushedCount = globalIds.Count;
        _localByGlobal = new Dictionary<int, int>(globalIds.Count);
        for (var i = 0; i < globalIds.Count; i++)
        {
            _localByGlobal[globalIds[i]] = i;
        }
    }

    /// <summary>Loads path for the single writer's use: a torn trailing
    /// record (a previous crash mid-append, not a clean multiple of
    /// RecordBytes) is truncated away immediately, so this call's own
    /// appends start from a clean boundary. Safe only because AppUsageStore
    /// serializes every Append - a concurrent reader must use
    /// <see cref="ReadOnly"/> instead, which never mutates the file.</summary>
    public static AppLocalIdMap LoadForWrite(string path)
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        var cleanCount = bytes.Length / RecordBytes;
        var globalIds = new List<int>(cleanCount);
        for (var i = 0; i < cleanCount; i++)
        {
            globalIds.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * RecordBytes, RecordBytes)));
        }

        var cleanLength = cleanCount * RecordBytes;
        if (cleanLength != bytes.Length)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(cleanLength);
        }
        return new AppLocalIdMap(path, globalIds);
    }

    /// <summary>Reads path read-only for a query: parses only whole
    /// RecordBytes-sized records and silently ignores a torn trailing one,
    /// without touching the file - a concurrent writer may be mid-append, and
    /// only the writer is allowed to truncate (see LoadForWrite).</summary>
    public static int[] ReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<int>();
        }

        var bytes = File.ReadAllBytes(path);
        var count = bytes.Length / RecordBytes;
        var ids = new int[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * RecordBytes, RecordBytes));
        }
        return ids;
    }

    /// <summary>The local id for globalId if already assigned that day, or
    /// null.</summary>
    public int? TryGetLocalId(int globalId) => _localByGlobal.TryGetValue(globalId, out var local) ? local : null;

    /// <summary>Assigns a new local id for globalId if unseen this day, or
    /// returns the existing one. Returns null - registering nothing - once
    /// this day already holds ushort.MaxValue distinct apps: a day with over
    /// 65535 distinct process names never happens in practice, so this only
    /// bounds the local id field width rather than reflecting a real
    /// limit.</summary>
    public int? GetOrAdd(int globalId)
    {
        if (_localByGlobal.TryGetValue(globalId, out var existing))
        {
            return existing;
        }
        if (_globalIds.Count > ushort.MaxValue)
        {
            return null;
        }

        var localId = _globalIds.Count;
        _globalIds.Add(globalId);
        _localByGlobal[globalId] = localId;
        return localId;
    }

    /// <summary>Brings the file up to this instance's map: appends the ids
    /// assigned since the previous Flush, or rewrites the whole map when the
    /// file no longer holds exactly the ids already flushed (a partial
    /// write, or the file deleted or truncated underneath a running
    /// store). The in-memory map is authoritative - AppUsageStore is the
    /// single writer - so a rewrite restores what the day's segment
    /// records reference.</summary>
    public void Flush()
    {
        var expectedLength = (long)_flushedCount * RecordBytes;
        var onDisk = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        if (onDisk != expectedLength)
        {
            RewriteAll();
            return;
        }
        if (_globalIds.Count == _flushedCount)
        {
            return;
        }

        using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        Span<byte> buf = stackalloc byte[RecordBytes];
        for (var i = _flushedCount; i < _globalIds.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf, _globalIds[i]);
            fs.Write(buf);
        }
        fs.Flush(flushToDisk: true);
        _flushedCount = _globalIds.Count;
    }

    // Write-then-rename so a crash mid-rewrite leaves the old file intact
    // rather than a map shorter than the records that reference it.
    private void RewriteAll()
    {
        if (_globalIds.Count == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            _flushedCount = 0;
            return;
        }

        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new byte[_globalIds.Count * RecordBytes];
            for (var i = 0; i < _globalIds.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * RecordBytes, RecordBytes), _globalIds[i]);
            }
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
        _flushedCount = _globalIds.Count;
    }
}
