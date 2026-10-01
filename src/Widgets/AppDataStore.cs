using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets;

/// <summary>
/// Generic per-(profile,app,key) persistent JSON document store backing
/// <c>/apps-api/data/{appId}/{key}</c>. One file per document at
/// <c>&lt;NexusRoot&gt;/app-data/profiles/&lt;profileId&gt;/&lt;appId&gt;/&lt;key&gt;.json</c>,
/// separate from the app's install directory (install/update/uninstall wipes
/// that; this tree survives all three) and from the profile file itself.
/// Every document belongs to exactly one profile; the route layer acts on the
/// active one. Writes are compare-and-swap on <see cref="AppDataFile.Revision"/>.
/// Every public method validates its profileId/appId/key regardless of whether
/// the caller already did - callers that read an id straight off a file or the
/// cloud must never be able to walk this store's paths, and this is the one
/// place that can guarantee it. A single lock per (profile,app) covers every
/// mutation to that app's documents, so the per-app key-count cap can never be
/// raced past by two concurrent writes to two different new keys.
/// </summary>
public sealed class AppDataStore
{
    public const int MaxDataBytes = 256 * 1024;
    public const int MaxKeysPerApp = 16;

    /// <summary>Per-profile cap on the serialized appData bundle a cloud backup carries; the api enforces the same number.</summary>
    public const int MaxBundleBytes = 384 * 1024;

    private const string ProfilesFolder = "profiles";

    private readonly ConcurrentDictionary<string, object> _appLocks = new(StringComparer.Ordinal);
    private readonly Func<string> _rootProvider;
    private readonly Func<string> _activeProfileProvider;

    /// <summary>Fires (profileId, appId, key) after a mutation actually changes what a reader of that document would see - a successful Put, Import, Delete, or a bundle apply.</summary>
    public event Action<string, string, string>? DocumentChanged;

    /// <summary>Fires (profileId) when every running app instance must reload its data: the active profile changed, or a restore/import wrote into the active profile.</summary>
    public event Action<string>? ResetRequested;

    public AppDataStore(Func<string> activeProfileProvider)
        : this(() => Path.Combine(NexusDataPaths.NexusRoot(), "app-data"), activeProfileProvider)
    {
    }

    /// <summary>Test seam: an explicit root instead of the machine's real data directory.</summary>
    internal AppDataStore(Func<string> rootProvider, Func<string> activeProfileProvider)
    {
        _rootProvider = rootProvider;
        _activeProfileProvider = activeProfileProvider;
    }

    public string ActiveProfileId => _activeProfileProvider();

    private string RootDir => _rootProvider();
    private string ProfilesDir => Path.Combine(RootDir, ProfilesFolder);
    private string ProfileDir(string profileId) => Path.Combine(ProfilesDir, profileId);
    private string AppDir(string profileId, string appId) => Path.Combine(ProfileDir(profileId), appId);
    private string FilePath(string profileId, string appId, string key) => Path.Combine(AppDir(profileId, appId), key + ".json");

    private object AppLock(string profileId, string appId) => _appLocks.GetOrAdd(profileId + "/" + appId, static _ => new object());

