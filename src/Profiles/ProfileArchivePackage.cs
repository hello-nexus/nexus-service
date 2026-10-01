using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Profiles;

/// <summary>
/// Reader for the legacy .nexusprofile zip: <c>profile.json</c> plus one
/// <c>app-data/&lt;appId&gt;/&lt;key&gt;.json</c> entry per document. Read-only;
/// exports are a single JSON bundle now.
/// </summary>
public static class ProfileArchivePackage
{
    /// <summary>Generous relative to a single document's 256 KiB cap - bounds the whole archive against a hostile zip bomb, not normal use. Enforced against bytes actually read, never a zip entry's declared Length - the central directory's Length is unverified metadata a crafted entry can lie about.</summary>
    public const long MaxArchiveBytes = 64 * 1024 * 1024;

    /// <summary>A profile archive holds one profile plus its app-data; there is no legitimate reason for more than a few thousand entries.</summary>
    public const int MaxEntries = 4096;

    /// <summary>Per-entry read cap for an app-data entry: the document's own data-size limit plus room for the revision/updatedAt envelope.</summary>
    private const long MaxAppDataEntryBytes = AppDataStore.MaxDataBytes + 4096;

    private const string ProfileJsonName = "profile.json";
    private const string AppDataPrefix = "app-data/";

    public sealed class Entry
    {
        public required string AppId { get; init; }
        public required string Key { get; init; }
        public required JsonElement Data { get; init; }
    }

    public sealed class ReadResult
    {
        public bool Ok => Error is null;
        public string? Error { get; init; }
        /// <summary>Raw profile.json text, fed straight into ProfileManager.ImportProfileJson so the existing shader-param sanitisation and legacy-shape handling run unchanged.</summary>
        public string? ProfileJson { get; init; }
        public IReadOnlyList<Entry> AppData { get; init; } = new List<Entry>();

        public static ReadResult Fail(string error) => new() { Error = error };
    }

    public static ReadResult Read(Stream zipStream)
    {
        try
        {
            return ReadCore(zipStream);
        }
        catch (InvalidDataException)
        {
            return ReadResult.Fail("not a valid archive");
        }
        catch (NotSupportedException)
        {
            return ReadResult.Fail("not a valid archive");
        }
    }

    private static ReadResult ReadCore(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaxEntries)
        {
            return ReadResult.Fail("archive has too many entries");
        }

        long totalBytesRead = 0;

        var profileEntry = zip.GetEntry(ProfileJsonName);
        if (profileEntry is null)
        {
            return ReadResult.Fail("archive is missing profile.json");
        }

        string profileJson;
        using (var s = profileEntry.Open())
        {
            var bytes = ReadBounded(s, MaxArchiveBytes - totalBytesRead);
            totalBytesRead += bytes.Length;
            profileJson = Encoding.UTF8.GetString(bytes);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(profileJson, PersistenceJsonContext.Default.ProfileExport);
            if (parsed?.Settings is null)
            {
                return ReadResult.Fail("profile.json is missing settings");
            }
        }
        catch (JsonException)
        {
            return ReadResult.Fail("profile.json is not valid JSON");
        }

        var entries = new List<Entry>();
        var keysPerApp = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var zipEntry in zip.Entries)
        {
            if (!zipEntry.FullName.StartsWith(AppDataPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var rel = zipEntry.FullName[AppDataPrefix.Length..];
            var slash = rel.IndexOf('/');
            if (slash < 0 || !rel.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }
            var appId = rel[..slash];
            var key = rel[(slash + 1)..^".json".Length];
            // Invalid app ids / keys are ignored rather than failing the
            // whole import - an archive built by a future version could
            // carry app-data this version does not recognise.
            if (!AppIds.IsValid(appId) || !AppDataKeys.IsValid(key))
            {
                continue;
            }
            // Same per-app key cap the live PUT route enforces - extra keys
            // for an app beyond it are dropped rather than failing the import.
            var countSoFar = keysPerApp.GetValueOrDefault(appId);
            if (countSoFar >= AppDataStore.MaxKeysPerApp)
            {
                continue;
            }

            byte[] entryBytes;
            using (var entryStream = zipEntry.Open())
            {
                var remaining = Math.Min(MaxAppDataEntryBytes, MaxArchiveBytes - totalBytesRead);
                entryBytes = ReadBounded(entryStream, remaining);
            }
            totalBytesRead += entryBytes.Length;

            AppDataDocumentDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize(entryBytes, AppJsonContext.Default.AppDataDocumentDto);
            }
            catch (JsonException)
            {
                continue;
            }
            if (dto?.Data is null)
            {
                continue;
            }
            var dataBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(dto.Data.Value, PersistenceJsonContext.Default.JsonElement));
            if (dataBytes > AppDataStore.MaxDataBytes)
            {
                continue;
            }

            entries.Add(new Entry { AppId = appId, Key = key, Data = dto.Data.Value });
            keysPerApp[appId] = countSoFar + 1;
        }

        return new ReadResult { ProfileJson = profileJson, AppData = entries };
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes from <paramref name="source"/>, throwing InvalidDataException instead of reading further once it has more - a zip entry's declared Length is unverified central-directory metadata a crafted entry can lie about, so the actual byte count read is what gets checked against every size cap in this reader.</summary>
    private static byte[] ReadBounded(Stream source, long limit)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - total + 1))) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("entry exceeds its allowed size");
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
