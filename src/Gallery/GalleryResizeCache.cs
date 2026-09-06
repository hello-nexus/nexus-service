using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;

namespace Nexus.Service.Gallery;

/// <summary>
/// Panel-sized JPEG derivatives of gallery images, cached on disk.
///
/// Panels fetch photos over a slow transport (the Q60 rides an adb reverse
/// tunnel) and decode them in a Chromium 83 WebView, so a full-resolution
/// phone JPEG costs seconds per slideshow step. Each (image, width) pair is
/// encoded once by ffmpeg and reused. The user's own file is only ever read -
/// gallery sources are references into their folders and nothing here writes
/// outside <see cref="CacheDir"/>.
///
/// Three properties keep this off the service's critical path: concurrent
/// encodes are capped at <see cref="MaxConcurrentEncodes"/>, identical
/// requests collapse onto one process, and the directory is swept back under
/// <see cref="MaxCacheBytes"/> after each write. Every failure path - no
/// ffmpeg, a timeout, an unreadable source - returns null so the caller serves
/// the original; this can only ever be a speed-up.
/// </summary>
public sealed class GalleryResizeCache
{
    /// <summary>
    /// Allowlisted output widths, ascending. A request snaps UP to the first
    /// bucket at least as wide as the ask. This is what bounds the cache:
    /// honouring arbitrary pixel widths would mint a fresh derivative per
    /// panel size, per rotation, per DPR.
    /// </summary>
    private static readonly int[] BucketWidths = { 320, 480, 640, 960, 1280, 1920 };

    public static IReadOnlyList<int> Buckets => BucketWidths;

    /// <summary>
    /// Sources a JPEG derivative can stand in for losslessly. Deliberately
    /// excludes png/webp/avif/gif: all four carry alpha and/or animation that
    /// a flattened first-frame JPEG would silently destroy.
    /// </summary>
    private static readonly string[] DerivableExtensions = { ".jpg", ".jpeg", ".bmp" };

    private const long MaxCacheBytes = 256L * 1024 * 1024;
    private const int MaxConcurrentEncodes = 2;
    // Far below MediaImporter's import budget: this one sits on a live HTTP
    // request, and a still-image resize that has not finished by then is not
    // going to beat serving the original.
    private const int EncodeTimeoutSeconds = 20;
    // How long a request waits for an encode slot before giving up and letting
    // the caller serve the original.
    private const int GateWaitSeconds = 3;
    private const int MaxRememberedFailures = 512;
    private static readonly TimeSpan LruTouchInterval = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _encodeGate = new(MaxConcurrentEncodes, MaxConcurrentEncodes);
    // Lazy, not a bare Task: ConcurrentDictionary may invoke a GetOrAdd factory
    // more than once for the same key even though it stores only one result,
    // and two encodes of one image must never both be running.
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new();
    // Sources ffmpeg has already refused. Without this a file it cannot decode
    // re-spawns a process on every request forever, each one holding an encode
    // slot that a workable image could have used.
    private readonly ConcurrentDictionary<string, byte> _failed = new();
    private int _sweeping;

    public GalleryResizeCache(GalleryLibrary library)
        : this(Path.Combine(library.RootDir, "resized"))
    {
    }

    public GalleryResizeCache(string cacheDir)
    {
        CacheDir = cacheDir;
    }

    public string CacheDir { get; }

    /// <summary>Snaps an arbitrary pixel width onto <see cref="Buckets"/>.</summary>
    public static int SnapWidth(int requested)
    {
        foreach (var bucket in BucketWidths)
        {
            if (requested <= bucket)
            {
                return bucket;
            }
        }
        return BucketWidths[^1];
    }

