using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nexus.Service.Models.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Media;

/// <summary>
/// Manages a folder-based media library for lighting effects. Each imported
/// item gets its own folder: meta.json + thumb.jpg + frames.bin. frames.bin
/// is raw RGB24 at the canvas resolution stored in meta (Width x Height), one
/// frame after another. thumb.jpg is a JPEG of the first frame for UI grid
/// previews.
/// </summary>
public sealed class MediaLibrary
{
    private const string MetaFileName = "meta.json";
    private const string ThumbFileName = "thumb.jpg";
    private const string FramesFileName = "frames.bin";
    private const string StagingDirName = ".staging";

    private readonly string _rootDir;

    public MediaLibrary()
        : this(MediaStoreDir("effects"))
    {
    }

    internal static string ResolveDefaultRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share");
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    }

    /// <c>&lt;data-root&gt;/Nexus</c>: %ProgramData% (Windows), ~/Library/Application Support (macOS), $XDG_DATA_HOME (Linux).
    internal static string NexusDataDir() => Path.Combine(ResolveDefaultRoot(), "Nexus");

    /// Per-device store root <c>.../Nexus/devices/&lt;family&gt;</c>. Shared so every device store lands under one root instead of hand-rolling a per-OS path.
    internal static string DeviceStoreDir(string family) => Path.Combine(NexusDataDir(), "devices", family);

    /// Non-device media content <c>.../Nexus/media/&lt;kind&gt;</c> (effects, gallery, deck-images).
    internal static string MediaStoreDir(string kind) => Path.Combine(NexusDataDir(), "media", kind);

    public MediaLibrary(string rootDir)
    {
        _rootDir = rootDir;
        Directory.CreateDirectory(_rootDir);
    }

    public string RootDir => _rootDir;

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
            return false;

        foreach (var ch in id)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
                return false;
        }

        return true;
    }

    public List<MediaItem> ListItems()
    {
        var items = new List<MediaItem>();
        if (!Directory.Exists(_rootDir))
        {
            return items;
        }

        foreach (var dir in Directory.GetDirectories(_rootDir))
        {
            if (!HasCompleteItemFiles(dir))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(Path.Combine(dir, MetaFileName));
                var item = JsonSerializer.Deserialize(json, AppJsonContext.Default.MediaItem);
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

    public MediaItem? GetItem(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var metaPath = Path.Combine(_rootDir, id, MetaFileName);
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            var item = JsonSerializer.Deserialize(File.ReadAllText(metaPath), AppJsonContext.Default.MediaItem);
            return item is not null && IsValidId(item.Id) ? item : null;
        }
        catch { return null; }
    }

    public string GetItemDir(string id) => Path.Combine(_rootDir, RequireValidId(id));
    public string GetThumbPath(string id) => Path.Combine(_rootDir, RequireValidId(id), ThumbFileName);
    public string GetFramesBinPath(string id) => Path.Combine(_rootDir, RequireValidId(id), FramesFileName);

    public string GetStagingDir() => Path.Combine(_rootDir, StagingDirName);

    public string GetStagePreviewPath(string stageId) =>
        Path.Combine(GetStagingDir(), stageId + ".preview.jpg");

    /// <summary>
    /// Finds the raw staged file for stageId (any extension except .preview.jpg).
    /// Returns null if not found.
    /// </summary>
    public string? FindStagedRaw(string stageId)
    {
        var stagingDir = GetStagingDir();
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
    public void DeleteStage(string stageId)
    {
        var stagingDir = GetStagingDir();
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
    public void SweepStaging(int maxAgeMinutes)
    {
        var stagingDir = GetStagingDir();
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

    public void SaveMeta(MediaItem item)
    {
        var dir = Path.Combine(_rootDir, item.Id);
        Directory.CreateDirectory(dir);
        AtomicJsonFile.Write(Path.Combine(dir, MetaFileName), JsonSerializer.Serialize(item, AppJsonContext.Default.MediaItem));
    }

    public bool DeleteItem(string id)
    {
        if (!IsValidId(id))
        {
            return false;
        }

        var dir = Path.Combine(_rootDir, id);
        if (!Directory.Exists(dir))
        {
            return true;
        }

        TryDeleteFile(Path.Combine(dir, MetaFileName));
        TryDeleteFile(Path.Combine(dir, ThumbFileName));
        TryDeleteFile(Path.Combine(dir, FramesFileName));

        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return !HasCompleteItemFiles(dir);
        }
    }

    private static bool HasCompleteItemFiles(string dir)
    {
        return Directory.Exists(dir)
            && File.Exists(Path.Combine(dir, MetaFileName))
            && File.Exists(Path.Combine(dir, ThumbFileName))
            && File.Exists(Path.Combine(dir, FramesFileName));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch { }
    }

    private static string RequireValidId(string id)
    {
        if (!IsValidId(id))
            throw new ArgumentException("Invalid media id.", nameof(id));

        return id;
    }
}
