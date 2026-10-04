using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting.Scene;

/// <summary>
/// The scene document and the imported case model, each in its own file under
/// the machine-scope data root: the scene names this machine's device ids, so
/// it never rides the synced profile. A missing or corrupt document reads as an
/// empty scene.
/// </summary>
public sealed class LightingSceneStore
{
    private readonly string _docPath;
    private readonly string _modelPath;
    private readonly object _lock = new();
    private LightingSceneDoc? _cached;

    public LightingSceneStore() : this(NexusDataPaths.NexusRoot()) { }

    public LightingSceneStore(string dir)
    {
        _docPath = Path.Combine(dir, "lighting-scene.json");
        _modelPath = Path.Combine(dir, "lighting-scene.glb");
    }

    /// <summary>Raised after every successful write, outside the store lock.</summary>
    public event Action? Changed;

    /// <summary>A private copy; mutate it and hand it to <see cref="Save"/>.</summary>
    public LightingSceneDoc Load()
    {
        lock (_lock)
        {
            return Clone(_cached ??= ReadUnlocked());
        }
    }

    public void Save(LightingSceneDoc doc)
    {
        lock (_lock)
        {
            WriteUnlocked(doc);
            _cached = Clone(doc);
        }
        Changed?.Invoke();
    }

    /// <summary>The imported model's bytes, or null when none was imported.</summary>
    public byte[]? LoadModel()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(_modelPath) ? File.ReadAllBytes(_modelPath) : null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lighting-scene] model read failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Writes the model (null deletes it) and returns its content hash.</summary>
    public string? SaveModel(byte[]? bytes)
    {
        lock (_lock)
        {
            EnsureDir();
            if (bytes is null)
            {
                if (File.Exists(_modelPath))
                {
                    File.Delete(_modelPath);
                }
                return null;
            }
            AtomicJsonFile.Write(_modelPath, bytes);
            return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        }
    }

    private LightingSceneDoc ReadUnlocked()
    {
        try
        {
            if (!File.Exists(_docPath))
            {
                return new LightingSceneDoc();
            }
            var json = File.ReadAllText(_docPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new LightingSceneDoc();
            }
            return Normalize(JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.LightingSceneDoc) ?? new LightingSceneDoc());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lighting-scene] load failed: {ex.GetType().Name}: {ex.Message}");
            return new LightingSceneDoc();
        }
    }

    // A hand-edited or truncated file can carry nulls where the model expects lists.
    private static LightingSceneDoc Normalize(LightingSceneDoc doc)
    {
        doc.Objects = (doc.Objects ?? new()).FindAll(o => o is not null);
        foreach (var o in doc.Objects)
        {
            o.Anchors = (o.Anchors ?? new()).FindAll(a => a is not null);
        }
        doc.Bindings = (doc.Bindings ?? new()).FindAll(b => b is not null);
        foreach (var b in doc.Bindings)
        {
            b.Targets = (b.Targets ?? new()).FindAll(t => t is not null);
        }
        return doc;
    }

    private void WriteUnlocked(LightingSceneDoc doc)
    {
        EnsureDir();
        AtomicJsonFile.Write(_docPath, JsonSerializer.Serialize(doc, PersistenceJsonContext.Default.LightingSceneDoc));
    }

    private void EnsureDir()
    {
        var dir = Path.GetDirectoryName(_docPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static LightingSceneDoc Clone(LightingSceneDoc doc) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(doc, PersistenceJsonContext.Default.LightingSceneDoc),
            PersistenceJsonContext.Default.LightingSceneDoc) ?? new LightingSceneDoc();
}
