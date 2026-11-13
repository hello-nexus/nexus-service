using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Common;
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
    // One cap across every source (folders and single files): the first this
    // many items in sort order are kept. Excluded files and duplicates of
    // earlier sources do not count.
    public const int MaxItems = 5000;
    // Bounds the walk-then-sort: past this many eligible files the scan
    // stops and the sort covers only what it collected.
    public const int MaxScanItems = MaxItems * 10;
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
    private GallerySort _sort = new();
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

    /// <summary>Tests lower it to exercise the ceiling without writing a full ceiling's worth of files.</summary>
    internal int ScanCeiling { get; init; } = MaxScanItems;

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

    private static bool IsMediaName(ReadOnlySpan<char> name)
    {
        var ext = Path.GetExtension(name);
        foreach (var known in ImageExtensions)
        {
            if (ext.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var known in VideoExtensions)
        {
            if (ext.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

    public GallerySort GetSort()
    {
        lock (_lock)
        {
            LoadSources();
            return CloneSort(_sort);
        }
    }

    /// <summary>False when either field is not a known value; nothing is stored then.</summary>
    public bool SetSort(GallerySort sort)
    {
        if (!IsValidSort(sort))
            return false;

        lock (_lock)
        {
            var sources = LoadSources();
            _sort = CloneSort(sort);
            SaveSources(sources);
            return true;
        }
    }

    private static bool IsValidSort(GallerySort? sort) =>
        sort is not null
        && sort.By is GallerySortKeys.Name or GallerySortKeys.Modified or GallerySortKeys.Added
        && sort.Direction is GallerySortDirections.Asc or GallerySortDirections.Desc;

    private static GallerySort CloneSort(GallerySort s) => new() { By = s.By, Direction = s.Direction };

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
    /// Flatten all sources to the item list in the stored <see cref="GallerySort"/>
    /// order. Duplicate paths across sources collapse to the first occurrence.
    /// Refreshes the id → path map used by <see cref="ResolveItemPath"/>.
    /// </summary>
    public List<GalleryItem> EnumerateItems() => Enumerate().Items;

    /// <summary>
    /// <see cref="EnumerateItems"/> plus whether the global cap dropped items.
    /// Every eligible file of every source, up to <see cref="MaxScanItems"/>, is
    /// walked and sorted first, then the first <see cref="MaxItems"/> in sort
    /// order are kept, so the cap drops the tail of the sorted list.
    /// </summary>
    public GalleryEnumeration Enumerate()
    {
        List<GallerySource> sources;
        GallerySort sort;
        long generation;
        lock (_lock)
        {
            // Snapshot WITH copied Excluded lists: the scan below runs outside
            // the lock for seconds while ExcludeItem/RestoreExclusions mutate
            // the cached sources' lists under it.
            sources = LoadSources().Select(CloneSource).ToList();
            sort = CloneSort(_sort);
            generation = _generation;
        }

        var wantModified = sort.By == GallerySortKeys.Modified;
        var candidates = new List<Candidate>();
        // First source to reach a file owns it; excluded files never enter.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ceilingHit = false;

        for (var index = 0; index < sources.Count && !ceilingHit; index++)
        {
            var source = sources[index];
            if (source.Kind == GallerySourceKinds.Folder)
            {
                // Recursive: users point at e.g. a Pictures root whose images
                // live in subfolders. A folder whose walk fails contributes
                // nothing.
                var excluded = new HashSet<string>(source.Excluded, StringComparer.Ordinal);
                var found = new List<Candidate>();
                var foundIds = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    // Same walk as Directory.EnumerateFiles; the entry carries the
                    // write time from the directory read (free on Windows).
                    var walk = new FileSystemEnumerable<(string Path, long Modified)>(
                        source.Path,
                        (ref FileSystemEntry e) => (e.ToSpecifiedFullPath(), wantModified ? EntryModifiedTicks(ref e) : 0),
                        FolderScanOptions)
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && IsMediaName(e.FileName),
                    };
                    foreach (var (file, fileModified) in walk)
                    {
                        var full = Path.GetFullPath(file);
                        var id = ItemIdForPath(full);
                        if (excluded.Contains(id) || seen.Contains(id) || foundIds.Contains(id))
                        {
                            continue;
                        }

                        if (candidates.Count + found.Count >= ScanCeiling)
                        {
                            ceilingHit = true;
                            break;
                        }

                        foundIds.Add(id);
                        found.Add(new Candidate(full, id, source.Id, Path.GetFileName(full), fileModified, index));
                    }
                }
                catch
                {
                    continue;
                }

                seen.UnionWith(foundIds);
                candidates.AddRange(found);
            }
            else if (File.Exists(source.Path) && IsMediaFile(source.Path))
            {
                var full = Path.GetFullPath(source.Path);
                var id = ItemIdForPath(full);
                if (seen.Contains(id))
                {
                    continue;
                }

                if (candidates.Count >= ScanCeiling)
                {
                    ceilingHit = true;
                    break;
                }

                seen.Add(id);
                var name = string.IsNullOrEmpty(source.Name) ? Path.GetFileName(full) : source.Name;
                candidates.Add(new Candidate(full, id, source.Id, name, wantModified ? FileModifiedTicks(full) : 0, index));
            }
        }

        candidates.Sort(Comparer(sources, sort));
        var truncated = ceilingHit || candidates.Count > MaxItems;
        var kept = candidates.Count > MaxItems ? candidates.GetRange(0, MaxItems) : candidates;

        var items = new List<GalleryItem>(kept.Count);
        var paths = new Dictionary<string, string>(kept.Count);
        foreach (var c in kept)
        {
            paths[c.Id] = c.Full;
            items.Add(new GalleryItem
            {
                Id = c.Id,
                Name = c.Name,
                SourceId = c.SourceId,
                Kind = IsVideoFile(c.Full) ? GalleryItemKinds.Video : GalleryItemKinds.Image,
            });
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

        return new GalleryEnumeration(items, truncated, sort);
    }

    /// <param name="Modified">Write-time ticks; 0 when unread or unreadable.</param>
    /// <param name="SourceIndex">Position of the owning source in persisted order.</param>
    private readonly record struct Candidate(string Full, string Id, string SourceId, string Name, long Modified, int SourceIndex);

    // A missing file reads as the FILETIME epoch rather than throwing.
    private static readonly long FileTimeEpochTicks = DateTime.FromFileTimeUtc(0).Ticks;

    private static long KnownTicks(long ticks) => ticks > FileTimeEpochTicks ? ticks : 0;

    // A dropped share or removed drive must not fail the whole enumeration.
    private static long EntryModifiedTicks(ref FileSystemEntry e)
    {
        try
        {
            return KnownTicks(e.LastWriteTimeUtc.UtcTicks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static long FileModifiedTicks(string path)
    {
        try
        {
            return KnownTicks(File.GetLastWriteTimeUtc(path).Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Name order is the natural order of the full path, so subfolders stay
    /// grouped; date modes break ties with it ascending. An unknown time (0)
    /// sorts after every known one in both directions: a source with no add
    /// time in persisted source order, a file with no readable write time by path.
    /// </summary>
    private static Comparison<Candidate> Comparer(List<GallerySource> sources, GallerySort sort)
    {
        var sign = sort.Direction == GallerySortDirections.Desc ? -1 : 1;
        var natural = NaturalStringComparer.Instance;

        switch (sort.By)
        {
            case GallerySortKeys.Modified:
                return (x, y) =>
                {
                    if ((x.Modified == 0) != (y.Modified == 0))
                        return x.Modified == 0 ? 1 : -1;
                    var c = x.Modified.CompareTo(y.Modified) * sign;
                    return c != 0 ? c : natural.Compare(x.Full, y.Full);
                };
            case GallerySortKeys.Added:
                var added = sources.Select(s => s.AddedAtUnixMs).ToArray();
                return (x, y) =>
                {
                    var ax = added[x.SourceIndex];
                    var ay = added[y.SourceIndex];
                    if ((ax <= 0) != (ay <= 0))
                        return ax <= 0 ? 1 : -1;
                    var c = ax <= 0
                        ? x.SourceIndex.CompareTo(y.SourceIndex)
                        : ax.CompareTo(ay) * sign;
                    return c != 0 ? c : natural.Compare(x.Full, y.Full);
                };
            default:
                return (x, y) => natural.Compare(x.Full, y.Full) * sign;
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
            _sort = IsValidSort(parsed?.Sort) ? CloneSort(parsed!.Sort!) : new GallerySort();
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
            _sort = new GallerySort();
        }

        return _sources;
    }

    private void SaveSources(List<GallerySource> sources)
    {
        _sources = sources;
        Directory.CreateDirectory(RootDir);
        var json = JsonSerializer.Serialize(new GallerySourcesFile { Sources = sources, Playlists = _playlists, Sort = _sort }, AppJsonContext.Default.GallerySourcesFile);
        AtomicJsonFile.Write(Path.Combine(RootDir, SourcesFileName), json);
    }

    private static GallerySourceMutationResponse Fail(string msg, string code = "") =>
        new() { Error = true, Msg = msg, Code = code };

    private static GalleryPlaylistMutationResponse FailPlaylist(string msg, string code = "") =>
        new() { Error = true, Msg = msg, Code = code };
}

/// <summary>Result of <see cref="GalleryLibrary.Enumerate"/>.</summary>
public sealed record GalleryEnumeration(List<GalleryItem> Items, bool Truncated, GallerySort Sort);
