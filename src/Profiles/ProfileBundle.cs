using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Profiles;

/// <summary>
/// Reads the three shapes a profile file arrives in (JSON bundle, legacy bare
/// JSON, legacy zip) into one parsed form, and sanitises a bundle's app data
/// whether it came from a file or the cloud. Applying a bundle is
/// <see cref="AppDataStore.ReplaceApps"/> on the profile the settings landed in.
/// </summary>
public static class ProfileBundle
{
    public const int MaxApps = 256;

    public sealed class Parsed
    {
        public bool Ok => Error is null;
        public string? Error { get; init; }
        public string Name { get; init; } = "";
        public string ProfileJson { get; init; } = "";
        public Dictionary<string, Dictionary<string, JsonElement>> AppData { get; init; } = new(StringComparer.Ordinal);

        /// <summary>"json" or "archive".</summary>
        public string Format { get; init; } = "json";

        public static Parsed Fail(string error) => new() { Error = error };
    }

    public static bool LooksLikeZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    public static Parsed Parse(byte[] bytes)
    {
        if (LooksLikeZip(bytes))
        {
            using var ms = new MemoryStream(bytes);
            var archive = ProfileArchivePackage.Read(ms);
            if (!archive.Ok)
            {
                return Parsed.Fail(archive.Error!);
            }
            var fromArchive = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
            foreach (var entry in archive.AppData)
            {
                if (!fromArchive.TryGetValue(entry.AppId, out var docs))
                {
                    docs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    fromArchive[entry.AppId] = docs;
                }
                docs[entry.Key] = entry.Data;
            }
            var archiveExport = JsonSerializer.Deserialize(archive.ProfileJson!, PersistenceJsonContext.Default.ProfileExport);
            return new Parsed
            {
                Name = archiveExport?.Name ?? "Imported",
                ProfileJson = archive.ProfileJson!,
                AppData = fromArchive,
                Format = "archive",
            };
        }

        var json = Encoding.UTF8.GetString(bytes);
        try
        {
            var export = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.ProfileExport);
            if (export?.Settings is not null)
            {
                return new Parsed { Name = export.Name ?? "Imported", ProfileJson = json, AppData = Sanitize(export.AppData) };
            }
            var bare = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);
            return bare is null
                ? Parsed.Fail("Invalid profile data.")
                : new Parsed { Name = "Imported", ProfileJson = json };
        }
        catch (JsonException)
        {
            return Parsed.Fail("Invalid profile JSON.");
        }
    }

    /// <summary>Keeps only apps/keys that pass id validation, documents within the size cap, and at most the per-app key cap; an untrusted bundle never reaches the store unfiltered.</summary>
    public static Dictionary<string, Dictionary<string, JsonElement>> Sanitize(IReadOnlyDictionary<string, Dictionary<string, JsonElement>>? raw)
    {
        var result = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        if (raw is null)
        {
            return result;
        }
        foreach (var (appId, docs) in raw)
        {
            if (result.Count >= MaxApps)
            {
                break;
            }
            if (!AppIds.IsValid(appId))
            {
                continue;
            }
            var kept = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, data) in docs ?? new Dictionary<string, JsonElement>())
            {
                if (kept.Count >= AppDataStore.MaxKeysPerApp)
                {
                    break;
                }
                if (!AppDataKeys.IsValid(key) || !AppDataStore.IsStorable(data))
                {
                    continue;
                }
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(data, PersistenceJsonContext.Default.JsonElement)) > AppDataStore.MaxDataBytes)
                {
                    continue;
                }
                kept[key] = data;
            }
            if (kept.Count == 0 && docs?.Count > 0)
            {
                continue;
            }
            result[appId] = kept;
        }
        return result;
    }

    /// <summary>The app ids a bundle would write, for the inspect response and the cloud library listing.</summary>
    public static List<string> AppIdsOf(Dictionary<string, Dictionary<string, JsonElement>> appData)
    {
        var ids = new List<string>();
        foreach (var (appId, docs) in appData)
        {
            if (docs.Count > 0)
            {
                ids.Add(appId);
            }
        }
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }
}
