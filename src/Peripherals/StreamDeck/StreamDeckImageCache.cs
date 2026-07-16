using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Content-addressed disk cache for uploaded key-image bytes:
/// <c>%ProgramData%\Nexus\streamdeck\&lt;serial&gt;\&lt;contentHash&gt;.bin</c>.
/// Lets a reconnect or service restart re-push a deck's keys without the web
/// editor re-uploading anything. Also keeps a bounded in-memory copy per
/// serial so a repeat repaint of a page/folder view (StreamDeckConnectionWorker.
/// PushCurrentView, run synchronously on a key's own input-reader thread for
/// every configured slot in view) never re-hits disk for an already-seen
/// hash - the file system access was otherwise unconditional on every call.
/// </summary>
public sealed class StreamDeckImageCache
{
    /// <summary>Covers a full XL (32 keys) toggle set (two states each) across a couple of pages without unbounded growth.</summary>
    private const int MemoryCacheCapacityPerSerial = 128;

    private readonly string _root;
    private readonly object _memoryLock = new();
    private readonly Dictionary<string, Dictionary<string, byte[]>> _memoryBySerial = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedList<string>> _memoryLruBySerial = new(StringComparer.Ordinal);

    public StreamDeckImageCache() : this(DefaultRoot())
    {
    }

    /// <summary>Test seam: points the cache at a tmp directory instead of real ProgramData.</summary>
    public StreamDeckImageCache(string root)
    {
        _root = root;
    }

    private static string DefaultRoot() => Nexus.Service.Media.MediaLibrary.DeviceStoreDir("streamdeck");

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Whitelists a device serial before it becomes a directory component,
    /// matching every observed shape (real Elgato serials, the sd-&lt;hex&gt;
    /// HID-path fallback, the sim-0001 simulator id) while rejecting path
    /// traversal segments like ".." or "/".
    /// </summary>
    public static bool IsValidSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Length > 64)
        {
            return false;
        }
        foreach (var ch in serial)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Writes the bytes if not already cached (content-addressed, so a re-upload of the same image is a no-op write). No-ops on an invalid serial.</summary>
    public void Store(string serial, string hash, byte[] bytes)
    {
        if (!IsValidSerial(serial))
        {
            return;
        }
        var path = PathFor(serial, hash);
        if (File.Exists(path))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        CacheInMemory(serial, hash, bytes);
    }

    /// <summary>Returns the cached bytes, or null when never stored, the serial is invalid, or the cache was wiped. Serves from the in-memory copy when present; a disk hit backfills it.</summary>
    public byte[]? Load(string serial, string hash)
    {
        if (!IsValidSerial(serial))
        {
            return null;
        }
        lock (_memoryLock)
        {
            if (_memoryBySerial.TryGetValue(serial, out var cache) && cache.TryGetValue(hash, out var cached))
            {
                TouchLruLocked(serial, hash);
                return cached;
            }
        }
        try
        {
            var path = PathFor(serial, hash);
            if (!File.Exists(path))
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            CacheInMemory(serial, hash, bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes one cached image. Used to evict a hash no longer referenced by any slot after a replace.</summary>
    public void Evict(string serial, string hash)
    {
        if (!IsValidSerial(serial))
        {
            return;
        }
        try
        {
            File.Delete(PathFor(serial, hash));
        }
        catch
        {
            /* best effort */
        }
        lock (_memoryLock)
        {
            _memoryBySerial.TryGetValue(serial, out var cache);
            cache?.Remove(hash);
            _memoryLruBySerial.TryGetValue(serial, out var lru);
            lru?.Remove(hash);
        }
    }

    private void CacheInMemory(string serial, string hash, byte[] bytes)
    {
        lock (_memoryLock)
        {
            if (!_memoryBySerial.TryGetValue(serial, out var cache))
            {
                cache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                _memoryBySerial[serial] = cache;
            }
            if (!_memoryLruBySerial.TryGetValue(serial, out var lru))
            {
                lru = new LinkedList<string>();
                _memoryLruBySerial[serial] = lru;
            }
            cache[hash] = bytes;
            lru.Remove(hash);
            lru.AddLast(hash);
            while (cache.Count > MemoryCacheCapacityPerSerial)
            {
                var oldest = lru.First!.Value;
                lru.RemoveFirst();
                cache.Remove(oldest);
            }
        }
    }

    // Caller holds _memoryLock.
    private void TouchLruLocked(string serial, string hash)
    {
        if (_memoryLruBySerial.TryGetValue(serial, out var lru))
        {
            lru.Remove(hash);
            lru.AddLast(hash);
        }
    }

    private string PathFor(string serial, string hash) => Path.Combine(_root, serial, hash + ".bin");
}