    /// <summary>A profile id becomes a folder name. Local ids are short hex; a cloud row can carry any string, so only plain id characters pass.</summary>
    public static bool IsValidProfileId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            return false;
        }
        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                return false;
            }
        }
        return true;
    }

    private static void EnsureValidProfile(string profileId)
    {
        if (!IsValidProfileId(profileId))
        {
            throw new ArgumentException("invalid profile id", nameof(profileId));
        }
    }

    private static void EnsureValid(string profileId, string appId, string key)
    {
        EnsureValidProfile(profileId);
        if (!AppIds.IsValid(appId))
        {
            throw new ArgumentException("invalid app id", nameof(appId));
        }
        if (!AppDataKeys.IsValid(key))
        {
            throw new ArgumentException("invalid key", nameof(key));
        }
    }

    public enum PutOutcome { Ok, Conflict, TooLarge, TooManyKeys }

    public sealed class PutResult
    {
        public required PutOutcome Outcome { get; init; }
        public int Revision { get; init; }
        public string UpdatedAt { get; init; } = "";
        public JsonElement? Data { get; init; }
    }

    /// <summary>Reads the raw persisted file, or null when no document has ever been written for this profile/app/key or the file is unreadable.</summary>
    public AppDataFile? TryRead(string profileId, string appId, string key)
    {
        EnsureValid(profileId, appId, key);
        return TryReadUnlocked(FilePath(profileId, appId, key));
    }

    /// <summary>Wire-shape read: revision 0 / null data when absent.</summary>
    public (int Revision, string UpdatedAt, JsonElement? Data) Get(string profileId, string appId, string key)
    {
        var doc = TryRead(profileId, appId, key);
        return doc is null ? (0, "", null) : (doc.Revision, doc.UpdatedAt, doc.Data);
    }

    public PutResult Put(string profileId, string appId, string key, int baseRevision, JsonElement data)
    {
        EnsureValid(profileId, appId, key);
        var serializedLength = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(data, PersistenceJsonContext.Default.JsonElement));
        if (serializedLength > MaxDataBytes)
        {
            return new PutResult { Outcome = PutOutcome.TooLarge };
        }

        lock (AppLock(profileId, appId))
        {
            var appDir = AppDir(profileId, appId);
            var path = FilePath(profileId, appId, key);
            var isNewKey = !File.Exists(path);
            var existing = TryReadUnlocked(path);

            if (isNewKey && CountKeys(appDir) >= MaxKeysPerApp)
            {
                return new PutResult { Outcome = PutOutcome.TooManyKeys };
            }

            var currentRevision = existing?.Revision ?? 0;
            if (baseRevision != currentRevision)
            {
                return new PutResult
                {
                    Outcome = PutOutcome.Conflict,
                    Revision = currentRevision,
                    UpdatedAt = existing?.UpdatedAt ?? "",
                    Data = existing?.Data,
                };
            }

            var now = DateTimeOffset.UtcNow.ToString("o");
            var file = new AppDataFile
            {
                Revision = currentRevision + 1,
                UpdatedAt = now,
                Data = data,
            };
            WriteFile(appDir, path, file);
            DocumentChanged?.Invoke(profileId, appId, key);
            return new PutResult { Outcome = PutOutcome.Ok, Revision = file.Revision, UpdatedAt = now, Data = data };
        }
    }

    /// <summary>
    /// Unconditional write for a restore/import: a revision strictly above
    /// whatever is currently stored, so a running instance's stale-revision
    /// guard still lets the restored document through.
    /// </summary>
    public AppDataFile Import(string profileId, string appId, string key, JsonElement data)
    {
        EnsureValid(profileId, appId, key);
        lock (AppLock(profileId, appId))
        {
            return ImportUnlocked(profileId, appId, key, data);
        }
    }

    private AppDataFile ImportUnlocked(string profileId, string appId, string key, JsonElement data)
    {
        var path = FilePath(profileId, appId, key);
        var existing = TryReadUnlocked(path);
        var file = new AppDataFile
        {
            Revision = (existing?.Revision ?? 0) + 1,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o"),
            Data = data,
        };
        WriteFile(AppDir(profileId, appId), path, file);
        DocumentChanged?.Invoke(profileId, appId, key);
        return file;
    }

    public void Delete(string profileId, string appId, string key)
    {
        EnsureValid(profileId, appId, key);
        lock (AppLock(profileId, appId))
        {
            var path = FilePath(profileId, appId, key);
            var existed = File.Exists(path);
            try { File.Delete(path); } catch { /* already gone */ }
            if (existed)
            {
                DocumentChanged?.Invoke(profileId, appId, key);
            }
        }
    }

    /// <summary>Deletes everything the profile owns. Called when the profile itself is deleted.</summary>
    public void DeleteProfile(string profileId)
    {
        EnsureValidProfile(profileId);
        var dir = ProfileDir(profileId);
        if (!Directory.Exists(dir))
        {
            return;
        }
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Moves every profile folder not in <paramref name="keepProfileIds"/> to
    /// <c>app-data/.archive/&lt;stamp&gt;/&lt;profileId&gt;</c>. For a library
    /// replace (cloud account switch), where profiles disappear without a
    /// user-chosen delete.
    /// </summary>
    public void ArchiveProfilesExcept(IReadOnlyCollection<string> keepProfileIds)
    {
        if (!Directory.Exists(ProfilesDir))
        {
            return;
        }
        var keep = new HashSet<string>(keepProfileIds, StringComparer.Ordinal);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        foreach (var dir in Directory.EnumerateDirectories(ProfilesDir).ToList())
        {
            var id = Path.GetFileName(dir);
            if (keep.Contains(id))
            {
                continue;
            }
            try
            {
                var target = Path.Combine(RootDir, ".archive", stamp, id);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(dir, target);
            }
            catch (IOException)
            {
                // Left in place; an orphan folder is harmless.
            }
        }
    }

    /// <summary>Every app id that has at least one document under the profile, sorted.</summary>
    public List<string> AppIdsFor(string profileId)
    {
        EnsureValidProfile(profileId);
        var dir = ProfileDir(profileId);
        var result = new List<string>();
        if (!Directory.Exists(dir))
        {
            return result;
        }
        foreach (var appDir in Directory.EnumerateDirectories(dir))
        {
            var appId = Path.GetFileName(appDir);
            if (AppIds.IsValid(appId) && Directory.EnumerateFiles(appDir, "*.json").Any(f => AppDataKeys.IsValid(Path.GetFileNameWithoutExtension(f))))
            {
                result.Add(appId);
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// The profile's documents as the shared bundle shape
    /// <c>{ appId: { key: data } }</c>, with revision/updatedAt stripped. Apps
    /// and keys are inserted in ordinal order so a serialization of the result
    /// is stable (the cloud backup hashes it).
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>> ReadProfile(string profileId)
    {
        var result = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var appId in AppIdsFor(profileId))
        {
            var docs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var keys = Directory.EnumerateFiles(AppDir(profileId, appId), "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(k => AppDataKeys.IsValid(k))
                .OrderBy(k => k, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var doc = TryReadUnlocked(FilePath(profileId, appId, key!));
                if (doc is not null)
                {
                    docs[key!] = doc.Data;
                }
            }
            if (docs.Count > 0)
            {
                result[appId] = docs;
            }
        }
        return result;
    }

    /// <summary>
    /// The one apply path for a bundle that arrives from a file or the cloud:
    /// for each app present in <paramref name="bundle"/>, the profile's
    /// documents for that app are replaced (keys absent from the bundle are
    /// deleted); apps not in the bundle are untouched. Invalid app ids / keys,
    /// oversized documents and keys past the per-app cap are dropped rather
    /// than failing the restore. Fires <see cref="ResetRequested"/> when the
    /// target is the active profile and the bundle named at least one app.
    /// </summary>
    public bool ReplaceApps(string profileId, IReadOnlyDictionary<string, Dictionary<string, JsonElement>>? bundle)
    {
        EnsureValidProfile(profileId);
        if (bundle is null)
        {
            return false;
        }
        var touched = false;
        foreach (var (appId, docs) in bundle)
        {
            if (!AppIds.IsValid(appId))
            {
                continue;
            }
            touched = true;
            var accepted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, data) in docs ?? new Dictionary<string, JsonElement>())
            {
                if (accepted.Count >= MaxKeysPerApp)
                {
                    break;
                }
                if (!AppDataKeys.IsValid(key) || data.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(data, PersistenceJsonContext.Default.JsonElement)) > MaxDataBytes)
                {
                    continue;
                }
                accepted[key] = data;
            }

            lock (AppLock(profileId, appId))
            {
                var appDir = AppDir(profileId, appId);
                if (Directory.Exists(appDir))
                {
                    foreach (var file in Directory.EnumerateFiles(appDir, "*.json").ToList())
                    {
                        var key = Path.GetFileNameWithoutExtension(file);
                        if (accepted.ContainsKey(key))
                        {
                            continue;
                        }
                        try { File.Delete(file); } catch { /* best effort */ }
                        DocumentChanged?.Invoke(profileId, appId, key);
                    }
                }
                foreach (var (key, data) in accepted)
                {
                    ImportUnlocked(profileId, appId, key, data);
                }
            }
        }

        if (touched && string.Equals(profileId, ActiveProfileId, StringComparison.Ordinal))
        {
            ResetRequested?.Invoke(profileId);
        }
        return touched;
    }

    /// <summary>Tells every running app instance to reload; the active profile changed under them.</summary>
    public void NotifyReset(string profileId) => ResetRequested?.Invoke(profileId);

    /// <summary>
    /// One-time startup migration from the account-wide layout
    /// (<c>app-data/&lt;appId&gt;/</c>) to the active profile's folder. An app
    /// that already exists there is skipped and its legacy folder is left in
    /// place. Idempotent: once moved, nothing is left at the legacy path.
    /// </summary>
    public void MigrateLegacyLayout()
    {
        var activeId = ActiveProfileId;
        if (!IsValidProfileId(activeId) || !Directory.Exists(RootDir))
        {
            return;
        }
        foreach (var legacy in Directory.EnumerateDirectories(RootDir).ToList())
        {
            var appId = Path.GetFileName(legacy);
            if (!AppIds.IsValid(appId))
            {
                continue;
            }
            var target = AppDir(activeId, appId);
            if (Directory.Exists(target))
            {
                continue;
            }
            try
            {
                Directory.CreateDirectory(ProfileDir(activeId));
                Directory.Move(legacy, target);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[app-data] could not migrate {appId}: {ex.Message}");
            }
        }
    }

    private static int CountKeys(string appDir)
    {
        if (!Directory.Exists(appDir))
        {
            return 0;
        }
        return Directory.EnumerateFiles(appDir, "*.json").Count();
    }

    private static AppDataFile? TryReadUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.AppDataFile);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteFile(string appDir, string path, AppDataFile file)
    {
        Directory.CreateDirectory(appDir);
        var json = JsonSerializer.Serialize(file, PersistenceJsonContext.Default.AppDataFile);
        // Restrict the temp file BEFORE content lands in it, same ordering as
        // JsonConfigStore.WriteAtomic - a crash-stranded temp file must never
        // keep whatever the umask gave it.
        NexusDataPaths.CreateRestricted(AtomicJsonFile.TempPathFor(path));
        AtomicJsonFile.Write(path, json);
    }
}
