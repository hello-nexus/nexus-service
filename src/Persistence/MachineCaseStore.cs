using System;
using System.IO;
using System.Text.Json;
using Nexus.Service.Serialization;

namespace Nexus.Service.Persistence;

/// <summary>On-disk shape of the machine-local case pick.</summary>
public sealed class MachineCaseFile
{
    public string? CaseId { get; set; }

    /// <summary>True while the pick has not reached the active cloud account.</summary>
    public bool Pending { get; set; }
}

/// <summary>
/// This machine's PC case (a catalog part id), kept in its own file under the
/// machine-scope data root rather than settings.json: settings.json is the
/// profile that syncs to the cloud, and a case belongs to one machine. Written
/// atomically; a missing or corrupt file reads as "no case, nothing pending".
/// </summary>
public sealed class MachineCaseStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public MachineCaseStore() : this(Path.Combine(NexusDataPaths.NexusRoot(), "system-case.json")) { }

    public MachineCaseStore(string path)
    {
        _path = path;
    }

    public MachineCaseFile Load()
    {
        lock (_lock)
        {
            return ReadUnlocked();
        }
    }

    public void Save(string? caseId, bool pending)
    {
        lock (_lock)
        {
            WriteUnlocked(new MachineCaseFile { CaseId = caseId, Pending = pending });
        }
    }

    /// <summary>Atomically rewrites the file from the current state; the mutator runs under the store lock.</summary>
    public MachineCaseFile Update(Func<MachineCaseFile, MachineCaseFile> mutator)
    {
        lock (_lock)
        {
            var next = mutator(ReadUnlocked());
            WriteUnlocked(next);
            return next;
        }
    }

    private MachineCaseFile ReadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new MachineCaseFile();
            }
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new MachineCaseFile();
            }
            return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.MachineCaseFile) ?? new MachineCaseFile();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[machine-case] load failed: {ex.GetType().Name}: {ex.Message}");
            return new MachineCaseFile();
        }
    }

    private void WriteUnlocked(MachineCaseFile file)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            AtomicJsonFile.Write(_path, JsonSerializer.Serialize(file, PersistenceJsonContext.Default.MachineCaseFile));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[machine-case] save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
