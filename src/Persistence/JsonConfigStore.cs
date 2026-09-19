using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Serialization;

namespace Nexus.Service.Persistence;

/// <summary>
/// File-backed NexusSettings store. Path is
/// <see cref="NexusDataPaths.NexusRoot"/>/settings.json, which resolves the
/// per-OS default or the NEXUS_DATA_ROOT override.
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
                // Fresh install only: anonymous telemetry defaults on here
                // (opt-out model). Every other path (existing file, migrated
                // file, corrupt-file fallback) keeps the field's own default
                // of false until the user explicitly opts in.
                _cached = new NexusSettings();
                _cached.Telemetry.CollectAnonymousData = true;
                // Written in the free-rotation layout convention from the start;
                // only a document from before it needs the v17 unswap.
                _cached.Lighting.FreeRotationLayouts = true;
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
                else if (NeedsDeckModesRecovery(_cached))
                {
                    // A downgrade to a pre-deck-modes build (then a re-upgrade)
                    // leaves SchemaVersion already at 18 - the schema-gated
                    // migration above never runs again - while the old build's
                    // own writes repopulated PhysicalDeckSettings.Legacy* with
                    // nothing hoisted into Presets/Instances. Recover
                    // independently of SchemaVersion whenever that exact
                    // stranded shape shows up.
                    Nexus.Service.Deck.DeckModesMigration.Apply(_cached);
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
    /// v13: any pre-existing settings.json predates the lighting
    /// device-selection onboarding, so it is marked already-complete; only a
    /// fresh install (no settings.json) sees LightingOnboardingCompleted
    /// default to false. Same shape as v8.
    /// v15: any pre-existing settings.json predates the dashboard density
    /// mode, so it is pinned to "advanced"; only a fresh install keeps the
    /// "simple" default. Same shape as v8/v13.
    /// v16: any pre-existing settings.json predates the feature-pillars
    /// onboarding screen, so it is marked already-complete; only a fresh
    /// install sees the screen. Same shape as v8/v13/v15.
    /// v17: quarter-turned lighting layouts stored their turned footprint;
    /// they now store the unturned frame, so 90/270 records swap sides back.
    /// Profiles carry their own lighting document and migrate in
    /// ProfileManager.LoadProfileIntoSettings as they are applied.
    /// v18: per-serial Stream Deck presets/live config hoist into the
    /// host-wide StreamDeckSettings.Presets/Instances, and every deck
    /// widget's inline layout config does the same (DeckModesMigration).
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
        if (doc.SchemaVersion < 13)
        {
            doc.LightingOnboardingCompleted = true;
        }
        if (doc.SchemaVersion < 15)
        {
            // An explicit JSON null survives the non-nullable initializer, and
            // every upgrading install takes this arm - an NRE here is caught by
            // Load() as a corrupt file and replaces the whole settings.json.
            doc.Ui ??= new UiSettings();
            doc.Ui.LightingDashboardMode = "advanced";
            doc.Ui.CoolingDashboardMode = "advanced";
        }
        if (doc.SchemaVersion < 16)
        {
            doc.FeaturesOnboardingCompleted = true;
            // Predates Ui.AutoKillConflictsAtStartup (shipped at v16), whose
            // default is now on: an upgrade keeps the off it ran with, and the
            // onboarding flags set above would otherwise let the first start
            // sweep vendor apps the user never saw listed.
            doc.Ui.AutoKillConflictsAtStartup = false;
        }
        if (doc.SchemaVersion < 17)
        {
            Nexus.Service.Lighting.LayoutRotationMigration.Apply(doc.Lighting);
        }
        if (doc.SchemaVersion < 18)
        {
            Nexus.Service.Deck.DeckModesMigration.Apply(doc);
        }
        doc.SchemaVersion = NexusSettings.CurrentSchemaVersion;
    }

    /// <summary>True when every deck preset/instance is empty (nothing hoisted yet) while at least one deck still carries pre-v18 Legacy* content - the shape a downgrade-then-re-upgrade round trip leaves behind with SchemaVersion already at 18.</summary>
    private static bool NeedsDeckModesRecovery(NexusSettings doc)
    {
        if (doc.StreamDeck.Presets.Count > 0 || doc.StreamDeck.Instances.Count > 0)
        {
            return false;
        }
        foreach (var deck in doc.StreamDeck.Decks.Values)
        {
            if (deck.LegacyDeck is not null || deck.LegacyImageRefs is not null
                || deck.LegacyPresets is not null || deck.LegacyActivePresetId is not null)
            {
                return true;
            }
        }
        return false;
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

    // Delegates the root to NexusDataPaths so NEXUS_DATA_ROOT is read in one
    // place; an active override lands settings.json directly under it, with
    // no ProgramData/legacy-path branching (that logic only applies to a real
    // per-OS install, which an override is standing in for).
    private static string ResolveSettingsPath()
        => Path.Combine(NexusDataPaths.NexusRoot(), "settings.json");

    /// <summary>Test seam: composes the settings path from an explicit root
    /// instead of reading the environment.</summary>
    internal static string ResolveSettingsPath(string? overrideRoot)
        => Path.Combine(NexusDataPaths.ResolveRoot(overrideRoot), "settings.json");
}
