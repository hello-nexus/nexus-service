using System;
using System.Collections.Generic;
using System.Linq;
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
        Setup = new PcSetupStore(dir, Load);
        Cooling = new Nexus.Service.Cooling.CoolingLayoutStore(dir);
        // A record save re-applies the scene: the engine samples from the record's resolved objects.
        Setup.Changed += _ => Changed?.Invoke();
    }

    /// <summary>The shared PC setup, in the same directory.</summary>
    public PcSetupStore Setup { get; }

    /// <summary>The 3D cooling layout, in the same directory.</summary>
    public Nexus.Service.Cooling.CoolingLayoutStore Cooling { get; }

    /// <summary>
    /// <see cref="Load"/> with the objects replaced by the saved PC setup's
    /// resolved objects when there are any; the scene file's own objects
    /// otherwise. Bindings are always the scene's.
    /// </summary>
    public LightingSceneDoc LoadEffective()
    {
        var doc = Load();
        if (Setup.LoadSaved() is { Setup.Resolved.Objects.Count: > 0 } saved)
        {
            doc.Objects = saved.Setup.Resolved.Objects;
        }
        if (HealBindings(doc.Bindings, doc.Objects))
        {
            var healed = false;
            lock (_lock)
            {
                // Re-heal the stored copy under the write lock, so concurrent readers persist it once.
                var stored = Clone(_cached ??= ReadUnlocked());
                if (HealBindings(stored.Bindings, doc.Objects))
                {
                    try
                    {
                        WriteUnlocked(stored);
                        _cached = Clone(stored);
                        healed = true;
                    }
                    catch (Exception ex)
                    {
                        // A failed write must not fail the read: the healed view still applies, and the next read retries.
                        Console.Error.WriteLine($"[lighting-scene] heal persist failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                doc.Bindings = stored.Bindings;
            }
            if (healed)
            {
                Changed?.Invoke();
            }
        }
        return doc;
    }

    /// <summary>
    /// Retargets a binding whose object no longer exists (an id from before the shared PC setup, like
    /// <c>mouse-1</c>) onto the one object of the same kind, when that object has the target's anchor or
    /// a surface and no other target holds it. One target at a time, so two missing ids never share an
    /// object. Only legacy <c>kind-N</c> ids heal. With zero or several candidates the target is left as it is. True when anything changed.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex s_legacyId = new(@"^([a-z]+)-\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool HealBindings(List<SceneBinding> bindings, List<SceneObject> objects)
    {
        var present = new HashSet<string>(objects.Select(o => o.Id), StringComparer.Ordinal);
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            foreach (var target in binding.Targets)
            {
                if (present.Contains(target.ObjectId))
                {
                    held.Add(target.ObjectId);
                }
            }
        }
        var changed = false;
        foreach (var binding in bindings)
        {
            foreach (var target in binding.Targets)
            {
                if (present.Contains(target.ObjectId))
                {
                    continue;
                }
                // Only the ids the scene editor minted before the shared setup (kind-N); a removed p-... item stays unbound.
                if (s_legacyId.Match(target.ObjectId) is not { Success: true } legacy)
                {
                    continue;
                }
                var kind = legacy.Groups[1].Value;
                var candidates = objects.Where(o => string.Equals(o.Kind, kind, StringComparison.Ordinal)).ToList();
                if (candidates.Count != 1 || held.Contains(candidates[0].Id))
                {
                    continue;
                }
                var anchors = candidates[0].Anchors;
                var anchor = anchors.FirstOrDefault(a => a.Id == target.AnchorId)
                    ?? (anchors.Count(a => a.Kind == "surface") == 1 ? anchors.First(a => a.Kind == "surface") : null);
                if (anchor is null)
                {
                    continue;
                }
                target.ObjectId = candidates[0].Id;
                target.AnchorId = anchor.Id;
                held.Add(candidates[0].Id);
                changed = true;
            }
        }
        return changed;
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
