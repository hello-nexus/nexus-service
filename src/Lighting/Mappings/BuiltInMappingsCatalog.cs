using System;
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Devices;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// The pre-built LED mappings that ship inside the binary: one artifact per
/// product a user can hang off an ARGB header (fans, strips, AIOs, blocks,
/// cases), keyed by a virtual <c>product:</c> key rather than by detected
/// hardware, because nothing on a header announces what is plugged into it.
///
/// Entirely local by design. An ARGB header is the one surface where the user
/// MUST tell us what they wired, so the picker has to work on a machine that
/// has never reached the network - no registry call, no cache warm-up, no
/// online account. The packed file is generated and committed, then embedded
/// by the csproj; read once on first access and cached for the process.
/// </summary>
public static class BuiltInMappingsCatalog
{
    private const string ResourceName = "builtin-led-mappings.json";

    private static IReadOnlyList<BuiltInMappingEntry>? _entries;
    private static Dictionary<string, BuiltInMappingEntry>? _byKey;
    private static readonly object _gate = new();

    public static IReadOnlyList<BuiltInMappingEntry> All
    {
        get
        {
            EnsureLoaded();
            return _entries!;
        }
    }

    /// <summary>The artifact for a product key, or null when the key is not in the catalog.</summary>
    public static MappingArtifact? Find(string key)
    {
        // First-party products are authored in code, not in the generated
        // file, so they are checked before it.
        if (HyteChainArtifacts.Build(key) is { } hyte)
            return hyte;
        EnsureLoaded();
        return _byKey!.TryGetValue(key, out var entry) ? entry.Artifact : null;
    }

    /// <summary>
    /// Substring search over product name, brand and type. Ranked so a name
    /// match beats a brand match and a prefix beats a mid-word hit, which is
    /// what makes typing "ql" surface the QL fan rather than every product
    /// whose description happens to contain those letters.
    /// </summary>
    public static List<BuiltInMappingSummary> Search(string? query, string? type, int limit)
        => Search(query, type, limit, out _);

