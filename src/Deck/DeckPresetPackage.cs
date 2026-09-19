using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Deck;

/// <summary>preset.json inside a .nexus-deck package - bundled (data/deck-presets), imported, or exported. See plan deck-modes.md CONTRACT ADDENDUM.</summary>
public sealed class DeckPackageManifest
{
    public int Format { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public DeckPackageMatch? Match { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public DeckConfig Deck { get; set; } = new();
}

public sealed class DeckPackageMatch
{
    public List<string>? ProcessNames { get; set; }
    public List<string>? DisplayNames { get; set; }
}

/// <summary>One entry inside a package (preset.json or an assets/&lt;sha256&gt;.&lt;ext&gt; file). Length is the declared/known uncompressed size, checked before Open() decompresses or copies anything.</summary>
public sealed class DeckPackageEntry
{
    public required string Name { get; init; }
    public required long Length { get; init; }
    public required Func<byte[]> Open { get; init; }
}

/// <summary>A package's contents, either a ZIP upload/export or the embedded bundled-template directory form - both read through the same DeckPresetPackage.Read.</summary>
public interface IDeckPackageSource
{
    IReadOnlyList<DeckPackageEntry> Entries();
}

/// <summary>ZIP-backed source: POST /deck/presets/import's raw body, or DeckPresetPackage.Write's own output read back in tests.</summary>
public sealed class ZipPackageSource : IDeckPackageSource
{
    private readonly ZipArchive _zip;

    public ZipPackageSource(Stream zipStream) => _zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

    public IReadOnlyList<DeckPackageEntry> Entries() =>
        _zip.Entries.Select(e => new DeckPackageEntry { Name = e.FullName, Length = e.Length, Open = () => ReadAll(e) }).ToList();

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        // entry.Length is metadata from the zip's own central directory, never
        // verified against the actual deflate stream, so a crafted entry can
        // decompress to far more than it declares. Cap the real read at the
        // smaller of the declared length and the package-wide limit so a
        // lying zip cannot force an unbounded allocation here.
        CopyBounded(stream, ms, Math.Min(entry.Length, DeckPresetPackage.MaxPackageBytes));
        return ms.ToArray();
    }

    /// <summary>Copies at most limit bytes from source to destination; throws InvalidDataException instead of reading further once source has more. Internal for direct testing.</summary>
    internal static void CopyBounded(Stream source, Stream destination, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - total + 1))) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("package entry exceeds its declared length");
            }
            destination.Write(buffer, 0, read);
        }
    }
}

/// <summary>
/// Embedded-resource-backed source for a bundled template: every
/// "deck-presets/&lt;id&gt;/..." manifest resource for one id, read eagerly
/// (bundled files are small and trusted, unlike an upload) so Entries() never
/// re-touches the assembly.
/// </summary>
public sealed class EmbeddedPackageSource : IDeckPackageSource
{
    private readonly List<DeckPackageEntry> _entries;

    /// <param name="files">(assembly manifest resource name, path relative to the package root e.g. "preset.json" or "assets/&lt;hash&gt;.png").</param>
    public EmbeddedPackageSource(System.Reflection.Assembly assembly, IEnumerable<(string LogicalName, string RelativePath)> files)
    {
        _entries = new List<DeckPackageEntry>();
        foreach (var (logicalName, relativePath) in files)
        {
            using var stream = assembly.GetManifestResourceStream(logicalName);
            if (stream is null)
            {
                continue;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            _entries.Add(new DeckPackageEntry { Name = relativePath, Length = bytes.Length, Open = () => bytes });
        }
    }

    public IReadOnlyList<DeckPackageEntry> Entries() => _entries;
}

/// <summary>Read() outcome: Manifest/Assets are non-null only when Error is null.</summary>
public sealed class DeckPackageReadResult
{
    public bool Ok => Error is null;
    public string? Error { get; init; }
    public DeckPackageManifest? Manifest { get; init; }
    /// <summary>Every referenced image icon's id (sha256 hex, no extension) mapped to its validated bytes and original extension (".png" or ".jpg").</summary>
    public IReadOnlyDictionary<string, (byte[] Bytes, string Ext)> Assets { get; init; } = new Dictionary<string, (byte[], string)>();

    public static DeckPackageReadResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// Reader/writer for the .nexus-deck package format: preset.json plus
/// optional assets/&lt;sha256&gt;.&lt;png|jpg&gt; image originals. The same
/// Read path serves bundled templates (DeckPresetCatalog), imported packages
/// (POST /deck/presets/import) and round-trip tests.
/// </summary>
public static class DeckPresetPackage
{
    public const long MaxPackageBytes = 20 * 1024 * 1024;
    public const long MaxAssetBytes = 4 * 1024 * 1024;

