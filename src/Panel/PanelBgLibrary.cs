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
/// panel-backgrounds/&lt;deviceId&gt;/&lt;assetId&gt;/. Each asset contains only
/// media.mp4 or media.jpg (already cropped and scaled), thumb.jpg, and
/// meta.json. The original source is not retained.
/// Transient uploads live in panel-backgrounds/&lt;deviceId&gt;/.staging/ and are
/// deleted on commit or cancel.
/// </summary>
public sealed class PanelBgLibrary
{
    private const string MetaFileName = "meta.json";
    private const string ThumbFileName = "thumb.jpg";
    private const string StagingDirName = ".staging";

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

            var metaPath = Path.Combine(dir, MetaFileName);
            var thumbPath = Path.Combine(dir, ThumbFileName);
            if (!File.Exists(metaPath) || !File.Exists(thumbPath))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(dir, "media.mp4")) &&
                !File.Exists(Path.Combine(dir, "media.jpg")))
            {
                continue;
            }

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
    public string GetThumbPath(string deviceId, string id) => Path.Combine(_rootDir, deviceId, RequireValidId(id), ThumbFileName);

    /// <summary>Returns the path for the converted media file (media.mp4 or media.jpg).</summary>
    public string GetMediaPath(string deviceId, string id, string ext) => Path.Combine(_rootDir, deviceId, RequireValidId(id), "media" + ext);

    public string GetDeviceDir(string deviceId) => Path.Combine(_rootDir, deviceId);

    // --- Staging helpers ---

    public string GetStagingDir(string deviceId) => Path.Combine(_rootDir, deviceId, StagingDirName);

    public string GetStagePreviewPath(string deviceId, string stageId) =>
        Path.Combine(GetStagingDir(deviceId), stageId + ".preview.jpg");

    /// <summary>
    /// Finds the raw staged file for stageId (any extension except .preview.jpg).
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
            if (!file.EndsWith(".preview.jpg", StringComparison.OrdinalIgnoreCase))
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
