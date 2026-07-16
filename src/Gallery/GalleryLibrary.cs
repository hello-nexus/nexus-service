using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Media;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Gallery;

/// <summary>
/// Per-system gallery source registry + item enumeration. Sources are
/// referenced files/folders on local disk plus uploaded images; every panel
/// surface of this PC draws from the same set. sources.json (plus the upload
/// files themselves) is the only persisted state - folders are rescanned on
/// each enumeration so external file changes show up without a watcher.
/// </summary>
public sealed class GalleryLibrary
{
    private const string SourcesFileName = "sources.json";
    public const int MaxItemsPerFolder = 500;

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif" };

    // Folder sources scan recursively; depth-bounded so a junction/symlink
    // cycle can't spin, inaccessible subtrees are skipped silently.
    private static readonly EnumerationOptions FolderScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 8,
    };

    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly object _lock = new();
    private List<GallerySource>? _sources;
    private Dictionary<string, string> _itemPaths = new();
    // Bumped on every source mutation. EnumerateItems snapshots it and only
    // swaps its map in if no mutation happened mid-scan, so a slow scan can't
    // resurrect items from a source removed while it ran.
    private long _generation;
    private long _lastEnumerationTicks;

    public GalleryLibrary()
        : this(MediaLibrary.MediaStoreDir("gallery"))
    {
    }

    public GalleryLibrary(string rootDir)
    {
        RootDir = rootDir;
        Directory.CreateDirectory(rootDir);
    }

    public string RootDir { get; }

    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(ImageExtensions, ext) >= 0;
    }

    /// <summary>
    /// Stable item id: first 16 hex chars of SHA-256 of the canonical path.
    /// Survives restarts and rescans so panel clients can cache by id. Windows
    /// paths hash case-folded so the same file reached via differently-cased
    /// sources collapses to one id.
    /// </summary>
    public static string ItemIdForPath(string canonicalPath)
    {
        var normalized = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? canonicalPath.ToLowerInvariant()
            : canonicalPath;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    public List<GallerySource> ListSources()
    {
        lock (_lock)
        {
            // Deep-ish copy: Excluded is the one field mutated in place on
            // cached sources (ExcludeItem/RestoreExclusions under this lock);
            // callers serialize outside it, so they must not alias the list.
            return LoadSources().Select(CloneSource).ToList();
        }
    }

    private static GallerySource CloneSource(GallerySource s) => new()
    {
        Id = s.Id,
        Kind = s.Kind,
        Path = s.Path,
        Name = s.Name,
        AddedAtUnixMs = s.AddedAtUnixMs,
        Excluded = new List<string>(s.Excluded),
    };

    public GallerySourceMutationResponse AddReference(string path, string kind)
    {
        if (kind != GallerySourceKinds.File && kind != GallerySourceKinds.Folder && kind != GallerySourceKinds.Auto)
            return Fail("kind must be 'file', 'folder' or 'auto'");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return Fail("path must be absolute");

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return Fail("invalid path");
        }

        if (kind == GallerySourceKinds.Auto)
        {
            // Drag-n-drop sends bare paths; the service decides what they are.
            kind = Directory.Exists(full) ? GallerySourceKinds.Folder : GallerySourceKinds.File;
        }

        if (kind == GallerySourceKinds.File)
        {
            if (!File.Exists(full)) return Fail("file not found");
            if (!IsImageFile(full)) return Fail("unsupported image format");
        }
        else if (!Directory.Exists(full))
        {
            return Fail("folder not found");
        }

        lock (_lock)
        {
            var sources = LoadSources();
            if (sources.Any(s => s.Kind == kind && string.Equals(s.Path, full, PathComparison)))
                return Fail("source already added", GalleryErrorCodes.Duplicate);

            var source = NewSource(sources, kind, full, Path.GetFileName(full));
            sources.Add(source);
            SaveSources(sources);
            InvalidateItemsLocked();
            return new GallerySourceMutationResponse { Source = source };
        }
    }

    /// <summary>
    /// Remove a source. Sources are references - nothing is ever deleted
    /// from disk.
    /// </summary>
    public bool RemoveSource(string id)
    {
        lock (_lock)
        {
            var sources = LoadSources();
            var source = sources.FirstOrDefault(s => s.Id == id);
            if (source is null)
                return false;

            sources.Remove(source);
            SaveSources(sources);
            InvalidateItemsLocked();
            return true;
        }
    }

    /// <summary>
    /// Hide one item of a folder source without touching the file: its id
    /// goes on the source's exclusion list (restorable in one click).
    /// </summary>
    public bool ExcludeItem(string sourceId, string itemId)
    {
        if (!MediaLibrary.IsValidId(itemId))
            return false;

        lock (_lock)
        {
            var sources = LoadSources();
            var source = sources.FirstOrDefault(s => s.Id == sourceId);
            // Folder sources only - a file source's single item is removed by
            // deleting the source; an exclusion on it would never be consulted
            // and would render a phantom badge.
            if (source is null || source.Kind != GallerySourceKinds.Folder)
                return false;

            if (!source.Excluded.Contains(itemId))
            {
                source.Excluded.Add(itemId);
                SaveSources(sources);
                InvalidateItemsLocked();
            }

            return true;
        }
    }

    /// <summary>Clear a source's exclusion list - every hidden item returns.</summary>
    public bool RestoreExclusions(string sourceId)
    {
        lock (_lock)
        {
            var sources = LoadSources();
            var source = sources.FirstOrDefault(s => s.Id == sourceId);
            if (source is null)
                return false;

            if (source.Excluded.Count > 0)
            {
                source.Excluded.Clear();
                SaveSources(sources);
                InvalidateItemsLocked();
            }

            return true;
        }
    }

    /// <summary>
    /// Flatten all sources to the ordered item list (source order, then file
    /// name). Duplicate paths across sources collapse to the first occurrence.
    /// Refreshes the id → path map used by <see cref="ResolveItemPath"/>.
    /// </summary>
    public List<GalleryItem> EnumerateItems()
    {
        List<GallerySource> sources;
        long generation;
        lock (_lock)
        {
            // Snapshot WITH copied Excluded lists: the scan below runs outside
            // the lock for seconds while ExcludeItem/RestoreExclusions mutate
            // the cached sources' lists under it.
            sources = LoadSources().Select(CloneSource).ToList();
            generation = _generation;
        }

        var items = new List<GalleryItem>();
        var paths = new Dictionary<string, string>();

        foreach (var source in sources)
        {
            if (source.Kind == GallerySourceKinds.Folder)
            {
                // Recursive: users point at e.g. a Pictures root whose images
                // live in subfolders. The cap applies before the sort, so an
                // over-cap tree yields an arbitrary-but-stable subset.
                List<string> files;
                try
                {
                    files = Directory.EnumerateFiles(source.Path, "*", FolderScanOptions)
                        .Where(IsImageFile)
                        .Take(MaxItemsPerFolder)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    AddItem(file, source.Id, name: null, source.Excluded);
                }
            }
            else if (File.Exists(source.Path) && IsImageFile(source.Path))
            {
                AddItem(source.Path, source.Id, source.Name, excluded: null);
            }
        }

        lock (_lock)
        {
            // A mutation mid-scan means this map may contain items from a
            // removed source - drop it; the next enumeration rebuilds fresh.
            // The throttle timestamp only advances on a real swap, else a
            // discarded scan would arm it over an empty map and panel reads
            // would false-negative for the throttle window.
            if (generation == _generation)
            {
                _itemPaths = paths;
                _lastEnumerationTicks = Environment.TickCount64;
            }
        }

        return items;

        void AddItem(string file, string sourceId, string? name, List<string>? excluded)
        {
            var full = Path.GetFullPath(file);
            var id = ItemIdForPath(full);
            if (excluded is not null && excluded.Contains(id))
            {
                return;
            }

            if (paths.TryAdd(id, full))
            {
                items.Add(new GalleryItem
                {
                    Id = id,
                    Name = string.IsNullOrEmpty(name) ? Path.GetFileName(full) : name,
                    SourceId = sourceId,
                });
            }
        }
    }

    /// <summary>
    /// Resolve an item id to its absolute path. Only ids derived from the
    /// registered source set resolve - client-supplied paths never enter.
    /// </summary>
    public string? ResolveItemPath(string id)
    {
        lock (_lock)
        {
            if (_itemPaths.TryGetValue(id, out var cached))
                return cached;

            // Unknown-id requests are panel-reachable; without this throttle a
            // client spamming random ids forces a full disk rescan per request.
            if (Environment.TickCount64 - _lastEnumerationTicks < 2000)
                return null;
        }

        // Cold start or a freshly added file: rebuild the map once.
        EnumerateItems();
        lock (_lock)
        {
            return _itemPaths.TryGetValue(id, out var path) ? path : null;
        }
    }

    private GallerySource NewSource(List<GallerySource> existing, string kind, string path, string? name)
    {
        var baseName = string.IsNullOrEmpty(name) ? "source" : name;
        var id = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");
        // Same-millisecond adds (multi-select) can collide; suffix until unique.
        var unique = id;
        for (var n = 2; existing.Any(s => s.Id == unique); n++)
        {
            unique = MediaImporter.SanitizeId($"{id}-{n}");
        }

        return new GallerySource
        {
            Id = unique,
            Kind = kind,
            Path = path,
            Name = baseName,
            AddedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    // Call under _lock after any source mutation: revokes every resolved item
    // id immediately (panel file/thumbnail reads must not outlive the source)
    // and marks in-flight enumerations stale.
    private void InvalidateItemsLocked()
    {
        _generation++;
        _itemPaths = new Dictionary<string, string>();
        _lastEnumerationTicks = 0;
    }

    private List<GallerySource> LoadSources()
    {
        if (_sources is not null)
            return _sources;

        var file = Path.Combine(RootDir, SourcesFileName);
        try
        {
            var parsed = JsonSerializer.Deserialize(File.ReadAllText(file), AppJsonContext.Default.GallerySourcesFile);
            _sources = parsed?.Sources
                .Where(s => MediaLibrary.IsValidId(s.Id) && !string.IsNullOrEmpty(s.Path) && !string.IsNullOrEmpty(s.Kind))
                .ToList() ?? new List<GallerySource>();
            // Pre-exclusions builds stored uploaded copies as a distinct kind;
            // they're plain file references now (upload support is gone).
            foreach (var s in _sources)
            {
                if (s.Kind == GallerySourceKinds.Upload)
                {
                    s.Kind = GallerySourceKinds.File;
                }
            }
        }
        catch
        {
            // Missing or corrupt file: start empty rather than failing boot.
            _sources = new List<GallerySource>();
        }

        return _sources;
    }

    private void SaveSources(List<GallerySource> sources)
    {
        _sources = sources;
        Directory.CreateDirectory(RootDir);
        var json = JsonSerializer.Serialize(new GallerySourcesFile { Sources = sources }, AppJsonContext.Default.GallerySourcesFile);
        AtomicJsonFile.Write(Path.Combine(RootDir, SourcesFileName), json);
    }

    private static GallerySourceMutationResponse Fail(string msg, string code = "") =>
        new() { Error = true, Msg = msg, Code = code };
}
