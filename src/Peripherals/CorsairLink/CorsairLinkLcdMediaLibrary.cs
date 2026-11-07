using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Folder-based library of 480x480 LCD media items. Each item lives under
/// <c>&lt;root&gt;/&lt;id&gt;/</c>: meta.json + frame-0000.jpg [frame-0001.jpg ...].
/// </summary>
public sealed class CorsairLinkLcdMediaLibrary
{
    private const string MetaFileName = "meta.json";

    private static readonly string[] StillExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    private readonly string _rootDir;

    public CorsairLinkLcdMediaLibrary()
        : this(MediaLibrary.DeviceStoreDir("corsair-lcd"))
    {
    }

    internal CorsairLinkLcdMediaLibrary(string rootDir)
    {
        _rootDir = rootDir;
        Directory.CreateDirectory(rootDir);
    }

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        foreach (var ch in id)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_') return false;
        }
        return true;
    }

    public List<LcdMediaItem> ListItems()
    {
        var items = new List<LcdMediaItem>();
        if (!Directory.Exists(_rootDir)) return items;

        foreach (var dir in Directory.GetDirectories(_rootDir))
        {
            if (!File.Exists(Path.Combine(dir, MetaFileName))) continue;
            if (!File.Exists(Path.Combine(dir, "frame-0000.jpg"))) continue;

            try
            {
                var json = File.ReadAllText(Path.Combine(dir, MetaFileName));
                var item = JsonSerializer.Deserialize(json, AppJsonContext.Default.LcdMediaItem);
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

    /// <summary>
    /// Reads JPEG frame files from disk and builds in-memory image data.
    /// Returns null if the item does not exist or has no readable frames.
    /// </summary>
    public LcdImageData? LoadImageData(string id)
    {
        if (!IsValidId(id)) return null;
        var dir = Path.Combine(_rootDir, id);
        if (!Directory.Exists(dir)) return null;

        LcdMediaItem? meta;
        try
        {
            var metaPath = Path.Combine(dir, MetaFileName);
            if (!File.Exists(metaPath)) return null;
            meta = JsonSerializer.Deserialize(
                File.ReadAllText(metaPath),
                AppJsonContext.Default.LcdMediaItem);
        }
        catch { return null; }

        if (meta is null || !IsValidId(meta.Id)) return null;

        var frames = new List<LcdFrame>();
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

            var delayMs = (meta.Delays is not null && i < meta.Delays.Length)
                ? meta.Delays[i]
                : 0;
            frames.Add(new LcdFrame { JpegBytes = jpeg, DelayMs = delayMs });
        }

        if (frames.Count == 0) return null;
        return new LcdImageData { Id = id, Frames = frames.ToArray() };
    }

    public bool DeleteItem(string id)
    {
        if (!IsValidId(id)) return false;
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
    /// Imports a still image (JPEG/PNG/BMP/WebP) or animated GIF as 480x480 LCD JPEG frames.
    /// Uses ffmpeg for conversion. GIF frame delays are parsed from the raw file bytes;
    /// no C# pixel decoding is performed.
    /// </summary>
    public async Task<LcdImportResult> ImportAsync(string sourcePath, string originalName)
    {
        if (FfmpegResolver.Path is null)
        {
            return LcdImportResult.Failure("LCD media import requires ffmpeg, which is missing. Reinstall nexus-service.");
        }

        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var isStill = Array.Exists(StillExtensions, e => e == ext);
        var isGif = ext == ".gif";

        if (!isStill && !isGif)
        {
            return LcdImportResult.Failure("Unsupported format. Use JPEG, PNG, BMP, WebP, or GIF.");
        }

        var baseName = Path.GetFileNameWithoutExtension(originalName);
        var id = MediaImporter.SanitizeId($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{baseName}");
        var dir = Path.Combine(_rootDir, id);

        try
        {
            Directory.CreateDirectory(dir);

            if (isStill)
            {
                await MediaImporter.RunFfmpeg(
                    "-y", "-i", sourcePath,
                    "-vf", "scale=480:480",
                    // The LCD decodes only 4:2:0 JPEG; ffmpeg keeps an RGB source at 4:4:4.
                    "-pix_fmt", "yuvj420p",
                    "-frames:v", "1",
                    "-q:v", "5",
                    Path.Combine(dir, "frame-0000.jpg")).ConfigureAwait(false);
            }
            else
            {
                await MediaImporter.RunFfmpeg(
                    "-y", "-i", sourcePath,
                    "-vf", "scale=480:480",
                    // The LCD decodes only 4:2:0 JPEG; ffmpeg keeps an RGB source at 4:4:4.
                    "-pix_fmt", "yuvj420p",
                    "-vsync", "0",
                    "-q:v", "5",
                    // image2 numbers from 1 unless told otherwise; frames are read from 0000.
                    "-start_number", "0",
                    Path.Combine(dir, "frame-%04d.jpg")).ConfigureAwait(false);
            }

            var frameCount = CountFrameFiles(dir);
            if (frameCount == 0)
            {
                Directory.Delete(dir, recursive: true);
                return LcdImportResult.Failure("No frames extracted");
            }

            var delays = Array.Empty<int>();
            if (isGif)
            {
                var gifBytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(false);
                delays = ParseGifFrameDelays(gifBytes);
            }

            var item = new LcdMediaItem
            {
                Id = id,
                Name = originalName,
                Type = frameCount > 1 ? "animated" : "static",
                Frames = frameCount,
                Delays = delays,
                ImportedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            SaveMeta(item);
            return LcdImportResult.Success(item);
        }
        catch (Exception ex)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
            return LcdImportResult.Failure(ex.Message);
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

    internal void SaveMeta(LcdMediaItem item)
    {
        var dir = Path.Combine(_rootDir, item.Id);
        Directory.CreateDirectory(dir);
        AtomicJsonFile.Write(
            Path.Combine(dir, MetaFileName),
            JsonSerializer.Serialize(item, AppJsonContext.Default.LcdMediaItem));
    }

    /// <summary>
    /// One delay per image of a GIF, from the Graphic Control Extension before it
    /// ([packed, delay_lo, delay_hi, transparent]). The file is walked block by block, so the
    /// bytes 0x21 0xF9 0x04 inside compressed image data are not read as a delay: scanning for
    /// them found extra "frames" with delays of minutes. Delay in GIF is centiseconds; result is
    /// milliseconds with a 10ms minimum per lsh.go GIF handling. A truncated file yields the
    /// images read so far.
    /// </summary>
    internal static int[] ParseGifFrameDelays(byte[] data)
    {
        var delays = new List<int>();
        // Header (6) and logical screen descriptor (7); a global colour table follows if flagged.
        if (data.Length < 13 || data[0] != (byte)'G' || data[1] != (byte)'I' || data[2] != (byte)'F')
        {
            return delays.ToArray();
        }
        var pos = 13 + ColourTableLength(data[10]);
        var pendingDelay = 0;
        while (pos < data.Length)
        {
            var block = data[pos++];
            if (block == 0x21 && pos < data.Length)
            {
                var label = data[pos++];
                if (label == 0xF9 && pos + 4 < data.Length && data[pos] == 4)
                {
                    pendingDelay = data[pos + 2] | (data[pos + 3] << 8);
                }
                pos = SkipSubBlocks(data, pos);
            }
            else if (block == 0x2C && pos + 9 <= data.Length)
            {
                // Image descriptor (9), local colour table, LZW code size (1), then the image data.
                pos += 9 + ColourTableLength(data[pos + 8]) + 1;
                if (pos > data.Length)
                {
                    break;
                }
                pos = SkipSubBlocks(data, pos);
                delays.Add(Math.Max(10, pendingDelay * 10));
                pendingDelay = 0;
            }
            else
            {
                // 0x3B trailer, or something that is not a GIF block.
                break;
            }
        }
        return delays.ToArray();

        static int ColourTableLength(byte packed) => (packed & 0x80) != 0 ? 3 * (1 << ((packed & 0x07) + 1)) : 0;

        // Data sub-blocks: a length byte and that many bytes, until a zero length.
        static int SkipSubBlocks(byte[] data, int pos)
        {
            while (pos < data.Length)
            {
                var length = data[pos++];
                if (length == 0)
                {
                    break;
                }
                pos += length;
            }
            return pos;
        }
    }
}

public readonly record struct LcdImportResult(LcdMediaItem? Item, string? Error)
{
    public bool Ok => Item is not null;
    public static LcdImportResult Success(LcdMediaItem item) => new(item, null);
    public static LcdImportResult Failure(string error) => new(null, error);
}
