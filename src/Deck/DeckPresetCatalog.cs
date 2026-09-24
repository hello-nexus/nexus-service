using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nexus.Service.Models.Deck;
using Nexus.Service.Platform;

namespace Nexus.Service.Deck;

/// <summary>
/// Read-only catalog of the .nexus-deck templates bundled under
/// data/deck-presets/&lt;id&gt;/, embedded at build time (see the csproj glob)
/// under manifest names "deck-presets/&lt;id&gt;/...", mirroring
/// BundledFirmwareCatalog's scan. Loaded once, lazily, and cached for the
/// process lifetime - the bundle never changes at runtime.
/// </summary>
public sealed class DeckPresetCatalog
{
    private const string ResourcePrefix = "deck-presets/";

    private readonly Lazy<Dictionary<string, DeckPackageReadResult>> _byId;

    public DeckPresetCatalog() : this(typeof(DeckPresetCatalog).Assembly)
    {
    }

    public DeckPresetCatalog(Assembly assembly)
    {
        _byId = new Lazy<Dictionary<string, DeckPackageReadResult>>(() => Load(assembly));
    }

    public IReadOnlyList<DeckTemplateSummary> Templates =>
        _byId.Value
            .Select(kv => ToSummary(kv.Key, kv.Value.Manifest!))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public DeckPackageReadResult? Open(string id) => _byId.Value.TryGetValue(id, out var result) ? result : null;

    private static Dictionary<string, DeckPackageReadResult> Load(Assembly assembly)
    {
        var files = new Dictionary<string, List<(string LogicalName, string RelativePath)>>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // RecursiveDir can use either separator depending on the build host
            // (macOS AOT build vs Windows installer build); normalize to '/'
            // for parsing, matching BundledFirmwareCatalog.
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var rel = normalized[ResourcePrefix.Length..];
            var slash = rel.IndexOf('/');
            if (slash <= 0 || slash >= rel.Length - 1)
            {
                continue;
            }
            var id = rel[..slash];
            var relativePath = rel[(slash + 1)..];
            if (!files.TryGetValue(id, out var list))
            {
                list = new List<(string, string)>();
                files[id] = list;
            }
            list.Add((name, relativePath));
        }

        var byId = new Dictionary<string, DeckPackageReadResult>(StringComparer.Ordinal);
        foreach (var (id, entries) in files)
        {
            var result = DeckPresetPackage.Read(new EmbeddedPackageSource(assembly, entries));
            if (!result.Ok)
            {
                ServiceLog.Warn($"[deck-presets] bundled template '{id}' failed validation: {result.Error}");
                continue;
            }
            if (result.Manifest!.Id != id)
            {
                ServiceLog.Warn($"[deck-presets] bundled template dir '{id}' has mismatched preset.json id '{result.Manifest.Id}'");
                continue;
            }
            byId[id] = result;
        }
        return byId;
    }

    private static DeckTemplateSummary ToSummary(string id, DeckPackageManifest manifest) => new()
    {
        Id = id,
        Name = manifest.Name,
        Description = manifest.Description,
        Cols = manifest.Cols,
        Rows = manifest.Rows,
        Match = manifest.Match,
        PageCount = manifest.Deck.Pages.Count,
    };
}
