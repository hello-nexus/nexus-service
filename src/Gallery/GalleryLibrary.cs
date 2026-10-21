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
/// referenced image and video files/folders on local disk; every panel
/// surface of this PC draws from the same set. sources.json (sources plus the
/// playlists cut from them) is the only persisted state - folders are
/// rescanned on each enumeration so external file changes show up without a
/// watcher.
/// </summary>
public sealed class GalleryLibrary
{
    private const string SourcesFileName = "sources.json";
    // One cap across every source (folders and single files). The walk stops
    // at the first countable item past it, so a request visits at most one
    // more media file than the cap.
    public const int MaxItems = 5000;
    public const int MaxPlaylists = 50;
    public const int MaxPlaylistNameLength = 48;
    // Per id list on one playlist, so a runaway client cannot grow
    // sources.json without bound. Ids past it are dropped, not rejected.
    public const int MaxPlaylistEntries = 5000;

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif" };
    // Containers the panel WebViews (Chromium 83 on the Q-series, WebView2,
    // WKWebView) open natively; nothing is transcoded, so any other container
    // is refused up front. The codec inside is not checked - an HEVC clip in
    // one of these passes here and is skipped by the widget when it fails to
    // decode.
    private static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".webm", ".mov" };

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
    private List<GalleryPlaylist> _playlists = new();
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

    public static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(VideoExtensions, ext) >= 0;
    }

    public static bool IsMediaFile(string path) => IsImageFile(path) || IsVideoFile(path);

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
            if (!IsMediaFile(full)) return Fail("unsupported media format");
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
            // Picked item ids stay: they are inert while their files are
            // outside the library, and come back if the source is re-added.
            foreach (var playlist in _playlists)
            {
                playlist.SourceIds.Remove(id);
            }

            SaveSources(sources);
            InvalidateItemsLocked();
            return true;
        }
    }

    public List<GalleryPlaylist> ListPlaylists()
    {
        lock (_lock)
        {
            LoadSources();
            return _playlists.Select(ClonePlaylist).ToList();
        }
    }

    public GalleryPlaylistMutationResponse CreatePlaylist(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        lock (_lock)
        {
            var sources = LoadSources();
            if (_playlists.Count >= MaxPlaylists)
                return FailPlaylist("playlist limit reached", GalleryErrorCodes.Limit);
            var nameError = ValidatePlaylistName(trimmed, exceptId: null);
            if (nameError is not null)
                return nameError;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var id = $"pl-{now:x}";
            var unique = id;
            for (var n = 2; _playlists.Any(p => p.Id == unique); n++)
            {
                unique = $"{id}-{n}";
            }

            var playlist = new GalleryPlaylist { Id = unique, Name = trimmed, CreatedAtUnixMs = now };
            _playlists.Add(playlist);
            SaveSources(sources);
            return new GalleryPlaylistMutationResponse { Playlist = ClonePlaylist(playlist) };
        }
    }

    /// <summary>
    /// Replace any field the body carries. Source ids are kept only while the
    /// source exists; item ids are only format-checked, since they are
    /// resolved against the live library at read time anyway.
    /// </summary>
    public GalleryPlaylistMutationResponse UpdatePlaylist(string id, GalleryPlaylistBody body)
    {
        lock (_lock)
        {
            var sources = LoadSources();
            var playlist = _playlists.FirstOrDefault(p => p.Id == id);
            if (playlist is null)
                return FailPlaylist("playlist not found", GalleryErrorCodes.NotFound);

            if (body.Name is not null)
            {
                var trimmed = body.Name.Trim();
                var nameError = ValidatePlaylistName(trimmed, exceptId: id);
                if (nameError is not null)
                    return nameError;
                playlist.Name = trimmed;
            }

            if (body.SourceIds is not null)
            {
                var known = sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                playlist.SourceIds = CleanIds(body.SourceIds).Where(known.Contains).ToList();
            }

            if (body.ItemIds is not null)
                playlist.ItemIds = CleanIds(body.ItemIds);
            if (body.ExcludedIds is not null)
                playlist.ExcludedIds = CleanIds(body.ExcludedIds);

            SaveSources(sources);
            return new GalleryPlaylistMutationResponse { Playlist = ClonePlaylist(playlist) };
        }
    }

    public bool DeletePlaylist(string id)
    {
        lock (_lock)
        {
            var sources = LoadSources();
            if (_playlists.RemoveAll(p => p.Id == id) == 0)
                return false;

            SaveSources(sources);
            return true;
        }
    }

    private GalleryPlaylistMutationResponse? ValidatePlaylistName(string name, string? exceptId)
    {
        if (name.Length == 0 || name.Length > MaxPlaylistNameLength)
            return FailPlaylist("invalid playlist name", GalleryErrorCodes.InvalidName);
        // Widgets pick playlists by name in a dropdown, so two with one name
        // would be indistinguishable there.
        if (_playlists.Any(p => p.Id != exceptId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return FailPlaylist("playlist name already used", GalleryErrorCodes.Duplicate);
        return null;
    }

    private static List<string> CleanIds(IEnumerable<string> ids) =>
        ids.Where(MediaLibrary.IsValidId).Distinct(StringComparer.Ordinal).Take(MaxPlaylistEntries).ToList();

    private static GalleryPlaylist ClonePlaylist(GalleryPlaylist p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        CreatedAtUnixMs = p.CreatedAtUnixMs,
        SourceIds = new List<string>(p.SourceIds),
        ItemIds = new List<string>(p.ItemIds),
        ExcludedIds = new List<string>(p.ExcludedIds),
    };

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
    public List<GalleryItem> EnumerateItems() => Enumerate().Items;

    /// <summary>
    /// <see cref="EnumerateItems"/> plus whether the global cap dropped items.
    /// Sources are walked in persisted order and the walk stops once the cap is
    /// exceeded; only the files kept from a folder are sorted, so the subset
    /// kept from an over-cap folder is arbitrary but stable.
    /// </summary>
    public GalleryEnumeration Enumerate()
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
        var truncated = false;

        foreach (var source in sources)
        {
            if (truncated)
            {
                break;
            }

            if (source.Kind == GallerySourceKinds.Folder)
            {
                // Recursive: users point at e.g. a Pictures root whose images
                // live in subfolders. Excluded files and files an earlier
                // source already holds take no slot.
                var excluded = new HashSet<string>(source.Excluded, StringComparer.Ordinal);
                var picked = new List<(string Full, string Id)>();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(source.Path, "*", FolderScanOptions))
                    {
                        if (!IsMediaFile(file))
                        {
                            continue;
                        }

                        var full = Path.GetFullPath(file);
                        var id = ItemIdForPath(full);
                        if (excluded.Contains(id) || paths.ContainsKey(id))
                        {
                            continue;
                        }

                        if (items.Count + picked.Count >= MaxItems)
                        {
                            truncated = true;
                            break;
                        }

                        picked.Add((full, id));
                    }
                }
                catch
                {
                    continue;
                }

                picked.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Full, y.Full));
                foreach (var (full, id) in picked)
                {
                    AddItem(full, id, source.Id, name: null);
                }
            }
            else if (File.Exists(source.Path) && IsMediaFile(source.Path))
            {
                var full = Path.GetFullPath(source.Path);
                var id = ItemIdForPath(full);
                if (paths.ContainsKey(id))
                {
                    continue;
                }

                if (items.Count >= MaxItems)
                {
                    truncated = true;
                    continue;
                }

                AddItem(full, id, source.Id, source.Name);
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

        return new GalleryEnumeration(items, truncated);

        void AddItem(string full, string id, string sourceId, string? name)
        {
            if (paths.TryAdd(id, full))
            {
                items.Add(new GalleryItem
                {
                    Id = id,
                    Name = string.IsNullOrEmpty(name) ? Path.GetFileName(full) : name,
                    SourceId = sourceId,
                    Kind = IsVideoFile(full) ? GalleryItemKinds.Video : GalleryItemKinds.Image,
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
            _playlists = parsed?.Playlists?
                .Where(p => MediaLibrary.IsValidId(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p =>
                {
                    // Lists absent from the file deserialize as null.
                    p.SourceIds ??= new List<string>();
                    p.ItemIds ??= new List<string>();
                    p.ExcludedIds ??= new List<string>();
                    return p;
                })
                .ToList() ?? new List<GalleryPlaylist>();
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
            _playlists = new List<GalleryPlaylist>();
        }

        return _sources;
    }

    private void SaveSources(List<GallerySource> sources)
    {
        _sources = sources;
        Directory.CreateDirectory(RootDir);
        var json = JsonSerializer.Serialize(new GallerySourcesFile { Sources = sources, Playlists = _playlists }, AppJsonContext.Default.GallerySourcesFile);
        AtomicJsonFile.Write(Path.Combine(RootDir, SourcesFileName), json);
    }

    private static GallerySourceMutationResponse Fail(string msg, string code = "") =>
        new() { Error = true, Msg = msg, Code = code };

    private static GalleryPlaylistMutationResponse FailPlaylist(string msg, string code = "") =>
        new() { Error = true, Msg = msg, Code = code };
}

/// <summary>Result of <see cref="GalleryLibrary.Enumerate"/>.</summary>
public sealed record GalleryEnumeration(List<GalleryItem> Items, bool Truncated);
