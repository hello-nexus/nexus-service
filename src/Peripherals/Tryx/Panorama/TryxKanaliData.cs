using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Reads Kanali's on-disk media library to supply covers and display names for
/// panel media Nexus did not upload itself (cloud themes pushed as download_NN, prior
/// Kanali uploads). Kanali is the Tryx OEM Electron app; its data dir is
/// <c>&lt;profile&gt;\AppData\Roaming\kanali</c>. Best-effort: no Kanali install -> empty
/// index, callers fall back to a placeholder. Matches the panel-reported filename first
/// exactly, then by timestamp stem (the panel and Kanali disagree on the suffix form),
/// and finally extracts a frame from the original upload still under <c>kanali\media</c>.</summary>
public static class TryxKanaliData
{
    /// <summary>A matched Kanali record: a human display name and, when present on disk,
    /// the source cover image path (PNG or JPG) to downscale into a thumbnail.</summary>
    public sealed record Entry(string DisplayName, string? CoverPath);

    private static readonly object Lock = new();
    private static Dictionary<string, Entry> _index = new(StringComparer.Ordinal);
    // Same records keyed by timestamp stem, so a panel-reported name in a different
    // form than Kanali's stored fileName (e.g. "<ts>.Mp4" vs "<ts>.mp4.h264_2240x1080")
    // still matches. Keyed by StemKey output (already lower-cased).
    private static Dictionary<string, Entry> _stemIndex = new(StringComparer.Ordinal);
    // Timestamp stem -> original upload video path under kanali\media, the frame source
    // when Kanali has no cover record for a panel file.
    private static Dictionary<string, string> _mediaVideos = new(StringComparer.Ordinal);
    private static string? _dataDir;
    private static DateTime _lastLocateUtc = DateTime.MinValue;
    private static bool _parsed;
    private static DateTime _storeStamp;
    private static DateTime _materialStamp;

    private static string CacheDir { get; } = Path.Combine(
        Nexus.Service.Media.MediaLibrary.DeviceStoreDir("tryx"), "kanali-thumbs");

    // Kanali covers can be multi-MB PNGs; downscale to a small JPEG before inlining as a
    // data URL, then cap the encoded result the same as the upload thumbnail cache.
    private const int MaxThumbBytes = 512 * 1024;

    /// <summary>Returns the display name + cover-derived thumbnail data URL for a
    /// panel-reported device filename, or null when Kanali has no record of it.</summary>
    public static (string? DisplayName, string? Thumb)? Lookup(string deviceFileName)
    {
        Entry? entry;
        string? sourceVideo;
        lock (Lock)
        {
            EnsureFresh();
            if (!_index.TryGetValue(deviceFileName, out entry))
            {
                _stemIndex.TryGetValue(StemKey(deviceFileName), out entry);
            }
            _mediaVideos.TryGetValue(StemKey(deviceFileName), out sourceVideo);
        }
        string? thumb = entry?.CoverPath is { } cover ? EnsureThumbDataUrl(deviceFileName, cover) : null;
        // No Kanali cover record (or its cover file is gone): extract a frame from the
        // original upload Kanali still keeps under its media dir, matched by stem.
        if (thumb is null && sourceVideo is not null)
        {
            thumb = EnsureThumbDataUrl(deviceFileName, sourceVideo);
        }
        if (entry is null && thumb is null) return null;
        return (entry?.DisplayName, thumb);
    }

