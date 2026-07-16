using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Serialization;

namespace Nexus.Service.Persistence;

/// <summary>
/// File-backed NexusSettings store.
/// Path: ~/Library/Application Support/Nexus/settings.json on macOS,
///       %LOCALAPPDATA%/Nexus/settings.json on Windows,
///       $XDG_CONFIG_HOME/Nexus/settings.json (or ~/.config/Nexus) on Linux.
///
/// Concurrency: a single global lock around load/save. Updates mutate the in-memory
/// doc synchronously, but the disk write is coalesced to a short debounce window so
/// lighting slider drags (10-20 Hz) don't serialize and fsync the whole settings
/// JSON on every frame. Dispose() flushes any pending write.
///
/// Atomic write: serialize -> write to settings.json.tmp -> File.Replace into place.
/// If the destination doesn't exist yet (first run), File.Move handles that path.
/// </summary>
public sealed class JsonConfigStore : IConfigStore, IDisposable
{
    private const int FlushDebounceMs = 300;

    private readonly object _lock = new();
    private NexusSettings? _cached;
    private Timer? _flushTimer;
    private bool _dirty;
    private bool _disposed;

    public JsonConfigStore()
    {
        SettingsPath = ResolveSettingsPath();
    }

    // Test-only ctor: lets unit tests point the store at a throwaway path
    // instead of clobbering the user's real settings.json. Prod wiring uses
    // the parameterless ctor registered in DI.
    internal JsonConfigStore(string settingsPath)
    {
        SettingsPath = settingsPath;
    }

    public string SettingsPath { get; }

    public NexusSettings Load()
    {
        lock (_lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (!File.Exists(SettingsPath))
            {
                _cached = new NexusSettings();
                Persist(_cached);
                return _cached;
            }

            try
            {
                var json = File.ReadAllText(SettingsPath);
                _cached = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);
                // Valid JSON (e.g. a literal "null") but no data: the file
                // still existed, so this is not a fresh install for the v8
                // OnboardingCompleted migration below.
                _cached ??= new NexusSettings { SchemaVersion = 0 };
                if (_cached.SchemaVersion < NexusSettings.CurrentSchemaVersion)
                {
                    Migrate(_cached);
                    Persist(_cached);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[nexus-service] settings.json corrupted, starting fresh: {ex.Message}");
                // Preserve the unreadable file before overwriting it with
                // defaults, so a single bad byte doesn't silently destroy the
                // user's config with no recovery copy.
                try
                {
                    var backup = SettingsPath + ".corrupt";
                    File.Copy(SettingsPath, backup, overwrite: true);
                    Console.Error.WriteLine($"[nexus-service] preserved corrupt settings at {backup}");
                }
                catch { /* best-effort backup; never block startup on it */ }
                // The file existed but couldn't be read: not a fresh install
                // for the v8 OnboardingCompleted migration, so route through
                // Migrate() the same as any other pre-v8 document.
                _cached = new NexusSettings { SchemaVersion = 0 };
                Migrate(_cached);
                Persist(_cached);
            }

            return _cached;
        }
    }

