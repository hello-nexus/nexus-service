using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Folder-based library of SL-LCD Wireless media items sized to the panel's
/// fixed resolution, mirroring <see cref="CorsairLinkLcdMediaLibrary"/>. Each
/// item lives under <c>&lt;root&gt;/&lt;id&gt;/</c>: meta.json +
/// frame-0000.jpg [frame-0001.jpg ...].
/// </summary>
public sealed class Slv3LcdMediaLibrary
{
    private const string MetaFileName = "meta.json";

    private static readonly string[] StillExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".webm", ".mkv", ".m4v", ".avi" };

    // ffprobe is not bundled, so the true source fps is not read; video frames
    // are resampled to this fixed output rate instead (same simplification as
    // MediaImporter.ImportAsync's animated path). The frame cap bounds a
    // JPEG-per-frame import to a short clip relative to that rate.
    private const int VideoFps = 15;
    private const int VideoMaxFrames = 150;

    private readonly string _rootDir;

    public Slv3LcdMediaLibrary()
        : this(MediaLibrary.DeviceStoreDir("lianli-wireless"))
    {
    }

    internal Slv3LcdMediaLibrary(string rootDir)
    {
        _rootDir = rootDir;
        Directory.CreateDirectory(rootDir);
    }

    public List<Slv3LcdMediaItem> ListItems()
    {
        var items = new List<Slv3LcdMediaItem>();
        if (!Directory.Exists(_rootDir))
        {
            return items;
        }

        foreach (var dir in Directory.GetDirectories(_rootDir))
        {
            if (!File.Exists(Path.Combine(dir, MetaFileName))) continue;
            if (!File.Exists(Path.Combine(dir, "frame-0000.jpg"))) continue;

            try
            {
                var json = File.ReadAllText(Path.Combine(dir, MetaFileName));
                var item = JsonSerializer.Deserialize(json, AppJsonContext.Default.Slv3LcdMediaItem);
                if (item is not null && MediaLibrary.IsValidId(item.Id))
                {
                    items.Add(item);
                }
            }
            catch { /* skip corrupt entries */ }
        }

        items.Sort((a, b) => b.ImportedAtUnixMs.CompareTo(a.ImportedAtUnixMs));
        return items;
    }

    /// <summary>
    /// Reads JPEG frame files from disk and builds in-memory image data.
    /// Returns null if the item does not exist or has no readable frames.
    /// </summary>
    public Slv3LcdImageData? LoadFrames(string id)
    {
        if (!MediaLibrary.IsValidId(id)) return null;
        var dir = Path.Combine(_rootDir, id);
        if (!Directory.Exists(dir)) return null;

        Slv3LcdMediaItem? meta;
        try
        {
            var metaPath = Path.Combine(dir, MetaFileName);
            if (!File.Exists(metaPath)) return null;
            meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), AppJsonContext.Default.Slv3LcdMediaItem);
        }
        catch { return null; }

        if (meta is null || !MediaLibrary.IsValidId(meta.Id)) return null;

        var frames = new List<Slv3LcdFrame>();
        for (var i = 0; ; i++)
        {
            var path = Path.Combine(dir, $"frame-{i:D4}.jpg");
            if (!File.Exists(path)) break;

            byte[] jpeg;
            try
            {
                jpeg = File.ReadAllBytes(path);
            }
            catch { break; }

            var delayMs = (meta.Delays is not null && i < meta.Delays.Length) ? meta.Delays[i] : 0;
            frames.Add(new Slv3LcdFrame { JpegBytes = jpeg, DelayMs = delayMs });
        }

        if (frames.Count == 0) return null;
        return new Slv3LcdImageData { Id = id, Frames = frames.ToArray() };
    }

    /// <summary>Absolute path to the item's first frame (its thumbnail), or null if absent.</summary>
    public string? GetThumbnailPath(string id)
    {
        if (!MediaLibrary.IsValidId(id)) return null;
        var path = Path.Combine(_rootDir, id, "frame-0000.jpg");
        return File.Exists(path) ? path : null;
    }

    public bool DeleteItem(string id)
    {
        if (!MediaLibrary.IsValidId(id)) return false;
        var dir = Path.Combine(_rootDir, id);
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Imports a still image (JPEG/PNG/BMP/WebP), an animated GIF, or a video
    /// as LCD JPEG frames sized to the panel's fixed resolution. Stills reuse
    /// <see cref="Slv3LcdImage"/>'s size-capped encode; GIF/video frames are
    /// extracted directly via ffmpeg and are not individually size-checked
    /// (mirrors <see cref="CorsairLinkLcdMediaLibrary"/>, which has no such
    /// cap either). GIF frame delays are parsed from the raw file bytes;
    /// video frames get a uniform delay from the fixed extraction rate.
    /// </summary>
    public async Task<Slv3LcdImportResult> ImportAsync(
        string sourcePath, string originalName, Slv3LcdCropRect? crop = null)
    {
        if (FfmpegResolver.Path is null)
        {
            return Slv3LcdImportResult.Failure("LCD media import requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var isStill = Array.Exists(StillExtensions, e => e == ext);
        var isGif = ext == ".gif";
        var isVideo = Array.Exists(VideoExtensions, e => e == ext);

        if (!isStill && !isGif && !isVideo)
        {
            return Slv3LcdImportResult.Failure("Unsupported format. Use JPEG, PNG, BMP, WebP, GIF, or a video file.");
        }

        var kind = isStill ? "image" : isGif ? "gif" : "video";
        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var id = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");
        var dir = Path.Combine(_rootDir, id);

        try
        {
            Directory.CreateDirectory(dir);
            // The panel is physically round (the frame's corners are off-screen),
            // so pad instead of squash, matching Slv3LcdImage's still-image filter.
            var padFilter =
                $"scale={Slv3LcdProtocol.PanelWidth}:{Slv3LcdProtocol.PanelHeight}:force_original_aspect_ratio=decrease,pad={Slv3LcdProtocol.PanelWidth}:{Slv3LcdProtocol.PanelHeight}:-1:-1:color=black";
            // The web cropper picks the square region to keep; apply it before
            // the scale/pad so gif/video crop like stills do.
            var cropPadFilter = crop is { IsFullFrame: false } cr ? $"{cr.ToFfmpegFilter()},{padFilter}" : padFilter;

            var delays = Array.Empty<int>();
            if (isStill)
            {
                var sourceBytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(false);
                var encoded = await Slv3LcdImage.EncodeAsync(sourceBytes, ext, crop).ConfigureAwait(false);
                if (!encoded.Ok)
                {
                    Directory.Delete(dir, recursive: true);
                    return Slv3LcdImportResult.Failure(encoded.Error ?? "Encode failed");
                }
                await File.WriteAllBytesAsync(Path.Combine(dir, "frame-0000.jpg"), encoded.JpegBytes!).ConfigureAwait(false);
            }
            else if (isGif)
            {
                // ffmpeg's image2 muxer starts the %04d sequence at 1 by default;
                // force 0 so it lines up with CountFrameFiles/LoadFrames below.
                await MediaImporter.RunFfmpeg(
                    "-y", "-i", sourcePath,
                    "-vf", cropPadFilter,
                    "-vsync", "0",
                    "-start_number", "0",
                    "-q:v", "5",
                    Path.Combine(dir, "frame-%04d.jpg")).ConfigureAwait(false);

                var gifBytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(false);
                delays = CorsairLinkLcdMediaLibrary.ParseGifFrameDelays(gifBytes);
            }
            else
            {
                await MediaImporter.RunFfmpeg(
                    "-y", "-i", sourcePath,
                    "-vf", cropPadFilter,
                    "-r", VideoFps.ToString(),
                    "-frames:v", VideoMaxFrames.ToString(),
                    "-start_number", "0",
                    "-q:v", "5",
                    Path.Combine(dir, "frame-%04d.jpg")).ConfigureAwait(false);
            }

            var frameCount = CountFrameFiles(dir);
            if (frameCount == 0)
            {
                Directory.Delete(dir, recursive: true);
                return Slv3LcdImportResult.Failure("No frames extracted");
            }

            if (isVideo)
            {
                var uniformDelay = 1000 / VideoFps;
                delays = new int[frameCount];
                Array.Fill(delays, uniformDelay);
            }

            var item = new Slv3LcdMediaItem
            {
                Id = id,
                Name = originalName,
                Kind = kind,
                Frames = frameCount,
                Delays = delays,
                ImportedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            SaveMeta(item);
            return Slv3LcdImportResult.Success(item);
        }
        catch (Exception ex)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
            return Slv3LcdImportResult.Failure(ex.Message);
        }
    }

    private static int CountFrameFiles(string dir)
    {
        var count = 0;
        while (File.Exists(Path.Combine(dir, $"frame-{count:D4}.jpg")))
        {
            count++;
        }
        return count;
    }

    internal void SaveMeta(Slv3LcdMediaItem item)
    {
        var dir = Path.Combine(_rootDir, item.Id);
        Directory.CreateDirectory(dir);
        AtomicJsonFile.Write(
            Path.Combine(dir, MetaFileName),
            JsonSerializer.Serialize(item, AppJsonContext.Default.Slv3LcdMediaItem));
    }
}

public readonly record struct Slv3LcdImportResult(Slv3LcdMediaItem? Item, string? Error)
{
    public bool Ok => Item is not null;
    public static Slv3LcdImportResult Success(Slv3LcdMediaItem item) => new(item, null);
    public static Slv3LcdImportResult Failure(string error) => new(null, error);
}