    public static bool CanDerive(string path) =>
        Array.IndexOf(DerivableExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>
    /// Path to a cached JPEG of <paramref name="sourcePath"/> no wider than the
    /// bucket <paramref name="requestedWidth"/> snaps to, encoding it if
    /// needed. Null means "serve the original" - never an error to surface.
    /// The file name doubles as the caller's ETag.
    /// </summary>
    public async Task<string?> GetAsync(string itemId, string sourcePath, int requestedWidth, CancellationToken ct)
    {
        if (FfmpegResolver.Path is null || !CanDerive(sourcePath))
        {
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var width = SnapWidth(requestedWidth);
        // Size and mtime are in the key, so replacing the file on disk yields a
        // new key rather than a stale hit. Item ids hash the path alone and
        // cannot carry that.
        var key = CacheFileName(itemId, sourcePath, info.LastWriteTimeUtc.Ticks, info.Length, width);
        var cachePath = Path.Combine(CacheDir, key + ".jpg");
        if (IsUsable(cachePath))
        {
            TouchForLru(cachePath);
            return cachePath;
        }

        if (_failed.ContainsKey(key))
        {
            return null;
        }

        // Single-flight: a slideshow on several panels asking for the same photo
        // at the same bucket must run one ffmpeg, not one per panel.
        var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<string?>>(
            () => EncodeAsync(sourcePath, width, cachePath, k),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            // The shared encode runs on its own budget; awaiting it under the
            // caller's token lets THIS request give up (panel swapped the image,
            // tunnel reset) without cancelling the work every other panel is
            // waiting on.
            return await lazy.Value.WaitAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> EncodeAsync(string sourcePath, int width, string cachePath, string key)
    {
        try
        {
            // Bounded, so a cold gallery queueing behind the encode slots
            // degrades to serving originals instead of parking the request.
            if (!await _encodeGate.WaitAsync(TimeSpan.FromSeconds(GateWaitSeconds)))
            {
                return null;
            }

            // Unique per encode: a temp shared between two runs of the same key
            // could be moved onto the real name half-written, and a truncated
            // JPEG at a valid cache key is served for the life of the file.
            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                if (IsUsable(cachePath))
                {
                    return cachePath;
                }

                Directory.CreateDirectory(CacheDir);
                await MediaImporter.RunFfmpeg(EncodeTimeoutSeconds, CancellationToken.None, new[]
                {
                    "-y", "-i", sourcePath,
                    // The comma is escaped for ffmpeg's own filter parser, which
                    // would otherwise read it as a filter separator. min() is what
                    // stops a small source being upscaled into a file LARGER than
                    // the original.
                    "-vf", $"scale=min({width}\\,iw):-1",
                    // First frame only, and strip metadata: ffmpeg has already
                    // applied EXIF orientation to the pixels, so carrying the tag
                    // forward would make the browser rotate a second time.
                    "-frames:v", "1", "-map_metadata", "-1", "-q:v", "4",
                    // Muxer and codec are pinned rather than inferred: the output
                    // is written to a temp name, and ffmpeg reads the FINAL
                    // extension to pick a format - ".tmp" matches nothing and it
                    // fails with "Error initializing the muxer". -update marks it
                    // a single image rather than a numbered sequence.
                    "-f", "image2", "-update", "1", "-c:v", "mjpeg",
                    tempPath,
                });

                if (!IsUsable(tempPath))
                {
                    RememberFailure(key);
                    return null;
                }
                File.Move(tempPath, cachePath, overwrite: true);
                SweepIfOverBudget();
                return cachePath;
            }
            catch (Exception ex)
            {
                RememberFailure(key);
                Console.Error.WriteLine($"[gallery-resize] {Path.GetFileName(sourcePath)} @{width}: {ex.Message}");
                return null;
            }
            finally
            {
                TryDelete(tempPath);
                _encodeGate.Release();
            }
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private void RememberFailure(string key)
    {
        // Bounded: the set is a speed guard, not a record worth growing without
        // limit on a gallery full of unreadable files.
        if (_failed.Count >= MaxRememberedFailures)
        {
            _failed.Clear();
        }
        _failed[key] = 0;
    }

    /// <summary>
    /// Keeps the sweep's oldest-first order meaningful: it orders by write time,
    /// which a cache hit would otherwise never refresh, so the most-used
    /// derivative would be the first evicted. Rate-limited so a hot image is not
    /// rewriting its own timestamp on every request.
    /// </summary>
    private static void TouchForLru(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > LruTouchInterval)
            {
                File.SetLastWriteTimeUtc(path, now);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsUsable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Drops least-recently-used derivatives until the directory is back under
    /// budget, and clears temps orphaned by a kill mid-encode (they match no
    /// glob the serving path uses, so nothing else would ever remove them).
    /// Single-flighted so parallel encodes do not race each other's deletes.
    /// </summary>
    private void SweepIfOverBudget()
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
        {
            return;
        }
        try
        {
            var dir = new DirectoryInfo(CacheDir);
            foreach (var stale in dir.GetFiles("*.tmp"))
            {
                if (DateTime.UtcNow - stale.LastWriteTimeUtc > TimeSpan.FromMinutes(5))
                {
                    TryDelete(stale.FullName);
                }
            }

            var files = dir.GetFiles("*.jpg");
            var total = files.Sum(f => f.Length);
            if (total <= MaxCacheBytes)
            {
                return;
            }
            // Comfortably under the cap rather than exactly at it, so the next
            // encodes do not each trigger another sweep.
            var target = (long)(MaxCacheBytes * 0.8);
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= target)
                {
                    break;
                }
                var size = file.Length;
                if (TryDelete(file.FullName))
                {
                    total -= size;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// "&lt;itemId&gt;-&lt;digest&gt;". The digest covers size and mtime as well as
    /// width, so replacing a photo on disk yields a new name rather than a
    /// stale hit - item ids hash the path alone and cannot express that. The
    /// id prefix is what makes <see cref="PurgeExcept"/> able to find every
    /// derivative of an item the user just removed.
    /// </summary>
    private static string CacheFileName(string itemId, string sourcePath, long mtimeTicks, long length, int width)
    {
        var normalized = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? sourcePath.ToLowerInvariant()
            : sourcePath;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalized}|{mtimeTicks}|{length}|{width}"));
        return itemId + "-" + Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    /// <summary>
    /// Drops every derivative whose item is no longer in the gallery. Removing
    /// a source or hiding a folder image leaves the user's own file untouched
    /// by design, but the downsized copy Nexus made is ours to clean up - it
    /// must not outlive the item it was derived from.
    /// </summary>
    public void PurgeExcept(IReadOnlyCollection<string> liveItemIds)
    {
        FileInfo[] files;
        try
        {
            if (!Directory.Exists(CacheDir))
            {
                return;
            }
            files = new DirectoryInfo(CacheDir).GetFiles("*.jpg");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);
            var dash = name.IndexOf('-');
            if (dash <= 0)
            {
                continue;
            }
            if (!liveItemIds.Contains(name[..dash]))
            {
                TryDelete(file.FullName);
            }
        }
    }
}
