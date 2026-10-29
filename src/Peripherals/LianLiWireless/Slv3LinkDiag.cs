using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>What one GetDev round trip produced.</summary>
internal enum Slv3ReplyKind : byte
{
    Ok,
    /// <summary>A valid reply listing zero records.</summary>
    Empty,
    /// <summary>First byte 0: the RX has no device list this cycle.</summary>
    NoData,
    /// <summary>The read returned nothing before its timeout.</summary>
    Timeout,
    /// <summary>Bytes arrived but too short or without the command echo.</summary>
    Invalid,
    SendFail,
}

/// <summary>Why an upload failed; NotPrepared covers an unknown or unbound chain and a compression refusal.</summary>
internal enum Slv3UploadReason : byte
{
    Ok,
    NotPrepared,
    TxFail,
    TxTimeout,
}

internal enum Slv3UploadKind : byte
{
    Frame,
    Animation,
    Window,
}

/// <summary>
/// Always-on diagnostics for the wireless link: separates "the chain left the RX
/// device list while replies kept arriving" from "replies stalled" and "our uploads
/// failed or slowed". State is memory-only and bounded; every line goes to a queue
/// that one background writer appends to <c>lianli-link.log</c>.
/// </summary>
internal sealed class Slv3LinkDiag : IDisposable
{
    internal const string FileName = "lianli-link.log";
    internal const string RotatedFileName = "lianli-link.1.log";
    internal const long MaxFileBytes = 2L * 1024 * 1024;
    internal const int ChainRingSize = 128;
    internal const int ReplyRingSize = 256;
    internal const int UploadRingSize = 48;
    internal const int ReplyKeepBytes = 512;
    internal const long SummaryIntervalMs = 30_000;
    internal const long EarlyDumpGapMs = 5_000;
    internal const long DumpMinIntervalMs = 60_000;
    internal const long ReplyWindowMs = 60_000;
    private const int MaxQueuedLines = 4000;
    private const int RawDumpCount = 3;

    private readonly object _gate = new();
    private readonly Dictionary<string, Chain> _chains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Reply _replies = new();
    private long _pollSeq;
    private long _nextSummaryMs = -1;
    private int _dumpId;

    private readonly ConcurrentQueue<string> _lines = new();
    private int _queued;
    private int _droppedLines;
    private readonly ConcurrentQueue<string> _parentRequests = new();
    private Func<string, string?>? _parentResolver;
    private string? _lastParent;
    private string? _lastTxPath;
    private string? _lastRxPath;
    private bool _usbKnown;

    private string? _dir;
    private Thread? _writer;
    private AutoResetEvent? _wake;
    private volatile bool _stop;