    /// <summary>
    /// In-place schema migrations, applied once at load and persisted
    /// immediately. v6: LED map overrides / aspect ratios move from per-card
    /// keys into the device-scoped segment-local dicts (zones model).
    /// v7: legacy autoUpdateDisabled bool mapped to UpdateMode string.
    /// v8: any settings.json that already existed predates the first-run
    /// welcome screen, so it is marked already-onboarded; only an install
    /// with no settings.json at all (Load's !File.Exists branch, which never
    /// calls Migrate) sees OnboardingCompleted default to false.
    /// v9: animate templates shrink to user deltas - slots equal to the
    /// canonical defaults (previously materialized in full by the web client)
    /// are pruned; readers resolve missing slots via AnimateTemplateDefaults.
    /// v10: animate activation states shrink to deltas from the resolved
    /// selected-slot look; absent entries resolve through the templates.
    /// v12: the "device" sharing category (Stream Deck bindings) is added to
    /// SharedCategories so an upgrading install keeps today's
    /// workstation-global behavior instead of defaulting to per-profile.
    /// </summary>
    private static void Migrate(NexusSettings doc)
    {
        if (doc.SchemaVersion < 6)
        {
            Nexus.Service.Lighting.Zones.LegacyLedOverrideMigration.Apply(doc);
        }
        if (doc.SchemaVersion < 7)
        {
            if (doc.Update.LegacyAutoUpdateDisabled == true)
            {
                doc.Update.UpdateMode = "notify";
            }
            doc.Update.LegacyAutoUpdateDisabled = null;
        }
        if (doc.SchemaVersion < 8)
        {
            doc.OnboardingCompleted = true;
        }
        // Explicit JSON nulls can leave Lighting/Animate null despite the
        // non-nullable initializers; a throw here would send Load down the
        // corrupt-file path and reset every user setting.
        if (doc.SchemaVersion < 9 && doc.Lighting?.Animate is { } animate)
        {
            animate.Templates = Nexus.Service.Lighting.AnimateTemplateDefaults.Prune(animate.Templates);
        }
        // v10: activation states shrink to deltas - an entry equal to the
        // effect's resolved selected-slot look is redundant (StartAnimate no
        // longer writes those; readers resolve absent entries the same way).
        // Runs after v9 so resolution sees the pruned sparse templates.
        if (doc.SchemaVersion < 10 && doc.Lighting?.Animate is { } a10)
        {
            Nexus.Service.Lighting.AnimateTemplateDefaults.PruneStates(a10);
        }
        // v11: the app-placement prefix renamed marketplace: -> app: with no
        // runtime alias; rewrite every persisted widget type so existing
        // placements keep resolving.
        if (doc.SchemaVersion < 11)
        {
            Nexus.Service.Widgets.AppPrefixMigration.Apply(doc);
        }
        if (doc.SchemaVersion < 12)
        {
            doc.SharedCategories ??= new List<string>();
            if (!doc.SharedCategories.Contains(ProfileSharing.Device))
            {
                doc.SharedCategories.Add(ProfileSharing.Device);
            }
        }
        doc.SchemaVersion = NexusSettings.CurrentSchemaVersion;
    }

    public event Action? OnChanged;

    public void Update(Action<NexusSettings> mutator)
    {
        lock (_lock)
        {
            var doc = _cached ?? Load();
            mutator(doc);
            _dirty = true;
            ScheduleFlushLocked();
        }
        try
        { OnChanged?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine($"[config-store] OnChanged handler threw: {ex.Message}"); }
    }

    public void Reload()
    {
        lock (_lock)
        { _cached = null; _dirty = false; }
    }

    /// <summary>Force any pending debounced write to run now. Safe to call from any thread.</summary>
    public void FlushNow() => FlushPending();

    private void ScheduleFlushLocked()
    {
        if (_disposed)
        {
            return;
        }
        _flushTimer ??= new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
        _flushTimer.Change(FlushDebounceMs, Timeout.Infinite);
    }

    private void FlushPending()
    {
        // Serialize under the lock (produces a string - fast) so no other Update
        // can mutate the doc mid-serialization. Do the disk write outside the
        // lock so sliders aren't blocked on fsync.
        string? json;
        lock (_lock)
        {
            if (!_dirty || _cached is null)
            {
                return;
            }
            json = JsonSerializer.Serialize(_cached, PersistenceJsonContext.Default.NexusSettings);
            _dirty = false;
        }

        try
        { WriteAtomic(json); }
        catch (Exception ex) { Console.Error.WriteLine($"[config-store] flush failed: {ex.Message}"); }
    }

    private void Persist(NexusSettings doc)
    {
        // Used only for the first-run synchronous write (Load sees no settings
        // file and writes defaults immediately). Runtime updates go through the
        // debounced flush path.
        var json = JsonSerializer.Serialize(doc, PersistenceJsonContext.Default.NexusSettings);
        WriteAtomic(json);
    }

    private void WriteAtomic(string json)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        AtomicJsonFile.Write(SettingsPath, json);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        // Ensure the last in-memory update hits disk on shutdown.
        FlushPending();
    }

    /// <summary>Per-OS Nexus data directory (the settings.json parent). Shared by auxiliary stores like the mapping registry disk cache.</summary>
    public static string ResolveDataDirectory()
        => Path.GetDirectoryName(ResolveSettingsPath())!;

    private static string ResolveSettingsPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Nexus", "settings.json");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Machine-scope: settings belong to the LocalSystem service, not the
            // logged-in user. CommonApplicationData = %ProgramData%.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Nexus", "settings.json");
        }

        // Linux / others
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Nexus", "settings.json");
    }

}
