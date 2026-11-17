using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Nexus.Service.Platform;

namespace Nexus.Service.Diagnostics;

public enum DeviceEventKind : byte
{
    WriteOk,
    WriteFail,
    Connect,
    Disconnect,
    Stall,
    Failing,
}

/// <summary>
/// A fixed-size ring of recent events per device, kept in memory. When a device drops, stalls or starts failing
/// writes, its ring is appended as text to logs/device-incidents.log (a file, so it survives restarts and ships in
/// the support bundle). Devices are named by handler id plus a short hash of their key, never the raw key. File writes run on one background task so device threads never block on I/O.
/// </summary>
public sealed class DeviceIncidentLog
{
    public const int RingSize = 128;
    public const string FileName = "device-incidents.log";
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>The gap required after a device's first snapshot of one kind; each further snapshot doubles it up to <see cref="MaxSnapshotGap"/>, so a flapping device cannot fill the file and evict its first incident.</summary>
    public static readonly TimeSpan MinSnapshotGap = TimeSpan.FromSeconds(30);

    /// <summary>The longest required gap, and the span with no attempt at all (written or refused) after which the ladder starts over.</summary>
    public static readonly TimeSpan MaxSnapshotGap = TimeSpan.FromHours(1);

    /// <summary>Steady per-frame successes fold into one entry per this span so they do not evict the failures.</summary>
    private const long OkMergeMs = 1000;

    public static DeviceIncidentLog Shared { get; } = CreateShared();

    /// <summary>Redirects <see cref="Shared"/>'s file; the test assembly sets it so suites that drive the shared instance never write into a real logs directory.</summary>
    internal static Func<string>? SharedDirectoryOverride { get; set; }

    private static DeviceIncidentLog CreateShared()
    {
        var log = new DeviceIncidentLog(() => SharedDirectoryOverride?.Invoke() ?? ServiceLog.LogsDirectory, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => log.WaitIdle(TimeSpan.FromSeconds(2));
        return log;
    }

    private struct Entry
    {
        public long AtMs;
        public DeviceEventKind Kind;
        public int Count;
        /// <summary>Successes that shared a report window with the failures counted in <see cref="Count"/>.</summary>
        public int OkCount;
        public int LatencyMs;
        public int Code;
    }

    private sealed class Ring
    {
        public readonly Entry[] Entries = new Entry[RingSize];
        public int Next;
        public int Count;
        public string Name = "device";
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Ring> _rings = new(StringComparer.Ordinal);
    private readonly Func<string> _dir;
    private readonly Func<long> _nowMs;
    private readonly record struct Throttle(long LastAttemptMs, long LastWrittenMs, int Written);
    // Keyed by device and snapshot kind: a drop after a write-failure snapshot must not be throttled by it.
    private readonly Dictionary<string, Throttle> _throttle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Throttle?> _priorThrottle = new(StringComparer.Ordinal);
    private readonly System.Threading.Channels.Channel<string> _queue =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(64) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly object _writerLock = new();
    private bool _writerStarted;
    private int _pending;

    public DeviceIncidentLog(Func<string> logsDirectory, Func<long> nowMs)
    {
        _dir = logsDirectory;
        _nowMs = nowMs;
    }

    /// <summary>Records one event. Allocates only the first time a key is seen.</summary>
    public void Note(string key, string? name, DeviceEventKind kind, int latencyMs = 0, int code = 0, int count = 1, int okCount = 0)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }
        var now = _nowMs();
        lock (_lock)
        {
            if (!_rings.TryGetValue(key, out var ring))
            {
                ring = new Ring();
                _rings[key] = ring;
            }
            if (name is not null)
            {
                ring.Name = name;
            }
            if (kind == DeviceEventKind.WriteOk && ring.Count > 0)
            {
                ref var last = ref ring.Entries[(ring.Next + RingSize - 1) % RingSize];
                if (last.Kind == DeviceEventKind.WriteOk && now - last.AtMs < OkMergeMs)
                {
                    last.Count += count;
                    last.LatencyMs = Math.Max(last.LatencyMs, latencyMs);
                    return;
                }
            }
            ref var slot = ref ring.Entries[ring.Next];
            slot.AtMs = now;
            slot.Kind = kind;
            slot.Count = count;
            slot.OkCount = okCount;
            slot.LatencyMs = latencyMs;
            slot.Code = code;
            ring.Next = (ring.Next + 1) % RingSize;
            if (ring.Count < RingSize)
            {
                ring.Count++;
            }
        }
    }

    /// <summary>Drops a device's ring. When it held entries, records a disconnect and snapshots first; a key with no ring writes nothing.</summary>
    public void Disconnect(string key, string reason)
    {
        string? text;
        lock (_lock)
        {
            if (!_rings.TryGetValue(key, out var ring))
            {
                return;
            }
            _rings.Remove(key);
            if (ring.Count == 0)
            {
                return;
            }
            var now = _nowMs();
            ref var slot = ref ring.Entries[ring.Next];
            slot.AtMs = now;
            slot.Kind = DeviceEventKind.Disconnect;
            slot.Count = 1;
            slot.OkCount = 0;
            slot.LatencyMs = 0;
            slot.Code = 0;
            ring.Next = (ring.Next + 1) % RingSize;
            if (ring.Count < RingSize)
            {
                ring.Count++;
            }
            text = TryFormat(ring, key, reason, now, disconnect: true);
        }
        Enqueue(text, key + DisconnectSuffix);
    }

