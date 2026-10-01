using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Media;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Panel;

/// <summary>
/// Folder-based store for panel-background assets, scoped per device:
/// panel-backgrounds/&lt;deviceId&gt;/&lt;assetId&gt;/. Each asset contains
/// the converted media (media.jpg/.mp4, or media.png/.gif when it kept its
/// transparency) or an unconverted copy under its own extension
/// (PanelBgItem.MediaExt), a matching thumb.jpg/.png, and meta.json.
/// Transient uploads live in panel-backgrounds/&lt;deviceId&gt;/.staging/ and are
/// deleted on commit or cancel.
/// </summary>
public sealed class PanelBgLibrary
{
    private const string MetaFileName = "meta.json";
    private const string StagingDirName = ".staging";

    private static readonly string[] ThumbFileNames = { "thumb.jpg", "thumb.png" };

    private readonly string _rootDir;

    public PanelBgLibrary()
        : this(MediaLibrary.DeviceStoreDir("panel-backgrounds"))
    {
    }

    public PanelBgLibrary(string rootDir)
    {
        _rootDir = rootDir;
        Directory.CreateDirectory(_rootDir);
    }

    public string RootDir => _rootDir;

    public static bool IsValidId(string? id) => MediaLibrary.IsValidId(id);

    public List<PanelBgItem> ListItems(string deviceId)
    {
        var items = new List<PanelBgItem>();
        var deviceDir = Path.Combine(_rootDir, deviceId);
        if (!Directory.Exists(deviceDir))
        {
            return items;
        }

        foreach (var dir in Directory.GetDirectories(deviceDir))
        {
            // Skip hidden/staging dirs.
            if (Path.GetFileName(dir).StartsWith('.'))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(dir, MetaFileName)) ||
                !Array.Exists(ThumbFileNames, n => File.Exists(Path.Combine(dir, n))) ||
                !Directory.EnumerateFiles(dir, "media.*").Any())
            {
                continue;
            }

            var metaPath = Path.Combine(dir, MetaFileName);

            try
            {
                var item = JsonSerializer.Deserialize(File.ReadAllText(metaPath), AppJsonContext.Default.PanelBgItem);
                if (item is not null && IsValidId(item.Id))
                {
                    items.Add(item);
                }
            }
            catch { /* skip corrupt entries */ }
        }

        items.Sort((a, b) => b.ImportedAtUnixMs.CompareTo(a.ImportedAtUnixMs));
        return items;
    }

    public PanelBgItem? GetItem(string deviceId, string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var metaPath = Path.Combine(_rootDir, deviceId, id, MetaFileName);
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            var item = JsonSerializer.Deserialize(File.ReadAllText(metaPath), AppJsonContext.Default.PanelBgItem);
            return item is not null && IsValidId(item.Id) ? item : null;
        }
        catch { return null; }
    }

    public string GetItemDir(string deviceId, string id) => Path.Combine(_rootDir, deviceId, RequireValidId(id));

    /// <summary>Thumbnail path; a transparent asset's is png so the gallery tile shows through.</summary>
    public string GetThumbPath(string deviceId, string id, bool alpha = false) =>
        Path.Combine(_rootDir, deviceId, RequireValidId(id), alpha ? "thumb.png" : "thumb.jpg");

    /// <summary>Path of the asset's media file for the given extension.</summary>
    public string GetMediaPath(string deviceId, string id, string ext) => Path.Combine(_rootDir, deviceId, RequireValidId(id), "media" + ext);

    public string GetDeviceDir(string deviceId) => Path.Combine(_rootDir, deviceId);

    // --- Staging helpers ---

    public string GetStagingDir(string deviceId) => Path.Combine(_rootDir, deviceId, StagingDirName);

    /// <summary>Preview path for a staged upload; png when the source has transparency to show the cropper.</summary>
    public string GetStagePreviewPath(string deviceId, string stageId, bool alpha = false) =>
        Path.Combine(GetStagingDir(deviceId), stageId + (alpha ? ".preview.png" : ".preview.jpg"));

    /// <summary>The preview written for stageId, whichever extension it took, or null.</summary>
    public string? FindStagedPreview(string deviceId, string stageId)
    {
        foreach (var alpha in new[] { false, true })
        {
            var path = GetStagePreviewPath(deviceId, stageId, alpha);
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the raw staged file for stageId (any extension except the preview).
    /// Returns null if not found.
    /// </summary>
    public string? FindStagedRaw(string deviceId, string stageId)
    {
        var stagingDir = GetStagingDir(deviceId);
        if (!Directory.Exists(stagingDir))
        {
            return null;
        }

        foreach (var file in Directory.GetFiles(stagingDir, stageId + ".*"))
        {
            if (!file.EndsWith(".preview.jpg", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".preview.png", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// Deletes both the raw upload and preview for stageId.
    /// </summary>
    public void DeleteStage(string deviceId, string stageId)
    {
        var stagingDir = GetStagingDir(deviceId);
        if (!Directory.Exists(stagingDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(stagingDir, stageId + ".*"))
        {
            try { File.Delete(file); }
            catch { }
        }
    }

    /// <summary>
    /// Removes staging files older than maxAgeMinutes (abandoned stages).
    /// </summary>
    public void SweepStaging(string deviceId, int maxAgeMinutes)
    {
        var stagingDir = GetStagingDir(deviceId);
        if (!Directory.Exists(stagingDir))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-maxAgeMinutes);
        foreach (var file in Directory.GetFiles(stagingDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch { }
        }
    }

    // --- Committed asset helpers ---

    public void SaveMeta(string deviceId, PanelBgItem item)
    {
        var dir = Path.Combine(_rootDir, deviceId, item.Id);
        Directory.CreateDirectory(dir);
        AtomicJsonFile.Write(Path.Combine(dir, MetaFileName), JsonSerializer.Serialize(item, AppJsonContext.Default.PanelBgItem));
    }

    /// <summary>
    /// Deletes the device's entire media directory - every uploaded asset
    /// plus any staged imports. Returns true when nothing remains. The id
    /// guard keeps a recursive delete from ever resolving outside the media
    /// root ("", ".", ".."); registry-minted ids are base64url and always
    /// pass.
    /// </summary>
    public bool DeleteDeviceMedia(string deviceId)
    {
        if (!IsValidId(deviceId)) return false;
        var dir = GetDeviceDir(deviceId);
        if (!Directory.Exists(dir))
        {
            return true;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return !Directory.Exists(dir);
        }
    }

    public bool DeleteItem(string deviceId, string id)
    {
        if (!IsValidId(id))
        {
            return false;
        }

        var dir = Path.Combine(_rootDir, deviceId, id);
        if (!Directory.Exists(dir))
        {
            return true;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return !Directory.Exists(dir);
        }
    }

    private static string RequireValidId(string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException("Invalid background id.", nameof(id));
        }

        return id;
    }
}
