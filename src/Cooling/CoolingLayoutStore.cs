using System;
using System.IO;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Cooling;

/// <summary>
/// <c>cooling-layout.json</c>, machine scope next to the lighting scene: slot keys
/// and channel ids name this machine's case and fans. A missing or corrupt file
/// reads as an empty layout at revision 0; nothing is written until a client saves.
/// </summary>
public sealed class CoolingLayoutStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private CoolingLayoutFile? _saved;
    private bool _read;

    public CoolingLayoutStore(string dir) => _path = Path.Combine(dir, "cooling-layout.json");

    /// <summary>Raised with the new revision after every save, outside the store lock.</summary>
    public event Action<int>? Changed;

    /// <summary>A private copy.</summary>
    public CoolingLayoutFile Load()
    {
        lock (_lock)
        {
            return Clone(SavedUnlocked() ?? new CoolingLayoutFile());
        }
    }

    /// <summary>Saves <paramref name="next"/> as revision current + 1 when the current revision is <paramref name="baseRev"/>; otherwise returns false with the current file.</summary>
    public bool TrySave(int baseRev, CoolingLayout next, out CoolingLayoutFile current)
    {
        lock (_lock)
        {
            var existing = SavedUnlocked() ?? new CoolingLayoutFile();
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

    /// <summary>Forgets the saved layout, file included, so a shared test host starts each test clean.</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            try { File.Delete(_path); } catch (IOException) { /* already gone */ }
            _saved = null;
            _read = true;
        }
    }

    private CoolingLayoutFile WriteUnlocked(int currentRev, CoolingLayout next)
    {
        var file = new CoolingLayoutFile { Rev = currentRev + 1, Layout = next };
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        AtomicJsonFile.Write(_path, JsonSerializer.Serialize(file, PersistenceJsonContext.Default.CoolingLayoutFile));
        _saved = Clone(file);
        _read = true;
        return file;
    }

    private CoolingLayoutFile? SavedUnlocked()
    {
        if (!_read)
        {
            _saved = ReadUnlocked();
            _read = true;
        }
        return _saved;
    }

    private CoolingLayoutFile? ReadUnlocked()
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
            var file = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.CoolingLayoutFile);
            return file is not null && CoolingLayoutValidation.Validate(file.Layout) is null ? file : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cooling-layout] load failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static CoolingLayoutFile Clone(CoolingLayoutFile file) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(file, PersistenceJsonContext.Default.CoolingLayoutFile),
            PersistenceJsonContext.Default.CoolingLayoutFile) ?? new CoolingLayoutFile();
}
