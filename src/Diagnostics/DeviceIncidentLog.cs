using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
/// the support bundle). Devices are named by model plus a short hash of their id, never the raw id.
/// </summary>
public sealed class DeviceIncidentLog
{
    public const int RingSize = 128;
    public const string FileName = "device-incidents.log";
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>At most one snapshot per device in this span, so a flapping device cannot spam the file.</summary>
    public static readonly TimeSpan MinSnapshotGap = TimeSpan.FromSeconds(30);

    /// <summary>Steady per-frame successes fold into one entry per this span so they do not evict the failures.</summary>
    private const long OkMergeMs = 1000;

    public static DeviceIncidentLog Shared { get; } = new(() => ServiceLog.LogsDirectory, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private struct Entry
    {
        public long AtMs;
        public DeviceEventKind Kind;
        public int Count;
        public int LatencyMs;
        public int Code;
    }

    private sealed class Ring
    {
        public readonly Entry[] Entries = new Entry[RingSize];
        public int Next;
        public int Count;
        public string Name = "device";
        public long LastSnapshotMs = long.MinValue;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Ring> _rings = new(StringComparer.Ordinal);
    private readonly Func<string> _dir;
    private readonly Func<long> _nowMs;
    private readonly object _fileLock = new();

    public DeviceIncidentLog(Func<string> logsDirectory, Func<long> nowMs)
    {
        _dir = logsDirectory;
        _nowMs = nowMs;
    }

    /// <summary>Records one event. Allocates only the first time a key is seen.</summary>
    public void Note(string key, string? name, DeviceEventKind kind, int latencyMs = 0, int code = 0, int count = 1)
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
            slot.LatencyMs = latencyMs;
            slot.Code = code;
            ring.Next = (ring.Next + 1) % RingSize;
            if (ring.Count < RingSize)
            {
                ring.Count++;
            }
        }
    }

    public void Forget(string key)
    {
        lock (_lock)
        {
            _rings.Remove(key);
        }
    }

    /// <summary>Appends the device's ring to the file unless one was written for it within <see cref="MinSnapshotGap"/>. Returns whether it wrote.</summary>
    public bool Snapshot(string key, string reason)
    {
        string text;
        lock (_lock)
        {
            if (!_rings.TryGetValue(key, out var ring) || ring.Count == 0)
            {
                return false;
            }
            var now = _nowMs();
            if (ring.LastSnapshotMs != long.MinValue && now - ring.LastSnapshotMs < MinSnapshotGap.TotalMilliseconds)
            {
                return false;
            }
            ring.LastSnapshotMs = now;
            text = Format(ring, key, reason, now);
        }
        try
        {
            Append(text);
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

    /// <summary>Model name plus the first 8 hex of the id's SHA-256, so two units of one model stay distinguishable without exposing a serial.</summary>
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
                .Append(e.Kind).Append(" n=").Append(e.Count).Append(" lat=").Append(e.LatencyMs).Append("ms code=").Append(e.Code).Append('\n');
        }
        return sb.ToString();
    }

    private void Append(string text)
    {
        lock (_fileLock)
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
}
