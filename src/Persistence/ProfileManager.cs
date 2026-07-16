using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Persistence;

/// <summary>Thrown by CreateProfile/RenameProfile/ImportProfile when the target name (trimmed, case-insensitive) collides with a different profile.</summary>
public sealed class ProfileNameConflictException : Exception
{
    public ProfileNameConflictException(string name)
        : base($"A profile named '{name}' already exists.")
    {
    }
}

public sealed class ProfileManager : IDisposable
{

    internal const int MaxProfiles = 5;
    private readonly IConfigStore _store;
    private readonly string _dataDir;
    private readonly object _lock = new();
    private ProfileManifest _manifest = new();
    private bool _dirty;
    private JitteredPeriodicTimer? _flushTimer;

    public event Action? OnProfileSwitched;

    public ProfileManager(IConfigStore store)
    {
        _store = store;
        _dataDir = Path.GetDirectoryName(store.SettingsPath)!;
    }

    public void Initialize()
    {
        lock (_lock)
        {
            var manifestPath = ManifestPath();
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    _manifest = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.ProfileManifest) ?? new ProfileManifest();
                }
                catch
                {
                    _manifest = new ProfileManifest();
                }
            }

            if (_manifest.Profiles.Count == 0)
            {
                var entry = CreateDefaultProfile();
                _manifest.Profiles.Add(entry);
                _manifest.ActiveProfileId = entry.Id;
                SaveManifest();
                SaveProfileFile(entry.Id);
            }
            else if (string.IsNullOrEmpty(_manifest.ActiveProfileId) ||
                     _manifest.Profiles.All(p => p.Id != _manifest.ActiveProfileId))
            {
                _manifest.ActiveProfileId = _manifest.Profiles[0].Id;
                SaveManifest();
            }

            // PrimaryProfileId defaults to the active profile so the
            // day-one default-Shared category (device, per the
            // NexusSettings.SharedCategories field initializer) resolves to a
            // valid source. Repaired on every boot in case the Primary was
            // deleted while the service was off.
            _store.Update(s =>
            {
                if (string.IsNullOrEmpty(s.PrimaryProfileId) ||
                    _manifest.Profiles.All(p => p.Id != s.PrimaryProfileId))
                {
                    s.PrimaryProfileId = _manifest.ActiveProfileId;
                }
                s.SharedCategories ??= new List<string>();
                // Drop unrecognised category ids. Old settings.json files may
                // carry "keeb"/"y70"/"devices", which are workstation-root and
                // invalid in this list.
                s.SharedCategories = s.SharedCategories
                    .Select(c => ProfileSharing.Normalize(c))
                    .Where(c => c != null)
                    .Select(c => c!)
                    .Distinct()
                    .ToList();
            });

            // Pre-device profile files carry default StreamDeck+Keeb blocks
            // (Device predates both); this seeds root's live values into
            // every profile file exactly once so the first switch after
            // upgrade does not wipe them. The marker gates only this initial
            // seed - blocks added to the device category later are not covered.
            var root = _store.Load();
            if (!root.DeviceCategorySeeded)
            {
                foreach (var p in _manifest.Profiles)
                {
                    UpdateProfileFile(p.Id, new[] { ProfileSharing.Device }, root);
                }
                _store.Update(s => s.DeviceCategorySeeded = true);
            }

            // Every settings mutation marks the active profile dirty so the
            // background flush picks it up. Without this, lighting/cooling/
            // panel/etc. routes that call _store.Update never propagate to the
            // per-profile JSON, and a service restart loads the stale profile
            // state and clobbers in-memory mutations the user thought were saved.
            _store.OnChanged += MarkDirty;

            _flushTimer = new JitteredPeriodicTimer(
                periodMs: ProfileFlushIntervalMs,
                jitterMs: ProfileFlushJitterMs,
                FlushIfDirty);
        }
    }

    private const int ProfileFlushIntervalMs = 2000;
    private const int ProfileFlushJitterMs = 250;

    public ProfileManifest GetManifest()
    {
        lock (_lock)
        { return _manifest; }
    }

    public ProfileEntry? GetActiveEntry()
    {
        lock (_lock)
        {
            return _manifest.Profiles.FirstOrDefault(p => p.Id == _manifest.ActiveProfileId);
        }
    }

    public ProfileEntry CreateProfile(string name)
    {
        lock (_lock)
        {
            if (_manifest.Profiles.Count >= MaxProfiles)
            {
                throw new InvalidOperationException("Maximum number of profiles reached.");
            }

            var trimmed = name.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                throw new InvalidOperationException("Name is required.");
            }
            EnsureNameAvailable(trimmed, excludeProfileId: null);

            FlushActiveProfile();

            var id = Guid.NewGuid().ToString("N")[..8];
            var now = DateTimeOffset.UtcNow.ToString("o");
            var entry = new ProfileEntry { Id = id, Name = trimmed, CreatedAt = now, UpdatedAt = now };

            SaveProfileFile(id);

            _manifest.Profiles.Add(entry);
            _manifest.ActiveProfileId = id;
            SaveManifest();
            _dirty = false;

            return entry;
        }
    }

    public void RenameProfile(string profileId, string newName)
    {
        lock (_lock)
        {
            var entry = _manifest.Profiles.FirstOrDefault(p => p.Id == profileId)
                        ?? throw new KeyNotFoundException("Profile not found.");

            var trimmed = newName.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                throw new InvalidOperationException("Name is required.");
            }
            // Excludes the profile's own id, so renaming to its current name
            // (any casing) never conflicts with itself.
            EnsureNameAvailable(trimmed, profileId);

            entry.Name = trimmed;
            entry.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            SaveManifest();
        }
    }

    public void DeleteProfile(string profileId)
    {
        bool switched;
        lock (_lock)
        {
            if (_manifest.Profiles.Count <= 1)
            {
                throw new InvalidOperationException("Cannot delete the last profile.");
            }

            var entry = _manifest.Profiles.FirstOrDefault(p => p.Id == profileId)
                        ?? throw new KeyNotFoundException("Profile not found.");

            // Block deletion of the Primary profile. The user has to mark
            // another profile Primary first. The web side disables the
            // delete button on the Primary; this is the server-side guard.
            var settings = _store.Load();
            if (!string.IsNullOrEmpty(settings.PrimaryProfileId) && settings.PrimaryProfileId == profileId)
            {
                throw new InvalidOperationException("Cannot delete the Primary profile. Mark another profile as Primary first.");
            }

            _manifest.Profiles.Remove(entry);

            var filePath = ProfileFilePath(profileId);
            if (File.Exists(filePath))
            {
                try
                { File.Delete(filePath); }
                catch { }
            }

            switched = _manifest.ActiveProfileId == profileId;
            if (switched)
            {
                _manifest.ActiveProfileId = _manifest.Profiles[0].Id;
                LoadProfileIntoSettings(_manifest.ActiveProfileId);
            }
            SaveManifest();
        }

        // Fire OUTSIDE the lock; the handler does hardware I/O (fan
        // enumeration, lighting engine reapply via LiveEngineSync) that
        // would otherwise block every other ProfileManager call for
        // potentially seconds. Matches SwitchProfile's pattern.
        if (switched)
        {
            OnProfileSwitched?.Invoke();
        }
    }

    public void SwitchProfile(string profileId)
    {
        lock (_lock)
        {
            if (profileId == _manifest.ActiveProfileId)
            {
                return;
            }

            var entry = _manifest.Profiles.FirstOrDefault(p => p.Id == profileId)
                        ?? throw new KeyNotFoundException("Profile not found.");

            FlushActiveProfile();
            LoadProfileIntoSettings(profileId);

            _manifest.ActiveProfileId = profileId;
            entry.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            SaveManifest();
            _dirty = false;
        }

        OnProfileSwitched?.Invoke();
    }

    public void SaveActiveProfile()
    {
        lock (_lock)
        { FlushActiveProfile(); }
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    public NexusSettings? ExportProfile(string profileId)
    {
        lock (_lock)
        {
            var entry = _manifest.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (entry is null)
            {
                return null;
            }

            if (profileId == _manifest.ActiveProfileId)
            {
                var current = CloneSettings(_store.Load());
                current.Auth = null;
                current.PrimaryProfileId = null;
                current.SharedCategories = new List<string>();
                return current;
            }

            var filePath = ProfileFilePath(profileId);
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);
                if (settings != null)
                {
                    settings.Auth = null;
                    settings.PrimaryProfileId = null;
                    settings.SharedCategories = new List<string>();
                }
                return settings;
            }
            catch { return null; }
        }
    }

    public string? ExportProfileJson(string profileId)
    {
        var data = ExportProfile(profileId);
        if (data is null)
        {
            return null;
        }

        string name;
        lock (_lock)
        {
            name = _manifest.Profiles.FirstOrDefault(p => p.Id == profileId)?.Name ?? "Default";
        }

        var wrapper = new ProfileExport { Name = name, Settings = data };
        return JsonSerializer.Serialize(wrapper, PersistenceJsonContext.Default.ProfileExport);
    }

    /// <summary>Same payload ExportProfileJson serializes, returned as an object so callers (CloudProfileSyncService) can hash/transmit it without a round-trip through JSON text.</summary>
    public ProfileExport? ExportProfileForSync(string profileId)
    {
        var data = ExportProfile(profileId);
        if (data is null)
        {
            return null;
        }

        string name;
        lock (_lock)
        {
            name = _manifest.Profiles.FirstOrDefault(p => p.Id == profileId)?.Name ?? "Default";
        }

        return new ProfileExport { Name = name, Settings = data };
    }

    /// <summary>
    /// Create-or-overwrite a profile under a caller-supplied id (the cloud
    /// profileId), instead of generating a new one. Used only by the cloud
    /// profile-sync pull path so a profile pulled from the account keeps the
    /// same id on every machine. Throws when creating would exceed
    /// <see cref="MaxProfiles"/>; overwriting an existing id never counts
    /// against the cap. If the overwritten id is the active profile, the
    /// in-memory settings and engines are re-baselined the same way a profile
    /// switch is, so a stale in-memory copy can't clobber the pulled file on
    /// the next flush. A name colliding with a different local profile is
    /// auto-suffixed ("(2)", "(3)", ...) rather than rejected, since a sync
    /// pull must not fail the sync loop; the suffixed name pushes back on the
    /// profile's next sync.
    /// </summary>
    internal ProfileEntry ImportProfileWithId(string id, string name, NexusSettings data)
    {
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "Imported" : name.Trim();
        bool reactivate;
        ProfileEntry entry;
        lock (_lock)
        {
            data.Auth = null;
            data.PrimaryProfileId = null;
            data.SharedCategories = new List<string>();

            var existing = _manifest.Profiles.FirstOrDefault(p => p.Id == id);
            var now = DateTimeOffset.UtcNow.ToString("o");
            var uniqueName = NextAvailableName(trimmedName, candidate =>
                _manifest.Profiles.Any(p => p.Id != id && string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)));

            if (existing is null)
            {
                if (_manifest.Profiles.Count >= MaxProfiles)
                {
                    throw new InvalidOperationException("Maximum number of profiles reached.");
                }
                entry = new ProfileEntry { Id = id, Name = uniqueName, CreatedAt = now, UpdatedAt = now };
                _manifest.Profiles.Add(entry);
            }
            else
            {
                existing.Name = uniqueName;
                existing.UpdatedAt = now;
                entry = existing;
            }

            var json = JsonSerializer.Serialize(data, PersistenceJsonContext.Default.NexusSettings);
            WriteAtomic(ProfileFilePath(id), json);
            SaveManifest();

            reactivate = id == _manifest.ActiveProfileId;
            if (reactivate)
            {
                LoadProfileIntoSettings(id);
                _dirty = false;
            }
        }

        if (reactivate)
        {
            OnProfileSwitched?.Invoke();
        }

        return entry;
    }

    /// <summary>
    /// Copies profiles.json and every profile-*.json into a fresh timestamped
    /// directory under profiles-archive/, without touching the live library.
    /// Safety net before <see cref="ReplaceLibrary"/> discards the current
    /// library on a cloud account switch. Flushes the active profile first so
    /// the archive reflects the latest in-memory edits.
    /// </summary>
    internal string ArchiveLibrary()
    {
        lock (_lock)
        {
            FlushActiveProfile();

            var archiveDir = Path.Combine(_dataDir, "profiles-archive", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(archiveDir);

            var manifestPath = ManifestPath();
            if (File.Exists(manifestPath))
            {
                File.Copy(manifestPath, Path.Combine(archiveDir, Path.GetFileName(manifestPath)), overwrite: true);
            }
            foreach (var entry in _manifest.Profiles)
            {
                var src = ProfileFilePath(entry.Id);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(archiveDir, Path.GetFileName(src)), overwrite: true);
                }
            }
            return archiveDir;
        }
    }

    /// <summary>
    /// Wholesale library replace: deletes every current profile file and
    /// manifest entry, then writes the given set (ids preserved) as the new
    /// library. Used only for a cloud account switch pulling in the incoming
    /// account's profiles - callers must archive first via
    /// <see cref="ArchiveLibrary"/>. An empty set falls back to a fresh
    /// Default profile so the service never ends up with zero profiles.
    /// PrimaryProfileId/SharedCategories are reset since they may reference
    /// profile ids that no longer exist.
    /// </summary>
    internal void ReplaceLibrary(IReadOnlyList<(string Id, string Name, NexusSettings Data)> profiles)
    {
        lock (_lock)
        {
            foreach (var entry in _manifest.Profiles)
            {
                var path = ProfileFilePath(entry.Id);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch { }
            }

            _manifest = new ProfileManifest();

            if (profiles.Count == 0)
            {
                var def = CreateDefaultProfile();
                _manifest.Profiles.Add(def);
                _manifest.ActiveProfileId = def.Id;
                SaveManifest();
                SaveProfileFile(def.Id);
            }
            else
            {
                var limited = profiles.Take(MaxProfiles).ToList();

                // Dedupe names within the incoming set itself: a cloud
                // library can already contain two rows with the same name.
                // Names are assigned in ascending profileId (ordinal) order
                // so the lower id always keeps the plain name and higher ids
                // get " (2)", " (3)", ... - deterministic regardless of the
                // caller's list order.
                var uniqueNames = new Dictionary<string, string>();
                var takenNames = new List<string>();
                foreach (var p in limited.OrderBy(p => p.Id, StringComparer.Ordinal))
                {
                    var trimmed = string.IsNullOrWhiteSpace(p.Name) ? "Imported" : p.Name.Trim();
                    var unique = NextAvailableName(trimmed, name =>
                        takenNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
                    takenNames.Add(unique);
                    uniqueNames[p.Id] = unique;
                }

                foreach (var (id, _, data) in limited)
                {
                    data.Auth = null;
                    data.PrimaryProfileId = null;
                    data.SharedCategories = new List<string>();
                    var now = DateTimeOffset.UtcNow.ToString("o");
                    _manifest.Profiles.Add(new ProfileEntry { Id = id, Name = uniqueNames[id], CreatedAt = now, UpdatedAt = now });
                    var json = JsonSerializer.Serialize(data, PersistenceJsonContext.Default.NexusSettings);
                    WriteAtomic(ProfileFilePath(id), json);
                }
                _manifest.ActiveProfileId = _manifest.Profiles[0].Id;
                SaveManifest();
                LoadProfileIntoSettings(_manifest.ActiveProfileId);
            }

            _store.Update(s =>
            {
                s.PrimaryProfileId = _manifest.ActiveProfileId;
                s.SharedCategories = new List<string>();
            });
            _dirty = false;
        }

        OnProfileSwitched?.Invoke();
    }

    public ProfileEntry ImportProfile(string name, NexusSettings data, bool replaceExisting = false)
    {
        bool reactivate = false;
        ProfileEntry entry;
        lock (_lock)
        {
            data.Auth = null;
            data.PrimaryProfileId = null;
            data.SharedCategories = new List<string>();

            var trimmed = (name ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                trimmed = "Imported";
            }

            var existing = _manifest.Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            var now = DateTimeOffset.UtcNow.ToString("o");

            if (existing is not null)
            {
                if (!replaceExisting)
                {
                    throw new ProfileNameConflictException(trimmed);
                }

                // Overwrite in place: keep the id, manifest position and
                // CreatedAt so active/Primary references and the reorder
                // list stay valid; adopt the imported name's casing.
                existing.Name = trimmed;
                existing.UpdatedAt = now;
                var replaceJson = JsonSerializer.Serialize(data, PersistenceJsonContext.Default.NexusSettings);
                WriteAtomic(ProfileFilePath(existing.Id), replaceJson);
                SaveManifest();

                // Overwriting the active profile: re-baseline in-memory
                // settings from the imported file so the next flush can't
                // clobber it, then let the caller re-apply the engines.
                reactivate = existing.Id == _manifest.ActiveProfileId;
                if (reactivate)
                {
                    LoadProfileIntoSettings(existing.Id);
                    _dirty = false;
                }
                entry = existing;
            }
            else
            {
                if (_manifest.Profiles.Count >= MaxProfiles)
                {
                    throw new InvalidOperationException("Maximum number of profiles reached.");
                }

                var id = Guid.NewGuid().ToString("N")[..8];
                entry = new ProfileEntry { Id = id, Name = trimmed, CreatedAt = now, UpdatedAt = now };

                var json = JsonSerializer.Serialize(data, PersistenceJsonContext.Default.NexusSettings);
                WriteAtomic(ProfileFilePath(id), json);

                _manifest.Profiles.Add(entry);
                SaveManifest();
            }
        }

        if (reactivate)
        {
            OnProfileSwitched?.Invoke();
        }

        return entry;
    }

    public ProfileEntry ImportProfileJson(string json, bool replaceExisting = false)
    {
        // Imported profile JSON could be v1 shape from an older export.
        var migrated = json;
        var wrapper = JsonSerializer.Deserialize(migrated, PersistenceJsonContext.Default.ProfileExport);
        if (wrapper?.Settings is not null)
        {
            return ImportProfile(wrapper.Name ?? "Imported", wrapper.Settings, replaceExisting);
        }

        var data = JsonSerializer.Deserialize(migrated, PersistenceJsonContext.Default.NexusSettings)
                   ?? throw new InvalidOperationException("Invalid profile data.");
        return ImportProfile("Imported", data, replaceExisting);
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        lock (_lock)
        { FlushActiveProfile(); }
    }

    /// <summary>Throws <see cref="ProfileNameConflictException"/> when trimmedName (case-insensitive) is already used by a profile other than excludeProfileId.</summary>
    private void EnsureNameAvailable(string trimmedName, string? excludeProfileId)
    {
        if (_manifest.Profiles.Any(p => p.Id != excludeProfileId
            && string.Equals(p.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProfileNameConflictException(trimmedName);
        }
    }

    /// <summary>Returns baseName, or the first "baseName (N)" variant (N starting at 2) for which isTaken returns false.</summary>
    private static string NextAvailableName(string baseName, Func<string, bool> isTaken)
    {
        if (!isTaken(baseName))
        {
            return baseName;
        }
        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} ({suffix})";
            suffix++;
        } while (isTaken(candidate));
        return candidate;
    }

    private ProfileEntry CreateDefaultProfile()
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        return new ProfileEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = "Default",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private void FlushActiveProfile()
    {
        if (string.IsNullOrEmpty(_manifest.ActiveProfileId))
        {
            return;
        }

        SaveProfileFile(_manifest.ActiveProfileId);

        // Eager-copy model: every profile's JSON physically holds the same
        // value for shared categories. After flushing the active profile,
        // propagate shared sections to every other profile's JSON so they
        // stay in sync. No routing layer on load - whatever's on disk is
        // the truth for every profile.
        var settings = _store.Load();
        if (settings.SharedCategories.Count > 0)
        {
            foreach (var entry in _manifest.Profiles)
            {
                if (entry.Id == _manifest.ActiveProfileId)
                {
                    continue;
                }
                UpdateProfileFile(entry.Id, settings.SharedCategories, settings);
            }
        }

        _dirty = false;

        var activeEntry = _manifest.Profiles.FirstOrDefault(p => p.Id == _manifest.ActiveProfileId);
        if (activeEntry != null)
        {
            activeEntry.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            SaveManifest();
        }
    }

    private void FlushIfDirty()
    {
        if (!_dirty)
        {
            return;
        }

        lock (_lock)
        { FlushActiveProfile(); }
    }

    private void SaveProfileFile(string profileId)
    {
        // Eager-copy model: every profile's JSON holds the full state. Shared
        // sections happen to be identical across profiles because the flush +
        // propagate step keeps them in sync, but each file is a complete
        // standalone snapshot.
        UpdateProfileFile(profileId, ProfileSharing.All, _store.Load());
    }

    private void LoadProfileIntoSettings(string profileId)
    {
        // Profile JSONs only carry per-profile data (Lighting, Cooling,
        // Device, and the Theme + Dashboard subsets of Ui). Keeb is now
        // profile-scoped via the Device category (ApplyCategory below).
        // Hardware-bound state (Y70, Devices, every Panel* field on Ui, the
        // OS tray/status toggles) lives at NexusSettings root and follows the
        // device, not the active profile - so we do not copy those sections
        // from the profile file. They keep whatever the canonical
        // settings.json already loaded into in-memory.
        var data = ReadProfileFile(profileId);
        if (data == null)
        {
            return;
        }

        _store.Update(s =>
        {
            s.Lighting = data.Lighting ?? new LightingSettings();
            // Pre-v10 profile files (local, imported, or cloud-synced) carry
            // fully materialized template dicts and dense activation states;
            // re-prune both on apply so they don't re-inflate settings.json
            // and shadow future default-look changes.
            if (s.Lighting.Animate is { } animate)
            {
                animate.Templates = Nexus.Service.Lighting.AnimateTemplateDefaults.Prune(animate.Templates);
                Nexus.Service.Lighting.AnimateTemplateDefaults.PruneStates(animate);
            }
            s.Cooling = data.Cooling ?? new CoolingSettings();

            // Theme + Dashboard categories now live in dedicated top-level
            // blocks (Theme, Monitoring, Overlay, Panel.DashboardLayout) - no
            // need to gate on `data.Ui` since that block is now reduced to
            // residual flags. Always copy both categories.
            ProfileSharing.ApplyCategory(s, data, ProfileSharing.Theme);
            ProfileSharing.ApplyCategory(s, data, ProfileSharing.Dashboard);
            ProfileSharing.ApplyCategory(s, data, ProfileSharing.Device);

            // Pre-v11 profiles carry widget types under the legacy
            // marketplace: prefix; rewrite after the category copies so the
            // applied layout keeps resolving (idempotent).
            Nexus.Service.Widgets.AppPrefixMigration.Apply(s);

            // PanelDevices is hardware-scoped, not profile-scoped: do NOT
            // entries that the loaded profile JSON happens to carry into
        });
    }

    /// <summary>Returns the raw deserialized contents of a profile file, or null if missing/corrupt. Used by sharing-aware load and copy paths.</summary>
    private NexusSettings? ReadProfileFile(string profileId)
    {
        var path = ProfileFilePath(profileId);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the on-disk profile file, applies the named categories from <paramref name="source"/> via <see cref="ProfileSharing.ApplyCategory"/>, and writes it back atomically. Used to mirror shared-category writes to the Primary, and to propagate values when sharing is turned off.</summary>
    private void UpdateProfileFile(string profileId, IEnumerable<string> categories, NexusSettings source)
    {
        var path = ProfileFilePath(profileId);
        NexusSettings target;
        if (File.Exists(path))
        {
            try
            {
                var migrated = File.ReadAllText(path);
                target = JsonSerializer.Deserialize(migrated, PersistenceJsonContext.Default.NexusSettings)
                         ?? new NexusSettings();
            }
            catch
            {
                target = new NexusSettings();
            }
        }
        else
        {
            target = new NexusSettings();
        }

        foreach (var cat in categories)
        {
            ProfileSharing.ApplyCategory(target, source, cat);
        }

        // Per-profile JSONs do not carry top-level non-profile fields.
        target.SchemaVersion = 0;
        target.Auth = null;
        target.PrimaryProfileId = null;
        target.SharedCategories = new List<string>();
        target.PanelDevices = new();

        var json = JsonSerializer.Serialize(target, PersistenceJsonContext.Default.NexusSettings);
        WriteAtomic(path, json);
    }


    private NexusSettings CloneSettings(NexusSettings source)
    {
        return new NexusSettings
        {
            SchemaVersion = source.SchemaVersion,
            Auth = source.Auth,
            Lighting = source.Lighting,
            Cooling = source.Cooling,
            StreamDeck = source.StreamDeck,
            Keeb = source.Keeb,
            Y70 = source.Y70,
            Devices = source.Devices,
            Ui = source.Ui,
            Units = source.Units,
            PanelDevices = source.PanelDevices,
            PrimaryProfileId = source.PrimaryProfileId,
            SharedCategories = source.SharedCategories,
        };
    }

    private void SaveManifest()
    {
        var json = JsonSerializer.Serialize(_manifest, PersistenceJsonContext.Default.ProfileManifest);
        WriteAtomic(ManifestPath(), json);
    }

    private static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private string ManifestPath() => Path.Combine(_dataDir, "profiles.json");
    private string ProfileFilePath(string id) => Path.Combine(_dataDir, $"profile-{id}.json");

    // ------------------------------------------------------------------
    // Sharing operations (Primary picker + per-category Shared/Per-profile)
    // ------------------------------------------------------------------

    /// <summary>Set the Primary profile. Under the eager-copy sharing model every profile's JSON already holds the same value for shared categories, so changing the Primary is purely metadata - we don't touch any category data. Primary only matters going forward as the seed-source when a category is next toggled from Per-profile to Shared.</summary>
    public void SetPrimary(string profileId)
    {
        lock (_lock)
        {
            if (_manifest.Profiles.All(p => p.Id != profileId))
            {
                throw new KeyNotFoundException("Profile not found.");
            }
            _store.Update(s => s.PrimaryProfileId = profileId);
        }
        // No engine reapply needed - data is identical across profiles under
        // eager-copy, so the swap is purely the badge moving.
    }

    /// <summary>Toggle a category's Shared/Per-profile state. Returns true when the state actually changed. Throws on unknown category.</summary>
    public bool SetCategoryShared(string category, bool shared)
    {
        lock (_lock)
        {
            var normalized = ProfileSharing.Normalize(category)
                ?? throw new ArgumentException($"Unknown category '{category}'.");

            FlushActiveProfile();

            var current = _store.Load();
            var isShared = current.SharedCategories.Contains(normalized);
            if (isShared == shared)
            {
                return false;
            }

            if (shared)
            {
                // Per-profile -> Shared (eager-copy, destructive). Read the
                // Primary's current value for this category and physically
                // copy it into every other profile's JSON, then update the
                // active profile's in-memory state to match. Each profile's
                // existing per-profile value for this category is overwritten
                // - the user accepted that loss via the confirm dialog on the
                // web side. After this, every profile's stored data is in
                // sync, and edits during shared mode keep them in sync via
                // the propagation step in FlushActiveProfile.
                _store.Update(s =>
                {
                    if (!s.SharedCategories.Contains(normalized))
                    {
                        s.SharedCategories.Add(normalized);
                    }
                    if (string.IsNullOrEmpty(s.PrimaryProfileId) ||
                        _manifest.Profiles.All(p => p.Id != s.PrimaryProfileId))
                    {
                        s.PrimaryProfileId = _manifest.ActiveProfileId;
                    }
                });

                var settings = _store.Load();
                var primaryId = settings.PrimaryProfileId!;
                var primaryData = primaryId == _manifest.ActiveProfileId
                    ? settings
                    : ReadProfileFile(primaryId);

                if (primaryData != null)
                {
                    foreach (var entry in _manifest.Profiles)
                    {
                        if (entry.Id == primaryId)
                        {
                            continue;
                        }
                        UpdateProfileFile(entry.Id, new[] { normalized }, primaryData);
                    }

                    if (_manifest.ActiveProfileId != primaryId)
                    {
                        _store.Update(s =>
                        {
                            ProfileSharing.ApplyCategory(s, primaryData, normalized);
                            // ReadProfileFile bypasses JsonConfigStore.Migrate, so a
                            // pre-v11 primary can carry legacy marketplace: widget
                            // types into live settings; rewrite them (idempotent).
                            Nexus.Service.Widgets.AppPrefixMigration.Apply(s);
                        });
                    }
                }
            }
            else
            {
                // Shared -> Per-profile. Every profile's JSON already holds
                // an identical, current value for this category (kept in
                // sync by the propagation step), so all we do is drop the
                // entry from SharedCategories. From here, each profile is
                // free to diverge by editing.
                _store.Update(s => s.SharedCategories.Remove(normalized));
            }
        }

        // Treat sharing toggles as a partial profile reapply: fans, cooling
        // smoothing and the lighting engine all need to be re-baselined the
        // same way they are on /profiles/{id}/switch, so the user sees the
        // newly-shared (or newly-per-profile) data take effect immediately
        // instead of needing to switch profiles to force a reload.
        OnProfileSwitched?.Invoke();
        return true;
    }

    /// <summary>Reset one category. If shared, the reset is redirected to the Primary's JSON regardless of the named profileId, and the in-memory state is updated so the active profile reflects defaults immediately. If per-profile, the named profile's JSON is reset for that category; if the named profile is the active one, in-memory is also cleared.</summary>
    public void ResetCategory(string profileId, string category)
    {
        lock (_lock)
        {
            if (_manifest.Profiles.All(p => p.Id != profileId))
            {
                throw new KeyNotFoundException("Profile not found.");
            }
            var normalized = ProfileSharing.Normalize(category)
                ?? throw new ArgumentException($"Unknown category '{category}'.");

            FlushActiveProfile();

            var settings = _store.Load();
            var isShared = settings.SharedCategories.Contains(normalized);

            if (isShared)
            {
                // Shared category reset clears the value on every profile.
                // Update in-memory and immediately flush so the propagation
                // step in FlushActiveProfile writes the reset to every
                // profile's JSON. Effectively a global reset for that
                // category.
                _store.Update(s => ProfileSharing.ResetCategory(s, normalized));
                FlushActiveProfile();
            }
            else
            {
                if (profileId == _manifest.ActiveProfileId)
                {
                    _store.Update(s => ProfileSharing.ResetCategory(s, normalized));
                }
                else
                {
                    var data = ReadProfileFile(profileId) ?? new NexusSettings();
                    ProfileSharing.ResetCategory(data, normalized);
                    UpdateProfileFile(profileId, new[] { normalized }, data);
                }
            }
        }
    }

    /// <summary>Reset the per-profile (non-shared) categories on the named profile. Shared categories are identical across every profile under the eager-copy model and are NOT touched by this operation - if the user wants to reset a shared category they use the per-category reset on the Categories list, which clears it everywhere.</summary>
    public void ResetProfile(string profileId)
    {
        lock (_lock)
        {
            if (_manifest.Profiles.All(p => p.Id != profileId))
            {
                throw new KeyNotFoundException("Profile not found.");
            }

            FlushActiveProfile();

            var settings = _store.Load();
            var perProfileCats = ProfileSharing.All
                .Where(c => !settings.SharedCategories.Contains(c))
                .ToList();

            if (perProfileCats.Count == 0)
            {
                return;
            }

            if (profileId == _manifest.ActiveProfileId)
            {
                _store.Update(s =>
                {
                    foreach (var cat in perProfileCats)
                    {
                        ProfileSharing.ResetCategory(s, cat);
                    }
                });
            }
            else
            {
                var data = ReadProfileFile(profileId) ?? new NexusSettings();
                foreach (var cat in perProfileCats)
                {
                    ProfileSharing.ResetCategory(data, cat);
                }
                UpdateProfileFile(profileId, perProfileCats, data);
            }
        }
    }
}