    // Rebuilds the index when the Kanali data dir, or either source JSON's mtime, changes.
    private static void EnsureFresh()
    {
        try
        {
            // Re-locate when we have no dir yet (Kanali may be installed after the service
            // starts), throttled so an absent Kanali doesn't rescan every lookup.
            if (_dataDir is null && (DateTime.UtcNow - _lastLocateUtc) > TimeSpan.FromSeconds(30))
            {
                _lastLocateUtc = DateTime.UtcNow;
                _dataDir = LocateDataDir();
            }
            if (_dataDir is null)
            {
                _index = new(StringComparer.Ordinal);
                _stemIndex = new(StringComparer.Ordinal);
                _mediaVideos = new(StringComparer.Ordinal);
                _parsed = false;
                return;
            }

            var storePath = Path.Combine(_dataDir, "store.json");
            var materialPath = Path.Combine(_dataDir, "material.json");
            var storeStamp = StampOf(storePath);
            var materialStamp = StampOf(materialPath);
            // Re-parse only on an mtime change; tracked separately from the entry count so a
            // panel whose media Kanali doesn't know (empty index) doesn't re-read every call.
            // This also gates the _mediaVideos rebuild: a Kanali upload rewrites store.json, so
            // its media dir is re-enumerated then; a file appearing under media\ with no JSON
            // write stays invisible to the fallback until the next store.json change.
            if (_parsed && storeStamp == _storeStamp && materialStamp == _materialStamp)
            {
                return;
            }
            var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
            ParseMaterial(materialPath, next);
            ParseStore(storePath, next);
            _index = next;
            _stemIndex = BuildStemIndex(next);
            _mediaVideos = BuildMediaVideoIndex(_dataDir);
            _storeStamp = storeStamp;
            _materialStamp = materialStamp;
            _parsed = true;
        }
        // Reset _parsed so the next call re-parses rather than trusting stale stamps after a fault.
        catch
        {
            _index = new(StringComparer.Ordinal);
            _stemIndex = new(StringComparer.Ordinal);
            _mediaVideos = new(StringComparer.Ordinal);
            _parsed = false;
        }
    }