    private const string PresetJsonName = "preset.json";
    private const string AssetsPrefix = "assets/";

    public static DeckPackageReadResult Read(IDeckPackageSource source)
    {
        var entries = source.Entries();
        var totalBytes = entries.Sum(e => e.Length);
        if (totalBytes > MaxPackageBytes)
        {
            return DeckPackageReadResult.Fail("package exceeds the 20 MB size limit");
        }

        var presetEntry = entries.FirstOrDefault(e => e.Name == PresetJsonName);
        if (presetEntry is null)
        {
            return DeckPackageReadResult.Fail("package is missing preset.json");
        }

        DeckPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(presetEntry.Open(), AppJsonContext.Default.DeckPackageManifest);
        }
        catch (JsonException)
        {
            return DeckPackageReadResult.Fail("preset.json is not valid JSON");
        }
        if (manifest is null)
        {
            return DeckPackageReadResult.Fail("preset.json is not valid JSON");
        }
        if (manifest.Format != 1)
        {
            return DeckPackageReadResult.Fail($"unsupported package format {manifest.Format}");
        }
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            return DeckPackageReadResult.Fail("preset.json is missing id");
        }
        if (manifest.Deck is null)
        {
            return DeckPackageReadResult.Fail("preset.json is missing deck");
        }
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            return DeckPackageReadResult.Fail("preset.json is missing name");
        }
        if (manifest.Cols is < 1 or > 8 || manifest.Rows is < 1 or > 8)
        {
            return DeckPackageReadResult.Fail("preset.json cols/rows must be between 1 and 8");
        }

        var assetEntries = entries.Where(e => e.Name.StartsWith(AssetsPrefix, StringComparison.Ordinal) && e.Name.Length > AssetsPrefix.Length).ToList();
        foreach (var asset in assetEntries)
        {
            if (asset.Length > MaxAssetBytes)
            {
                return DeckPackageReadResult.Fail($"asset {asset.Name} exceeds the 4 MB per-asset limit");
            }
        }

        var referencedIds = ImageIconIds(manifest.Deck).Distinct(StringComparer.Ordinal).ToList();
        var assets = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        foreach (var id in referencedIds)
        {
            var pngName = $"{AssetsPrefix}{id}.png";
            var jpgName = $"{AssetsPrefix}{id}.jpg";
            var asset = assetEntries.FirstOrDefault(a => a.Name == pngName || a.Name == jpgName);
            if (asset is null)
            {
                return DeckPackageReadResult.Fail($"package is missing the asset for icon {id}");
            }
            var bytes = asset.Open();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, id, StringComparison.Ordinal))
            {
                return DeckPackageReadResult.Fail($"asset {asset.Name} hash does not match its file name");
            }
            assets[id] = (bytes, asset.Name == pngName ? ".png" : ".jpg");
        }

        return new DeckPackageReadResult { Manifest = manifest, Assets = assets };
    }

    /// <summary>Builds the ZIP bytes for a preset's export: preset.json plus every referenced image icon's original from imageStore. Missing originals are skipped (best effort - the exported package still opens, just without that icon).</summary>
    public static byte[] Write(DeckPreset preset, DeckImageStore imageStore)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new DeckPackageManifest
            {
                Format = 1,
                Id = preset.TemplateId ?? preset.Id,
                Name = preset.Name,
                Author = preset.Author,
                Version = preset.Version,
                Description = preset.Description,
                Cols = preset.Cols,
                Rows = preset.Rows,
                Deck = preset.Deck,
            };
            using (var presetStream = zip.CreateEntry(PresetJsonName).Open())
            {
                JsonSerializer.Serialize(presetStream, manifest, AppJsonContext.Default.DeckPackageManifest);
            }

            foreach (var id in ImageIconIds(preset.Deck).Distinct(StringComparer.Ordinal))
            {
                var loaded = imageStore.TryLoad(id);
                if (loaded is null)
                {
                    continue;
                }
                var ext = loaded.Value.ContentType == "image/png" ? ".png" : ".jpg";
                using var assetStream = zip.CreateEntry($"{AssetsPrefix}{id}{ext}").Open();
                assetStream.Write(loaded.Value.Bytes);
            }
        }
        return ms.ToArray();
    }

    private static IEnumerable<string> ImageIconIds(DeckConfig deck) =>
        deck.Pages.SelectMany(p => ImageIconIds(p.Slots));

    private static IEnumerable<string> ImageIconIds(List<DeckSlot> slots)
    {
        foreach (var slot in slots)
        {
            if (slot.Icon is { Kind: "image" } icon && icon.Value.Length > 0)
            {
                yield return icon.Value;
            }
            if (slot.Folder is not null)
            {
                foreach (var id in ImageIconIds(slot.Folder.Slots))
                {
                    yield return id;
                }
            }
        }
    }
}
