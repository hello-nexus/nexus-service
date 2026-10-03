#if DEV_TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>Preview of the listing app-publish.yml would register, built from the installed manifest and its store/ dir.</summary>
public static class LocalStoreListing
{
    public const string MediaRoute = "/apps-api/store/local-media/";

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
    };

    private static readonly string[] IconExtensions = { "svg", "png", "webp" };

    public static bool HasStoreDir(AppEntry entry) => Directory.Exists(Path.Combine(entry.RootPath, "store"));

    /// <summary>Full detail for one installed app; null when its manifest cannot be read.</summary>
    public static JsonObject? Detail(AppEntry entry, string? locale)
    {
        var item = Item(entry, locale);
        if (item is null) return null;
        var storeDir = Path.Combine(entry.RootPath, "store");

        var screenshots = new JsonArray();
        var shotsDir = Path.Combine(storeDir, "screenshots");
        if (Directory.Exists(shotsDir))
        {
            foreach (var name in Directory.EnumerateFiles(shotsDir).Select(Path.GetFileName).OfType<string>()
                         .Where(n => !n.StartsWith('.')).OrderBy(n => n, StringComparer.Ordinal))
            {
                screenshots.Add((JsonNode?)JsonValue.Create(MediaUrl(entry.Id, "screenshots/" + name)));
            }
        }
        item["screenshots"] = screenshots;

        var latest = (JsonObject)item["latest"]!;
        item["versions"] = new JsonArray(new JsonObject
        {
            ["version"] = entry.Manifest.Version,
            ["releasedAt"] = latest["releasedAt"]!.GetValue<string>(),
            ["minNexusVersion"] = entry.Manifest.MinNexusVersion,
            ["notes"] = latest["notes"]!.GetValue<string>(),
        });
        return item;
    }

    /// <summary>The storefront-card form: detail without screenshots and versions.</summary>
    public static JsonObject? Item(AppEntry entry, string? locale)
    {
        var manifest = ReadManifest(entry);
        if (manifest is null) return null;
        var m = entry.Manifest;
        var storeDir = Path.Combine(entry.RootPath, "store");

        var text = ListingText(manifest, locale);
        var iconUrl = IconExtensions
            .Where(ext => File.Exists(Path.Combine(storeDir, "icon." + ext)))
            .Select(ext => MediaUrl(entry.Id, "icon." + ext)).FirstOrDefault();
        var releasedAt = Directory.GetLastWriteTimeUtc(entry.RootPath).ToString("O");
        var releaseDate = StringOf(manifest, "release_date");

        var sizes = new JsonArray();
        foreach (var s in m.Sizes) sizes.Add((JsonNode?)JsonValue.Create(s));
        var surfaces = new JsonArray();
        foreach (var s in m.Surfaces) surfaces.Add((JsonNode?)JsonValue.Create(s));

        return new JsonObject
        {
            ["id"] = entry.Id,
            ["name"] = text.Name,
            ["tagline"] = text.Tagline,
            ["description"] = text.Description,
            // nexus-api never receives a publisher from the publish workflow, so every app carries the column default.
            ["publisher"] = "Nexus",
            ["category"] = string.IsNullOrEmpty(m.Category) ? "other" : m.Category,
            ["iconUrl"] = iconUrl,
            ["releaseDate"] = string.IsNullOrEmpty(releaseDate) ? null : releaseDate,
            ["rating"] = new JsonObject { ["average"] = 0, ["count"] = 0 },
            ["latest"] = new JsonObject
            {
                ["version"] = m.Version,
                ["sha256"] = "",
                ["size"] = FolderBytes(entry.RootPath),
                ["minNexusVersion"] = m.MinNexusVersion,
                ["hasWidget"] = m.Sizes.Count > 0,
                ["hasPage"] = m.Page,
                ["requiresTouch"] = manifest["requires_touch"] is JsonValue t && t.TryGetValue<bool>(out var touch) && touch,
                ["sizes"] = sizes,
                ["surfaces"] = surfaces,
                ["releasedAt"] = releasedAt,
                ["capabilities"] = manifest["capabilities"]?.DeepClone() ?? new JsonObject(),
                ["notes"] = Notes(storeDir, locale),
            },
            ["localPreview"] = true,
        };
    }

    /// <summary>Appends a card for each installed app with a store/ dir that the cloud list lacks.</summary>
    public static string AppendLocal(string cloudListJson, IEnumerable<AppEntry> installed, string? locale)
    {
        if (JsonNode.Parse(cloudListJson) is not JsonObject root || root["apps"] is not JsonArray apps) return cloudListJson;
        var known = apps.Select(a => a?["id"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in installed.Where(HasStoreDir).Where(e => !known.Contains(e.Id)))
        {
            if (Item(entry, locale) is { } item) apps.Add((JsonNode?)item);
        }
        return root.ToJsonString();
    }

    /// <summary>Reads one file under the app's store/ dir; null for anything outside it, a link, a non-image, or an oversized file.</summary>
    public static (byte[] Bytes, string ContentType)? ReadMedia(AppEntry entry, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\') || relativePath.Contains('\0')) return null;
        if (relativePath.Split('/').Any(seg => seg is "" or "." or "..")) return null;
        if (!MediaTypes.TryGetValue(Path.GetExtension(relativePath), out var type)) return null;

        var storeDir = Path.GetFullPath(Path.Combine(entry.RootPath, "store"));
        var full = Path.GetFullPath(Path.Combine(storeDir, relativePath));
        if (!full.StartsWith(storeDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;

        var info = new FileInfo(full);
        if (!info.Exists || info.LinkTarget is not null || info.Length > StoreCatalogProxy.MaxMediaBytes) return null;
        return (File.ReadAllBytes(full), type);
    }

    private static string MediaUrl(string appId, string relative) => MediaRoute + appId + "/" + relative;

    private static JsonObject? ReadManifest(AppEntry entry)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(entry.RootPath, "manifest.json"))) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string StringOf(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>The tags a locale resolves through: the exact tag, then its base language.</summary>
    private static string[] LocaleTags(string? locale)
    {
        if (string.IsNullOrEmpty(locale)) return Array.Empty<string>();
        var lower = locale.ToLowerInvariant();
        var dash = lower.IndexOf('-');
        return dash < 0 ? new[] { lower } : new[] { lower, lower[..dash] };
    }

    /// <summary>Mirrors nexus-api listingText: each field from the exact tag, then the base language, then the manifest copy.</summary>
    internal static (string Name, string Tagline, string Description) ListingText(JsonObject manifest, string? locale)
    {
        var id = StringOf(manifest, "id");
        var name = StringOf(manifest, "name");
        var tagline = StringOf(manifest, "tagline").Replace("\r\n", " ").Replace("\n", " ");
        var baseCopy = (
            Name: name.Length > 0 ? name : id,
            Tagline: tagline.Length > 30 ? tagline[..30] : tagline,
            Description: StringOf(manifest, "description"));

        var table = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (manifest["locales"] is JsonObject locales)
        {
            foreach (var (tag, node) in locales)
            {
                if (node is JsonObject copy) table[tag] = copy;
            }
        }

        string Pick(string field, string fallback)
        {
            foreach (var tag in LocaleTags(locale))
            {
                if (table.TryGetValue(tag, out var copy) && StringOf(copy, field) is { Length: > 0 } v) return v;
            }
            return fallback;
        }

        return (Pick("name", baseCopy.Name), Pick("tagline", baseCopy.Tagline), Pick("description", baseCopy.Description));
    }

    /// <summary>The workflow only registers translated notes when whats-new.md exists.</summary>
    internal static string Notes(string storeDir, string? locale)
    {
        var basePath = Path.Combine(storeDir, "whats-new.md");
        if (!File.Exists(basePath)) return "";
        foreach (var tag in LocaleTags(locale))
        {
            var match = Directory.EnumerateFiles(storeDir, "whats-new.*.md")
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), $"whats-new.{tag}.md", StringComparison.OrdinalIgnoreCase));
            if (match is not null) return File.ReadAllText(match).Trim();
        }
        return File.ReadAllText(basePath).Trim();
    }

    private static long FolderBytes(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
#endif