    /// <summary>
    /// As <see cref="Search(string?, string?, int)"/>, reporting how many rows
    /// matched before <paramref name="limit"/> truncated them - the M in
    /// "showing N of M". The whole catalog is the wrong number there: it counts
    /// rows the filter excluded and misses the generic and first-party rows,
    /// which are not in the packed file.
    /// </summary>
    public static List<BuiltInMappingSummary> Search(string? query, string? type, int limit, out int matched)
    {
        EnsureLoaded();
        var q = query?.Trim();
        var hasQuery = !string.IsNullOrEmpty(q);
        // FirstParty breaks rank ties toward our own products, so "fr" reaches
        // the FR12 before a third-party fan that also matches at that rank.
        var scored = new List<(int Rank, bool FirstParty, string Name, BuiltInMappingSummary Row)>();

        // The generics are not catalogued rows - their geometry is a function of
        // a count the user types - but they belong in the same picker, and
        // ahead of the products: someone whose fan is not listed should reach
        // "Generic Fan" without scrolling past the catalogued ones that are.
        foreach (var generic in GenericRows())
        {
            if (!string.IsNullOrEmpty(type)
                && !string.Equals(generic.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (hasQuery && RankMatch(generic, q!) == int.MaxValue)
                continue;
            scored.Add((-1, true, generic.Name, generic));
        }

        foreach (var row in FirstPartyRows())
        {
            if (!string.IsNullOrEmpty(type)
                && !string.Equals(row.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rank = 0;
            if (hasQuery)
            {
                rank = RankMatch(row, q!);
                if (rank == int.MaxValue)
                    continue;
            }
            scored.Add((rank, true, row.Name, row));
        }

        foreach (var entry in _entries!)
        {
            if (!string.IsNullOrEmpty(type)
                && !string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = Summarize(entry);
            var rank = 0;
            if (hasQuery)
            {
                rank = RankMatch(row, q!);
                if (rank == int.MaxValue)
                    continue;
            }
            scored.Add((rank, false, row.Name, row));
        }

        scored.Sort((a, b) =>
        {
            var byRank = a.Rank.CompareTo(b.Rank);
            if (byRank != 0) return byRank;
            if (a.FirstParty != b.FirstParty) return a.FirstParty ? -1 : 1;
            return string.CompareOrdinal(a.Name, b.Name);
        });

        matched = scored.Count;
        var take = limit > 0 ? Math.Min(limit, scored.Count) : scored.Count;
        var result = new List<BuiltInMappingSummary>(take);
        for (int i = 0; i < take; i++)
            result.Add(scored[i].Row);
        return result;
    }

    /// <summary>The two parametric picks, always offered alongside the catalogued products.</summary>
    private static IEnumerable<BuiltInMappingSummary> GenericRows()
    {
        yield return new BuiltInMappingSummary
        {
            Key = GenericChainArtifacts.FanKey,
            Name = GenericChainArtifacts.NameFor(GenericChainArtifacts.FanKey),
            Brand = "Generic",
            Type = "Fan",
            LedCount = 0,
            Parametric = true,
        };
        yield return new BuiltInMappingSummary
        {
            Key = GenericChainArtifacts.StripKey,
            Name = GenericChainArtifacts.NameFor(GenericChainArtifacts.StripKey),
            Brand = "Generic",
            Type = "Strip",
            LedCount = 0,
            Parametric = true,
        };
    }

    /// <summary>Our own accessories, authored in code because nothing in the generated catalog describes them.</summary>
    private static IEnumerable<BuiltInMappingSummary> FirstPartyRows()
    {
        foreach (var product in HyteChainArtifacts.All)
        {
            yield return new BuiltInMappingSummary
            {
                Key = product.Key,
                Name = product.Name,
                Brand = HyteChainArtifacts.Brand,
                Type = product.Type,
                LedCount = product.LedCount,
            };
        }
    }

    /// <summary>Lower is better; int.MaxValue means "no match".</summary>
    private static int RankMatch(BuiltInMappingSummary row, string query)
    {
        if (row.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (row.Brand.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (row.Brand.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (row.Type.Contains(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return int.MaxValue;
    }

    private static BuiltInMappingSummary Summarize(BuiltInMappingEntry entry)
    {
        var artifact = entry.Artifact;
        var ledCount = 0;
        if (artifact.Zones.Count > 0 && artifact.Zones[0].LedCount is { } n)
            ledCount = n;
        return new BuiltInMappingSummary
        {
            Key = entry.Key,
            Name = artifact.Name,
            Brand = artifact.Device.Match?.VendorHint ?? "",
            Type = entry.Type,
            LedCount = ledCount,
        };
    }

    private static void EnsureLoaded()
    {
        if (_entries is not null)
            return;
        lock (_gate)
        {
            if (_entries is not null)
                return;
            var entries = Load();
            var byKey = new Dictionary<string, BuiltInMappingEntry>(entries.Count, StringComparer.Ordinal);
            foreach (var entry in entries)
                byKey[entry.Key] = entry;
            _byKey = byKey;
            _entries = entries;
        }
    }

    private static List<BuiltInMappingEntry> Load()
    {
        var asm = typeof(BuiltInMappingsCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            Console.Error.WriteLine($"[mappings] built-in catalog resource {ResourceName} missing");
            return new List<BuiltInMappingEntry>();
        }
        try
        {
            var file = JsonSerializer.Deserialize(stream, AppJsonContext.Default.BuiltInMappingsFile);
            return file?.Mappings ?? new List<BuiltInMappingEntry>();
        }
        catch (Exception ex)
        {
            // An unreadable catalog costs the assign picker, nothing else, so
            // degrade to empty rather than taking the lighting page down.
            Console.Error.WriteLine($"[mappings] built-in catalog unreadable: {ex.Message}");
            return new List<BuiltInMappingEntry>();
        }
    }
}
