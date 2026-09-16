using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Activity;

/// <summary>
/// Disk cache for extracted app icons: <c>&lt;root&gt;/&lt;hash&gt;.png</c> plus a
/// <c>&lt;hash&gt;.meta.json</c> sidecar recording the source file's path and
/// last-write time, so an app reinstall/update (which touches the shortcut's
/// mtime) invalidates the cached icon instead of serving it forever.
/// </summary>
public sealed class IconDiskCache
{
    private readonly string _root;

    public IconDiskCache() : this(DefaultRoot())
    {
    }

    /// <summary>Test seam: points the cache at a tmp directory instead of real ProgramData.</summary>
    public IconDiskCache(string root)
    {
        _root = root;
    }

    public string Root => _root;

    private static string DefaultRoot()
    {
        if (Persistence.NexusDataPaths.SystemDaemonRoot is { } daemonRoot)
            return Path.Combine(daemonRoot, "icon-cache");
        // Mirrors StreamDeckImageCache.DefaultRoot / JsonConfigStore.ResolveSettingsPath:
        // CommonApplicationData is %ProgramData% only on Windows; on macOS it maps
        // to the unwritable /usr/share, so the cache lives beside Application
        // Support instead. Only the Windows branch runs in production
        // (WindowsShortcutsProvider is the only caller); the others exist so this
        // class stays testable and consistent with every other per-OS cache root.
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Nexus", "icon-cache");
        }
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Nexus", "icon-cache");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Nexus", "icon-cache");
    }

    public static string KeyFor(string targetId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetId))).ToLowerInvariant();

    /// <summary>
    /// Returns the cached PNG bytes when present and the recorded source path
    /// and write time still match the caller's, else null (miss or stale).
    /// </summary>
    public byte[]? TryGet(string targetId, string sourcePath, DateTime sourceWriteTimeUtc)
    {
        var key = KeyFor(targetId);
        var metaPath = MetaPath(key);
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            var meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), AppJsonContext.Default.IconCacheMeta);
            if (meta is null ||
                !string.Equals(meta.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                meta.SourceWriteTimeUtcTicks != sourceWriteTimeUtc.Ticks)
            {
                return null;
            }

            var pngPath = PngPath(key);
            return File.Exists(pngPath) ? File.ReadAllBytes(pngPath) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the PNG bytes and the source path/write-time sidecar used for the
    /// next staleness check. Best-effort: two panel viewers loading the same
    /// cold icon at once can both extract and both call Store, so a write
    /// failure here (the loser of a concurrent temp-file rename) is swallowed
    /// rather than thrown - the caller already has the extracted bytes in hand
    /// and a failed cache write must never discard a successful extraction.
    /// </summary>
    public void Store(string targetId, string sourcePath, DateTime sourceWriteTimeUtc, byte[] pngBytes)
    {
        try
        {
            var key = KeyFor(targetId);
            Directory.CreateDirectory(_root);
            AtomicJsonFile.Write(PngPath(key), pngBytes);
            var meta = new IconCacheMeta
            {
                SourcePath = sourcePath,
                SourceWriteTimeUtcTicks = sourceWriteTimeUtc.Ticks,
            };
            AtomicJsonFile.Write(MetaPath(key), JsonSerializer.Serialize(meta, AppJsonContext.Default.IconCacheMeta));
        }
        catch
        {
            /* best effort */
        }
    }

    /// <summary>
    /// Startup sweep: deletes entries whose PNG was last written more than
    /// maxAge ago (NTFS last-access tracking is disabled by default, so
    /// last-write is the only reliable touch signal), then - if still over
    /// maxEntries - deletes the oldest remainder by that same timestamp. Also
    /// removes an orphaned meta file left behind by a partial write. Returns
    /// the number of entries removed.
    /// </summary>
    public int PruneStale(TimeSpan maxAge, int maxEntries)
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var removed = 0;
        var cutoff = DateTime.UtcNow - maxAge;
        var survivors = new List<(string Key, DateTime WriteUtc)>();
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pngPath in Directory.EnumerateFiles(_root, "*.png"))
        {
            var key = Path.GetFileNameWithoutExtension(pngPath);
            DateTime writeUtc;
            try
            {
                writeUtc = File.GetLastWriteTimeUtc(pngPath);
            }
            catch
            {
                continue;
            }

            if (writeUtc < cutoff)
            {
                DeleteEntry(key);
                removed++;
            }
            else
            {
                survivors.Add((key, writeUtc));
                liveKeys.Add(key);
            }
        }

        if (survivors.Count > maxEntries)
        {
            survivors.Sort((a, b) => a.WriteUtc.CompareTo(b.WriteUtc));
            var overflow = survivors.Count - maxEntries;
            for (var i = 0; i < overflow; i++)
            {
                DeleteEntry(survivors[i].Key);
                liveKeys.Remove(survivors[i].Key);
                removed++;
            }
        }

        foreach (var metaPath in Directory.EnumerateFiles(_root, "*.meta.json"))
        {
            var key = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(metaPath));
            if (!liveKeys.Contains(key))
            {
                try { File.Delete(metaPath); } catch { }
            }
        }

        return removed;
    }

    private void DeleteEntry(string key)
    {
        try { File.Delete(PngPath(key)); } catch { }
        try { File.Delete(MetaPath(key)); } catch { }
    }

    private string PngPath(string key) => Path.Combine(_root, key + ".png");
    private string MetaPath(string key) => Path.Combine(_root, key + ".meta.json");
}

public sealed class IconCacheMeta
{
    public string SourcePath { get; set; } = "";
    public long SourceWriteTimeUtcTicks { get; set; }
}