    /// <summary>Panel filename -> timestamp stem, tolerant of Kanali's naming variants
    /// ("&lt;ts&gt;.mp4.h264_2240x1080", "&lt;ts&gt;.Mp4", "&lt;ts&gt;.mp4"): cut at the
    /// first ".mp4" (any case), else drop the last extension. Lower-cased so lookups are
    /// case-insensitive.</summary>
    internal static string StemKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var cut = name.IndexOf(".mp4", StringComparison.OrdinalIgnoreCase);
        var stem = cut >= 0 ? name.Substring(0, cut) : Path.GetFileNameWithoutExtension(name);
        return stem.ToLowerInvariant();
    }

    private static Dictionary<string, Entry> BuildStemIndex(Dictionary<string, Entry> exact)
    {
        var stem = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var kv in exact)
        {
            var key = StemKey(kv.Key);
            if (key.Length != 0) stem[key] = kv.Value;
        }
        return stem;
    }

    // Original uploads Kanali keeps at <dataDir>\media\<ts>.mp4 (root only; the transcoded
    // .h264 output and material-cache previews are excluded).
    private static Dictionary<string, string> BuildMediaVideoIndex(string dataDir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var mediaDir = Path.Combine(dataDir, "media");
            if (!Directory.Exists(mediaDir)) return map;
            foreach (var path in Directory.EnumerateFiles(mediaDir, "*.mp4"))
            {
                var key = StemKey(Path.GetFileName(path));
                if (key.Length != 0) map.TryAdd(key, path);
            }
        }
        catch { /* best effort */ }
        return map;
    }

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Scans every user profile's Roaming dir for a Kanali data folder and returns
    /// the one whose store.json was written most recently (the active user's), or null.
    /// The path is not fixed: the profile varies per machine and Kanali need not be
    /// installed at all.</summary>
    private static string? LocateDataDir()
    {
        string? best = null;
        var bestStamp = DateTime.MinValue;
        foreach (var candidate in CandidateDirs())
        {
            try
            {
                var store = Path.Combine(candidate, "store.json");
                if (!File.Exists(store)) continue;
                var stamp = File.GetLastWriteTimeUtc(store);
                if (stamp >= bestStamp) { bestStamp = stamp; best = candidate; }
            }
            catch { /* skip unreadable profile */ }
        }
        return best;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // An interactive (non-service) run resolves the real user's Roaming directly.
        string? appData = null;
        try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        catch { /* ignore */ }
        if (!string.IsNullOrEmpty(appData))
        {
            var d = Path.Combine(appData, "kanali");
            if (seen.Add(d)) yield return d;
        }
        // The service runs as LocalSystem, whose Roaming is the SYSTEM profile's; scan every
        // real profile under <systemdrive>\Users instead.
        string? usersRoot = null;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(root)) usersRoot = Path.Combine(root, "Users");
        }
        catch { /* ignore */ }
        if (usersRoot is null || !SafeDirExists(usersRoot)) yield break;
        string[] profiles;
        try { profiles = Directory.GetDirectories(usersRoot); }
        catch { yield break; }
        foreach (var profile in profiles)
        {
            var leaf = Path.GetFileName(profile);
            if (leaf is "Public" or "Default" or "Default User" or "All Users") continue;
            var d = Path.Combine(profile, "AppData", "Roaming", "kanali");
            if (seen.Add(d)) yield return d;
        }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    // material.json: { materialList: { <PRODUCT>: [ { name, coverFileLocalPath,
    // hardwareInfo: [ { fileName } ] }, ... ], ... } }. Cloud themes carry the panel-side
    // filename in hardwareInfo[].fileName. Iterates every product array, not just this
    // panel's, so it works across Tryx models.
    private static void ParseMaterial(string path, Dictionary<string, Entry> into)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("materialList", out var list) ||
                list.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            foreach (var product in list.EnumerateObject())
            {
                if (product.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in product.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = StringOf(item, "name");
                    var cover = StringOf(item, "coverFileLocalPath");
                    if (!item.TryGetProperty("hardwareInfo", out var hw) ||
                        hw.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var entry in hw.EnumerateArray())
                    {
                        var fileName = StringOf(entry, "fileName");
                        if (string.IsNullOrEmpty(fileName)) continue;
                        into[fileName] = new Entry(
                            string.IsNullOrEmpty(name) ? fileName : name,
                            string.IsNullOrEmpty(cover) ? null : cover);
                    }
                }
            }
        }
        catch { /* best effort */ }
    }

    // store.json: { waterBlockScreenCustomMedia: [ { name, thumb, fileName: [ [ <device> ]
    // ] }, ... ] }. Local uploads; fileName is nested arrays (split-screen slots).
    private static void ParseStore(string path, Dictionary<string, Entry> into)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("waterBlockScreenCustomMedia", out var media) ||
                media.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (var item in media.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = StringOf(item, "name");
                var thumb = StringOf(item, "thumb");
                if (!item.TryGetProperty("fileName", out var fileName)) continue;
                foreach (var device in FlattenStrings(fileName))
                {
                    into[device] = new Entry(
                        string.IsNullOrEmpty(name) ? device : name,
                        string.IsNullOrEmpty(thumb) ? null : thumb);
                }
            }
        }
        catch { /* best effort */ }
    }

    private static IEnumerable<string> FlattenStrings(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrEmpty(s)) yield return s;
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                foreach (var s in FlattenStrings(child))
                {
                    yield return s;
                }
            }
        }
    }

    private static string StringOf(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    // Downscales the Kanali cover to a cached JPEG (keyed by device filename) and returns it as
    // a data URL. Reuses the bundled ffmpeg; regenerates when the source is newer. Writes via a
    // per-call temp file + atomic rename so concurrent list requests for the same uncached name
    // don't read a half-written JPEG or clobber each other.
    private static string? EnsureThumbDataUrl(string deviceFileName, string coverPath)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName)) return null;
        try
        {
            if (!File.Exists(coverPath)) return null;
            var dest = Path.Combine(CacheDir, deviceFileName + ".jpg");
            if (!File.Exists(dest) ||
                File.GetLastWriteTimeUtc(dest) < File.GetLastWriteTimeUtc(coverPath))
            {
                var ffmpeg = FfmpegResolver.Path;
                if (ffmpeg is null) return null;
                Directory.CreateDirectory(CacheDir);
                var tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                if (!TryxThumbnailCache.ExtractFrameTo(ffmpeg, coverPath, tmp))
                {
                    try { File.Delete(tmp); } catch { /* nothing to clean */ }
                    return null;
                }
                try { File.Move(tmp, dest, overwrite: true); }
                catch { try { File.Delete(tmp); } catch { /* nothing to clean */ } }
            }
            var info = new FileInfo(dest);
            if (!info.Exists || info.Length == 0 || info.Length > MaxThumbBytes) return null;
            return "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(dest));
        }
        catch { return null; }
    }
}
