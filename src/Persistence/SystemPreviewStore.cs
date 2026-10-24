using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Nexus.Service.Serialization;

namespace Nexus.Service.Persistence;

/// <summary>Sidecar describing the stored preview image.</summary>
public sealed class SystemPreviewMeta
{
    public string? CaseId { get; set; }

    /// <summary>Lowercase hex sha256 of the PNG bytes.</summary>
    public string? Sha256 { get; set; }
}

/// <summary>A stored preview image with its hash.</summary>
public sealed record SystemPreview(string CaseId, string Sha256, byte[] Bytes);

/// <summary>
/// The rendered image of this machine's PC, kept next to the machine case
/// file rather than in settings.json: it belongs to one machine and never
/// syncs. The PNG and its json sidecar are each written atomically; the
/// sidecar goes last, and a read re-hashes the PNG against it, so a torn
/// pair reads as "no preview".
/// </summary>
public sealed class SystemPreviewStore
{
    private readonly string _pngPath;
    private readonly string _metaPath;
    private readonly object _lock = new();

    public SystemPreviewStore() : this(NexusDataPaths.NexusRoot()) { }

    public SystemPreviewStore(string dir)
    {
        _pngPath = Path.Combine(dir, "system-preview.png");
        _metaPath = Path.Combine(dir, "system-preview.json");
    }

    /// <summary>Writes the pair; false when the disk write failed (logged), leaving any earlier preview as it was or torn-and-ignored.</summary>
    public bool Save(string caseId, byte[] png)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(png));
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_pngPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                AtomicJsonFile.Write(_pngPath, png);
                AtomicJsonFile.Write(_metaPath, JsonSerializer.Serialize(new SystemPreviewMeta { CaseId = caseId, Sha256 = sha }, PersistenceJsonContext.Default.SystemPreviewMeta));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[system-preview] save failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>The stored preview when it exists, is intact, and was rendered for <paramref name="caseId"/>; otherwise null.</summary>
    public SystemPreview? TryGet(string? caseId)
    {
        if (caseId is null)
        {
            return null;
        }
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_metaPath) || !File.Exists(_pngPath))
                {
                    return null;
                }
                var meta = JsonSerializer.Deserialize(File.ReadAllText(_metaPath), PersistenceJsonContext.Default.SystemPreviewMeta);
                if (meta?.CaseId != caseId || string.IsNullOrEmpty(meta.Sha256))
                {
                    return null;
                }
                var bytes = File.ReadAllBytes(_pngPath);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), meta.Sha256, StringComparison.Ordinal))
                {
                    return null;
                }
                return new SystemPreview(caseId, meta.Sha256, bytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[system-preview] load failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