    /// <summary>Drops a device's ring without recording anything, for a device Nexus released rather than lost.</summary>
    public void Discard(string key)
    {
        lock (_lock)
        {
            _rings.Remove(key);
        }
    }

    /// <summary>Queues the device's ring for the file unless one was queued for it within <see cref="MinSnapshotGap"/>. Returns whether it queued.</summary>
    public bool Snapshot(string key, string reason)
    {
        string? text;
        lock (_lock)
        {
            if (!_rings.TryGetValue(key, out var ring) || ring.Count == 0)
            {
                return false;
            }
            text = TryFormat(ring, key, reason, _nowMs(), disconnect: false);
        }
        return Enqueue(text, key + SnapshotSuffix);
    }

    private const string DisconnectSuffix = "|d";
    private const string SnapshotSuffix = "|s";

    // Caller holds _lock. The throttle outlives the ring so a re-created ring cannot bypass it.
    private string? TryFormat(Ring ring, string key, string reason, long now, bool disconnect)
    {
        var tkey = key + (disconnect ? DisconnectSuffix : SnapshotSuffix);
        var minGap = (long)MinSnapshotGap.TotalMilliseconds;
        var maxGap = (long)MaxSnapshotGap.TotalMilliseconds;
        var written = 0;
        if (_throttle.TryGetValue(tkey, out var last))
        {
            // Refused attempts count as activity: a device that keeps flapping never earns a reset.
            written = now - last.LastAttemptMs >= maxGap ? 0 : last.Written;
            var required = written == 0 ? 0 : Math.Min(minGap << Math.Min(written - 1, 20), maxGap);
            if (written > 0 && now - last.LastWrittenMs < required)
            {
                _throttle[tkey] = last with { LastAttemptMs = now };
                return null;
            }
        }
        _priorThrottle[tkey] = _throttle.TryGetValue(tkey, out var prior) ? prior : null;
        _throttle[tkey] = new Throttle(now, now, written + 1);
        return Format(ring, key, reason, now);
    }

    private bool Enqueue(string? text, string key)
    {
        if (text is null)
        {
            return false;
        }
        lock (_writerLock)
        {
            if (!_writerStarted)
            {
                _writerStarted = true;
                Task.Run(DrainAsync);
            }
        }
        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(text))
        {
            return true;
        }
        Done();
        // The snapshot was never written, so it must not throttle the next one.
        lock (_lock)
        {
            if (_priorThrottle.GetValueOrDefault(key) is { } prior)
            {
                _throttle[key] = prior;
            }
            else
            {
                _throttle.Remove(key);
            }
        }
        return false;
    }

    private async Task DrainAsync()
    {
        await foreach (var text in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Append(text);
            }
            catch (Exception)
            {
                // A failed write drops that snapshot; the loop must outlive it.
            }
            Done();
        }
    }

    private void Done()
    {
        lock (_writerLock)
        {
            Interlocked.Decrement(ref _pending);
            Monitor.PulseAll(_writerLock);
        }
    }

    internal int Pending => Volatile.Read(ref _pending);

    /// <summary>Blocks until queued snapshots are on disk or <paramref name="timeout"/> passes.</summary>
    internal void WaitIdle(TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        lock (_writerLock)
        {
            while (Volatile.Read(ref _pending) > 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return;
                }
                Monitor.Wait(_writerLock, (int)left);
            }
        }
    }

    /// <summary>Name plus a short hash of the key, so two units of one model stay distinguishable without exposing a serial.</summary>
    public static string Label(string name, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"{name}#{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}";
    }

    // Caller holds _lock.
    private static string Format(Ring ring, string key, string reason, long now)
    {
        var sb = new StringBuilder();
        sb.Append("=== ").Append(DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
            .Append(' ').Append(Label(ring.Name, key)).Append(' ').Append(reason).Append(" (").Append(ring.Count).Append(" entries)\n");
        var start = ring.Count < RingSize ? 0 : ring.Next;
        for (var i = 0; i < ring.Count; i++)
        {
            var e = ring.Entries[(start + i) % RingSize];
            sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(e.AtMs).ToString("HH:mm:ss.fff")).Append(' ')
                .Append(e.Kind).Append(" n=").Append(e.Count).Append(e.OkCount > 0 ? " ok=" + e.OkCount : "").Append(" lat=").Append(e.LatencyMs).Append("ms code=").Append(e.Code).Append('\n');
        }
        return sb.ToString();
    }

    private void Append(string text)
    {
        var dir = _dir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
        {
            File.Move(path, Path.Combine(dir, "device-incidents.1.log"), overwrite: true);
        }
        File.AppendAllText(path, text, Encoding.UTF8);
    }
}
