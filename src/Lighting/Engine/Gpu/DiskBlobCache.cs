using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Size-capped directory of opaque blobs addressed by a lowercase hex name: the
/// backing store for ANGLE's compiled-program cache and for rendered
/// thumbnails.
///
/// Trust: what is read back is fed to a GPU driver or served as an image, so the
/// directory must not be writable by other accounts. That is decided once by
/// <see cref="BlobCachePolicy"/>; when it says no, the cache is off (every read
/// misses, every write is dropped). Only hex-named files are indexed.
///
/// Initialization (policy, scan) runs on a background thread; until it
/// completes every read misses and every write is dropped. Reads use an
/// in-memory index, so a miss costs no file access. Writes are queued, capped
/// by bytes, and written by one background thread, which also evicts the least
/// recently used files over the cap. Every failure degrades to a miss.
/// </summary>
internal sealed class DiskBlobCache
{
    public const long DefaultMaxQueuedBytes = 16L * 1024 * 1024;

    private readonly string _dir;
    private readonly long _capBytes;
    private readonly long _maxQueuedBytes;
    private readonly Func<bool> _trusted;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Length, long LastUsed)> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _queued = new(StringComparer.Ordinal);
    private readonly Queue<string> _writeOrder = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _enabled;
    private long _clock;
    private long _total;
    private long _queuedBytes;
    private bool _writerStarted;
    private int _writing;

    /// <param name="trusted">Called once on the init thread; false disables the cache.</param>
    public DiskBlobCache(string dir, long capBytes, Func<bool> trusted, long maxQueuedBytes = DefaultMaxQueuedBytes)
    {
        _dir = dir;
        _capBytes = capBytes;
        _trusted = trusted;
        _maxQueuedBytes = maxQueuedBytes;
        new Thread(Initialize) { IsBackground = true, Name = "nexus-blob-init" }.Start();
    }

    public string Directory => _dir;

    /// <summary>True once initialization finished and the cache is usable.</summary>
    public bool Enabled => _ready.IsSet && _enabled;

    /// <summary>Block until initialization finished. For tests.</summary>
    public bool WaitReady(TimeSpan timeout) => _ready.Wait(timeout) && _enabled;

    /// <summary>True for the only names the cache stores: lowercase hex, 16 to 128 characters. Anything else in the directory is ignored, and a caller-supplied name outside this form can never reach the file system.</summary>
    public static bool IsCacheName(string name)
    {
        if (name.Length is < 16 or > 128)
        {
            return false;
        }
        foreach (var c in name)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Lowercase hex of the bytes: a file-name-safe address for a binary key.</summary>
    public static string HexName(ReadOnlySpan<byte> key) => Convert.ToHexString(key).ToLowerInvariant();

    /// <summary>Address for a thumbnail: hash of everything that decides the image, including the build, so a render change in any build never serves an older image.</summary>
    public static string ThumbnailName(string effect, int slot, bool frozen, string tag, string version)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{version}|{effect}|{slot}|{(frozen ? 1 : 0)}|{tag}");
        return HexName(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public byte[]? TryGet(string name)
    {
        if (!Enabled || !IsCacheName(name))
        {
            return null;
        }
        lock (_gate)
        {
            if (_queued.TryGetValue(name, out var pending))
            {
                return pending;
            }
            if (!_index.TryGetValue(name, out var entry))
            {
                return null;
            }
            _index[name] = (entry.Length, ++_clock);
        }
        var path = Path.Combine(_dir, name);
        try
        {
            return File.ReadAllBytes(path);
        }
        catch
        {
            Forget(name, path);
            return null;
        }
    }

    /// <summary>Queue a write; returns at once. Dropped when the cache is not ready, the value is empty or over the cap, or the pending bytes are at their limit.</summary>
    public void Put(string name, ReadOnlySpan<byte> value)
    {
        if (!Enabled || !IsCacheName(name) || value.Length == 0 || value.Length > _capBytes)
        {
            return;
        }
        var copy = value.ToArray();
        lock (_gate)
        {
            var replaced = _queued.TryGetValue(name, out var old) ? old.Length : 0;
            if (_queuedBytes - replaced + copy.Length > _maxQueuedBytes)
            {
                return;
            }
            _queuedBytes += copy.Length - replaced;
            _queued[name] = copy;
            if (replaced == 0)
            {
                _writeOrder.Enqueue(name);
            }
            if (!_writerStarted)
            {
                _writerStarted = true;
                new Thread(WriterLoop) { IsBackground = true, Name = "nexus-blob-cache" }.Start();
            }
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Block until every queued write has reached disk. For tests and orderly shutdown.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            while (_queued.Count > 0 || _writing > 0)
            {
                Monitor.Wait(_gate, 50);
            }
        }
    }

    // Test seam: runs on the writer thread just before the named entry is written.
    internal Action<string>? BeforeWrite { get; set; }

    private void Forget(string name, string path)
    {
        try
        { File.Delete(path); }
        catch { }
        lock (_gate)
        {
            if (_index.Remove(name, out var e))
            {
                _total -= e.Length;
            }
        }
    }

    private void WriterLoop()
    {
        while (true)
        {
            string name;
            byte[] value;
            lock (_gate)
            {
                while (_writeOrder.Count == 0)
                {
                    Monitor.Wait(_gate);
                }
                name = _writeOrder.Dequeue();
                value = _queued[name];
                _writing++;
            }
            var ok = false;
            long length = 0;
            try
            {
                BeforeWrite?.Invoke(name);
                length = value.Length;
                var path = Path.Combine(_dir, name);
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, value);
                File.Move(tmp, path, overwrite: true);
                ok = true;
            }
            catch
            {
            }
            List<string>? victims;
            lock (_gate)
            {
                // A Put that replaced this entry while it was being written is still queued: write it again.
                if (ReferenceEquals(_queued[name], value))
                {
                    _queued.Remove(name);
                    _queuedBytes -= value.Length;
                }
                else
                {
                    _writeOrder.Enqueue(name);
                }
                if (ok)
                {
                    var old = _index.TryGetValue(name, out var e) ? e.Length : 0;
                    _index[name] = (length, ++_clock);
                    _total += length - old;
                }
                victims = PickVictimsLocked();
                _writing--;
                Monitor.PulseAll(_gate);
            }
            DeleteVictims(victims);
        }
    }

    // Caller holds the gate. Removes least recently used entries from the index until under the cap; the files are deleted outside the gate.
    private List<string>? PickVictimsLocked()
    {
        if (_total <= _capBytes)
        {
            return null;
        }
        var order = new List<KeyValuePair<string, (long Length, long LastUsed)>>(_index);
        order.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
        var victims = new List<string>();
        foreach (var kv in order)
        {
            if (_total <= _capBytes)
            {
                break;
            }
            _index.Remove(kv.Key);
            _total -= kv.Value.Length;
            victims.Add(kv.Key);
        }
        return victims;
    }

    private void DeleteVictims(List<string>? victims)
    {
        if (victims is null)
        {
            return;
        }
        foreach (var v in victims)
        {
            try
            { File.Delete(Path.Combine(_dir, v)); }
            catch { }
        }
    }

    private void Initialize()
    {
        try
        {
            if (!_trusted())
            {
                return;
            }
            System.IO.Directory.CreateDirectory(_dir);
            var info = new DirectoryInfo(_dir);
            if (IsReparse(info) || (info.Parent is { } parent && IsReparse(parent)))
            {
                return;
            }
            var files = new List<FileInfo>(info.EnumerateFiles());
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            List<string>? victims;
            lock (_gate)
            {
                foreach (var f in files)
                {
                    if (f.Name.EndsWith(".tmp", StringComparison.Ordinal))
                    {
                        try
                        { f.Delete(); }
                        catch { }
                        continue;
                    }
                    if (!IsCacheName(f.Name) || (f.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    _index[f.Name] = (f.Length, ++_clock);
                    _total += f.Length;
                }
                victims = PickVictimsLocked();
            }
            DeleteVictims(victims);
            _enabled = true;
        }
        catch
        {
            _enabled = false;
        }
        finally
        {
            _ready.Set();
        }
    }

    private static bool IsReparse(DirectoryInfo d) => d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0;
}