    /// <summary>Starts the background writer that owns all file I/O. Idempotent.</summary>
    public void StartWriter(string directory, Func<string, string?>? parentResolver = null)
    {
        if (_writer is not null)
        {
            return;
        }
        _dir = directory;
        _parentResolver = parentResolver;
        _wake = new AutoResetEvent(false);
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "slv3-link-diag" };
        _writer.Start();
    }

    // ---- recording (called from the poll and upload paths; memory only) ----

    public void OnReply(long nowMs, ReadOnlySpan<byte> reply, int latencyMs, Slv3ReplyKind kind, int records)
    {
        lock (_gate)
        {
            _pollSeq++;
            _replies.Add(nowMs, reply, latencyMs, kind, records);
        }
    }

    /// <summary>A chain's record is present in the reply being processed.</summary>
    public void OnSeen(string key, long nowMs, ReadOnlySpan<byte> record, byte slot, byte channel)
    {
        lock (_gate)
        {
            if (!_chains.TryGetValue(key, out var c))
            {
                c = new Chain(key);
                _chains[key] = c;
            }
            var reappeared = c.Dropped;
            if (c.LastSeenMs >= 0)
            {
                c.CloseGap(nowMs - c.LastSeenMs);
            }
            c.LastSeenMs = nowMs;
            c.LastPollSeq = _pollSeq;
            c.Add(nowMs, true, slot, channel, record);
            if (reappeared)
            {
                c.Dropped = false;
                DumpLocked(c, nowMs, "reappeared", 0);
            }
        }
    }

    /// <summary>
    /// Ends a poll. With a device list, tracked chains absent from it are recorded as such;
    /// either way the unseen-gap trigger and the 30 s summary are evaluated.
    /// </summary>
    public void EndPoll(long nowMs, bool gotList)
    {
        lock (_gate)
        {
            if (_nextSummaryMs < 0)
            {
                _nextSummaryMs = nowMs + SummaryIntervalMs;
            }
            foreach (var c in _chains.Values)
            {
                if (c.Dropped)
                {
                    continue;
                }
                if (gotList)
                {
                    c.PolledTotal++;
                    if (c.LastPollSeq == _pollSeq)
                    {
                        c.PolledSeen++;
                    }
                    else
                    {
                        c.Add(nowMs, false, 0, 0, default);
                    }
                }
                var unseen = nowMs - c.LastSeenMs;
                var seenNow = gotList && c.LastPollSeq == _pollSeq;
                if (!seenNow && unseen > EarlyDumpGapMs && nowMs >= c.NextDumpMs)
                {
                    DumpLocked(c, nowMs, "unseen5s", unseen);
                }
            }
            if (nowMs >= _nextSummaryMs)
            {
                _nextSummaryMs = nowMs + SummaryIntervalMs;
                foreach (var c in _chains.Values)
                {
                    if (!c.Dropped)
                    {
                        SummaryLocked(c, nowMs);
                    }
                }
            }
        }
    }

    public void OnDropped(string key, long nowMs)
    {
        lock (_gate)
        {
            if (_chains.TryGetValue(key, out var c) && !c.Dropped)
            {
                var unseen = nowMs - c.LastSeenMs;
                c.CloseGap(unseen);
                DumpLocked(c, nowMs, "drop", unseen);
                c.Dropped = true;
            }
        }
    }

    public void OnUpload(string key, long nowMs, Slv3UploadKind kind, bool ok, int latencyMs, long windowId, Slv3UploadReason reason)
    {
        lock (_gate)
        {
            if (_chains.TryGetValue(key, out var c))
            {
                c.AddUpload(nowMs, kind, ok, latencyMs, windowId, reason);
            }
        }
    }

    /// <summary>Dongle presence. Logs only when the discovered pair changes; the parent lookup runs on the writer.</summary>
    public void NoteUsb(Slv3PortInfo? tx, Slv3PortInfo? rx)
    {
        var txPath = tx?.PortName;
        var rxPath = rx?.PortName;
        if (_usbKnown && txPath == _lastTxPath && rxPath == _lastRxPath)
        {
            return;
        }
        var txChanged = !_usbKnown || txPath != _lastTxPath;
        _usbKnown = true;
        _lastTxPath = txPath;
        _lastRxPath = rxPath;
        Event($"usb tx={Describe(tx)} rx={Describe(rx)}");
        if (txChanged && txPath is not null)
        {
            _parentRequests.Enqueue(txPath);
            _wake?.Set();
        }
    }

    // Poll-path dumps and summaries are picked up by the writer's 1 s cadence; only events wake it.
    public void Event(string text)
    {
        Emit("EVT " + text);
        _wake?.Set();
    }

    private static string Describe(Slv3PortInfo? port) =>
        port is null ? "absent" : "present" + (port.Serial.Length > 0 ? " ser#" + HashPart(port.Serial) : "");

    internal static string HashPart(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 4).ToLowerInvariant();

    /// <summary>A generated instance id (contains '&amp;') identifies a port and is kept; anything else may be a serial and is hashed.</summary>
    internal static string RedactInstanceId(string instanceId)
    {
        var cut = instanceId.LastIndexOf('\\');
        if (cut < 0)
        {
            return instanceId;
        }
        var tail = instanceId[(cut + 1)..];
        return tail.Contains('&') ? instanceId : instanceId[..(cut + 1)] + "ser#" + HashPart(tail);
    }

    // ---- line production (caller holds _gate) ----

    private void SummaryLocked(Chain c, long nowMs)
    {
        var max = c.MaxGapMs;
        max = Math.Max(max, nowMs - c.LastSeenMs);
        Emit(string.Create(CultureInfo.InvariantCulture,
            $"SUM {c.Key} polls={c.PolledSeen}/{c.PolledTotal} maxgap={max}ms g2={c.Gaps2} g5={c.Gaps5} g10={c.Gaps10} slot={c.LastSlot} ch={c.LastChannel}"));
        c.ResetWindow();
    }

    private void DumpLocked(Chain c, long nowMs, string reason, long unseenMs)
    {
        c.NextDumpMs = nowMs + DumpMinIntervalMs;
        var id = ++_dumpId;
        var sb = new StringBuilder();
        var p = "D" + id.ToString(CultureInfo.InvariantCulture) + " ";
        var inv = CultureInfo.InvariantCulture;
        Emit(string.Create(inv, $"DUMP {id} chain={c.Key} reason={reason} unseen={unseenMs}ms win polls={c.PolledSeen}/{c.PolledTotal} maxgap={Math.Max(c.MaxGapMs, unseenMs)}ms g2={c.Gaps2} g5={c.Gaps5} g10={c.Gaps10}"));

        // Chain ring, run-length grouped by (present, slot, channel).
        var n = c.Count;
        for (var i = 0; i < n;)
        {
            var a = c.Index(i);
            var j = i + 1;
            while (j < n && c.Index(j) is var b && c.Present[b] == c.Present[a] && c.Slot[b] == c.Slot[a] && c.Chan[b] == c.Chan[a])
            {
                j++;
            }
            var last = c.Index(j - 1);
            sb.Clear();
            sb.Append(p).Append("CH t=").Append(c.T[a] - nowMs).Append("..").Append(c.T[last] - nowMs).Append("ms ")
              .Append(c.Present[a] ? "present" : "absent").Append(" x").Append(j - i);
            if (c.Present[a])
            {
                sb.Append(" slot=").Append(c.Slot[a]).Append(" ch=").Append(c.Chan[a]);
            }
            Emit(sb.ToString());
            i = j;
        }
        var raws = 0;
        for (var i = n - 1; i >= 0 && raws < RawDumpCount; i--)
        {
            var x = c.Index(i);
            if (!c.Present[x])
            {
                continue;
            }
            raws++;
            Emit(p + "CHRAW t=" + (c.T[x] - nowMs).ToString(inv) + "ms "
                + Convert.ToHexString(c.Raw, x * Chain.RecordBytes, Chain.RecordBytes));
        }

        // Dongle reply ring, last ReplyWindowMs, grouped by (kind, records, length).
        var r = _replies;
        var rn = r.Count;
        var first = 0;
        while (first < rn && nowMs - r.T[r.Index(first)] > ReplyWindowMs)
        {
            first++;
        }
        for (var i = first; i < rn;)
        {
            var a = r.Index(i);
            var j = i + 1;
            long latSum = r.Lat[a];
            var latMax = r.Lat[a];
            while (j < rn && r.Index(j) is var b && r.Kind[b] == r.Kind[a] && r.Records[b] == r.Records[a] && r.Len[b] == r.Len[a])
            {
                latSum += r.Lat[b];
                latMax = Math.Max(latMax, r.Lat[b]);
                j++;
            }
            var lastR = r.Index(j - 1);
            sb.Clear();
            sb.Append(p).Append("RX t=").Append(r.T[a] - nowMs).Append("..").Append(r.T[lastR] - nowMs).Append("ms ")
              .Append(KindName(r.Kind[a])).Append(" x").Append(j - i).Append(" records=").Append(r.Records[a])
              .Append(" len=").Append(r.Len[a]).Append(" lat avg=").Append(latSum / (j - i)).Append(" max=").Append(latMax).Append("ms");
            Emit(sb.ToString());
            i = j;
        }
        var rawsR = 0;
        for (var i = rn - 1; i >= first && rawsR < RawDumpCount; i--)
        {
            var x = r.Index(i);
            if (r.Len[x] == 0)
            {
                continue;
            }
            rawsR++;
            var kept = Math.Min(r.Len[x], ReplyKeepBytes);
            Emit(p + "RXRAW t=" + (r.T[x] - nowMs).ToString(inv) + "ms len=" + r.Len[x].ToString(inv)
                + (kept < r.Len[x] ? " cut=" + kept.ToString(inv) : "") + " "
                + Convert.ToHexString(r.Raw, x * ReplyKeepBytes, kept));
        }

        // Upload ring, oldest first.
        for (var i = 0; i < c.UploadCount; i++)
        {
            var u = c.UploadIndex(i);
            Emit(p + "UP t=" + (c.UT[u] - nowMs).ToString(inv) + "ms " + c.UKind[u].ToString().ToLowerInvariant()
                + (c.UOk[u] ? " ok" : " FAIL reason=" + ReasonName(c.UReason[u])) + " lat=" + c.ULat[u].ToString(inv) + "ms"
                + (c.UWindow[u] >= 0 ? " window=" + c.UWindow[u].ToString(inv) : ""));
        }
    }

    private static string ReasonName(Slv3UploadReason r) => r switch
    {
        Slv3UploadReason.NotPrepared => "not-prepared",
        Slv3UploadReason.TxFail => "tx-fail",
        Slv3UploadReason.TxTimeout => "tx-timeout",
        _ => "ok",
    };

    private static string KindName(Slv3ReplyKind k) => k switch
    {
        Slv3ReplyKind.Ok => "ok",
        Slv3ReplyKind.Empty => "empty",
        Slv3ReplyKind.NoData => "nodata",
        Slv3ReplyKind.Timeout => "timeout",
        Slv3ReplyKind.Invalid => "invalid",
        _ => "sendfail",
    };

    // ---- output ----

    private void Emit(string body)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueuedLines)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _droppedLines);
            return;
        }
        _lines.Enqueue(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " " + body);
    }

    /// <summary>Resolves pending parent lookups, then hands every queued line to <paramref name="sink"/>. Returns the line count.</summary>
    internal int Drain(Action<string> sink)
    {
        while (_parentRequests.TryDequeue(out var path))
        {
            string? parent = null;
            try
            {
                parent = _parentResolver?.Invoke(path);
            }
            catch
            {
                // diagnostics must never throw
            }
            if (!string.IsNullOrEmpty(parent))
            {
                var redacted = RedactInstanceId(parent);
                if (redacted != _lastParent)
                {
                    _lastParent = redacted;
                    Emit("EVT usb-parent " + redacted);
                }
            }
        }
        var count = 0;
        while (_lines.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _queued);
            sink(line);
            count++;
        }
        var dropped = Interlocked.Exchange(ref _droppedLines, 0);
        if (dropped > 0)
        {
            sink(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " EVT dropped-lines " + dropped.ToString(CultureInfo.InvariantCulture));
            count++;
        }
        return count;
    }

    private void WriterLoop()
    {
        var sb = new StringBuilder();
        while (!_stop)
        {
            _wake!.WaitOne(1000);
            Flush(sb);
        }
        Flush(sb);
    }

    private void Flush(StringBuilder sb)
    {
        sb.Clear();
        Drain(line => sb.Append(line).Append('\n'));
        if (sb.Length == 0 || _dir is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, FileName);
            var text = sb.ToString();
            var size = new FileInfo(path) is { Exists: true } fi ? fi.Length : 0;
            if (size + text.Length > MaxFileBytes)
            {
                File.Move(path, Path.Combine(_dir, RotatedFileName), overwrite: true);
            }
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // a full or locked disk must not affect the link
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake?.Set();
        _writer?.Join(2000);
    }

    // ---- ring storage ----

    private sealed class Chain
    {
        internal const int RecordBytes = Slv3Protocol.RecordLength;

        public Chain(string key) => Key = key;

        public string Key { get; }
        public long LastSeenMs = -1;
        public long LastPollSeq = -1;
        public long NextDumpMs;
        public bool Dropped;
        public byte LastSlot;
        public byte LastChannel;

        public int PolledSeen;
        public int PolledTotal;
        public long MaxGapMs;
        public int Gaps2;
        public int Gaps5;
        public int Gaps10;

        public readonly long[] T = new long[ChainRingSize];
        public readonly bool[] Present = new bool[ChainRingSize];
        public readonly byte[] Slot = new byte[ChainRingSize];
        public readonly byte[] Chan = new byte[ChainRingSize];
        public readonly byte[] Raw = new byte[ChainRingSize * RecordBytes];
        private int _head;
        public int Count;

        public readonly long[] UT = new long[UploadRingSize];
        public readonly Slv3UploadKind[] UKind = new Slv3UploadKind[UploadRingSize];
        public readonly bool[] UOk = new bool[UploadRingSize];
        public readonly int[] ULat = new int[UploadRingSize];
        public readonly long[] UWindow = new long[UploadRingSize];
        public readonly Slv3UploadReason[] UReason = new Slv3UploadReason[UploadRingSize];
        private int _uHead;
        public int UploadCount;

        public int Index(int i) => (_head - Count + i + ChainRingSize) % ChainRingSize;
        public int UploadIndex(int i) => (_uHead - UploadCount + i + UploadRingSize) % UploadRingSize;

        public void Add(long nowMs, bool present, byte slot, byte channel, ReadOnlySpan<byte> record)
        {
            var h = _head;
            T[h] = nowMs;
            Present[h] = present;
            Slot[h] = slot;
            Chan[h] = channel;
            if (present)
            {
                LastSlot = slot;
                LastChannel = channel;
                record[..Math.Min(record.Length, RecordBytes)].CopyTo(Raw.AsSpan(h * RecordBytes, RecordBytes));
            }
            _head = (h + 1) % ChainRingSize;
            if (Count < ChainRingSize)
            {
                Count++;
            }
        }

        public void AddUpload(long nowMs, Slv3UploadKind kind, bool ok, int latencyMs, long windowId, Slv3UploadReason reason)
        {
            var h = _uHead;
            UT[h] = nowMs;
            UKind[h] = kind;
            UOk[h] = ok;
            ULat[h] = latencyMs;
            UWindow[h] = windowId;
            UReason[h] = reason;
            _uHead = (h + 1) % UploadRingSize;
            if (UploadCount < UploadRingSize)
            {
                UploadCount++;
            }
        }

        public void CloseGap(long gapMs)
        {
            MaxGapMs = Math.Max(MaxGapMs, gapMs);
            if (gapMs > 2_000) Gaps2++;
            if (gapMs > 5_000) Gaps5++;
            if (gapMs > 10_000) Gaps10++;
        }

        public void ResetWindow()
        {
            PolledSeen = 0;
            PolledTotal = 0;
            MaxGapMs = 0;
            Gaps2 = 0;
            Gaps5 = 0;
            Gaps10 = 0;
        }
    }

    private sealed class Reply
    {
        public readonly long[] T = new long[ReplyRingSize];
        public readonly Slv3ReplyKind[] Kind = new Slv3ReplyKind[ReplyRingSize];
        public readonly int[] Lat = new int[ReplyRingSize];
        public readonly int[] Records = new int[ReplyRingSize];
        public readonly int[] Len = new int[ReplyRingSize];
        public readonly byte[] Raw = new byte[ReplyRingSize * ReplyKeepBytes];
        private int _head;
        public int Count;

        public int Index(int i) => (_head - Count + i + ReplyRingSize) % ReplyRingSize;

        public void Add(long nowMs, ReadOnlySpan<byte> reply, int latencyMs, Slv3ReplyKind kind, int records)
        {
            var h = _head;
            T[h] = nowMs;
            Kind[h] = kind;
            Lat[h] = latencyMs;
            Records[h] = records;
            Len[h] = reply.Length;
            reply[..Math.Min(reply.Length, ReplyKeepBytes)].CopyTo(Raw.AsSpan(h * ReplyKeepBytes, ReplyKeepBytes));
            _head = (h + 1) % ReplyRingSize;
            if (Count < ReplyRingSize)
            {
                Count++;
            }
        }
    }
}
