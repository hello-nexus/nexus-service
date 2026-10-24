using System;
using System.IO;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting.Scene;

/// <summary>
/// <c>pc-setup.json</c>, machine scope like the lighting scene. A missing or
/// corrupt file reads as the scene migrated by <see cref="PcSetupMigration"/>
/// at revision 0 (or an empty record when the scene has no objects); nothing is
/// written until a client saves, so reading never changes either file.
/// </summary>
public sealed class PcSetupStore
{
    private readonly string _path;
    private readonly Func<LightingSceneDoc> _legacyScene;
    private readonly object _lock = new();
    private PcSetupFile? _saved;
    private bool _read;

    public PcSetupStore(string dir, Func<LightingSceneDoc> legacyScene)
    {
        _path = Path.Combine(dir, "pc-setup.json");
        _legacyScene = legacyScene;
    }

    /// <summary>Raised with the new revision after every save, outside the store lock.</summary>
    public event Action<int>? Changed;

    /// <summary>A private copy.</summary>
    public PcSetupFile Load()
    {
        lock (_lock)
        {
            return Clone(SavedUnlocked() ?? Migrated());
        }
    }

    /// <summary>The saved record, or null when no client has saved one yet.</summary>
    public PcSetupFile? LoadSaved()
    {
        lock (_lock)
        {
            return SavedUnlocked() is { } file ? Clone(file) : null;
        }
    }

    /// <summary>Saves <paramref name="next"/> as revision current + 1 when the current revision is <paramref name="baseRev"/>; otherwise returns false with the current file.</summary>
    public bool TrySave(int baseRev, PcSetup next, out PcSetupFile current)
    {
        lock (_lock)
        {
            var existing = SavedUnlocked() ?? Migrated();
            if (existing.Rev != baseRev)
            {
                current = Clone(existing);
                return false;
            }
            current = WriteUnlocked(existing.Rev, next);
        }
        Changed?.Invoke(current.Rev);
        return true;
    }

    /// <summary>Saves <paramref name="next"/> over whatever is there (a backup restore).</summary>
    public PcSetupFile Replace(PcSetup next)
    {
        PcSetupFile saved;
        lock (_lock)
        {
            var existing = SavedUnlocked() ?? Migrated();
            saved = WriteUnlocked(existing.Rev, next);
        }
        Changed?.Invoke(saved.Rev);
        return Clone(saved);
    }

    /// <summary>Forgets the saved record, file included, so a shared test host starts each test clean.</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            try { File.Delete(_path); } catch (IOException) { /* already gone */ }
            _saved = null;
            _read = true;
        }
    }

    private PcSetupFile WriteUnlocked(int currentRev, PcSetup next)
    {
        var file = new PcSetupFile { Rev = currentRev + 1, Setup = next };
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        AtomicJsonFile.Write(_path, JsonSerializer.Serialize(file, PersistenceJsonContext.Default.PcSetupFile));
        _saved = Clone(file);
        _read = true;
        return file;
    }

    private PcSetupFile? SavedUnlocked()
    {
        if (!_read)
        {
            _saved = ReadUnlocked();
            _read = true;
        }
        return _saved;
    }

    private PcSetupFile? ReadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            return Normalize(JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.PcSetupFile));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pc-setup] load failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private PcSetupFile Migrated()
    {
        PcSetup? setup = null;
        try
        {
            setup = PcSetupMigration.FromScene(_legacyScene());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pc-setup] migration failed: {ex.GetType().Name}: {ex.Message}");
        }
        return new PcSetupFile { Rev = 0, Setup = setup ?? new PcSetup() };
    }

    // A hand-edited or truncated file can carry nulls where the model expects values.
    private static PcSetupFile? Normalize(PcSetupFile? file)
    {
        if (file is null)
        {
            return null;
        }
        file.Setup ??= new PcSetup();
        file.Setup.Case ??= new PcSetupCase();
        file.Setup.Items = (file.Setup.Items ?? new()).FindAll(i => i is not null);
        file.Setup.Resolved ??= new PcSetupResolved();
        file.Setup.Resolved.Objects = (file.Setup.Resolved.Objects ?? new()).FindAll(o => o is not null);
        foreach (var o in file.Setup.Resolved.Objects)
        {
            o.Anchors = (o.Anchors ?? new()).FindAll(a => a is not null);
        }
        return file;
    }

    private static PcSetupFile Clone(PcSetupFile file) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(file, PersistenceJsonContext.Default.PcSetupFile),
            PersistenceJsonContext.Default.PcSetupFile) ?? new PcSetupFile();
}
