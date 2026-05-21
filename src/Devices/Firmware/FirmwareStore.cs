using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Persistence;
using Qos.Service.Serialization;

namespace Qos.Service.Devices.Firmware;

/// <summary>
/// File-system + HTTP-backed implementation of <see cref="IFirmwareStore"/>.
///
/// Layout on disk:
/// <code>
/// &lt;root&gt;/
///   np50/
///     manifest.json
///     2.0.5.1.hex
///   q60/
///     ...
/// </code>
///
/// Manifest writes go through <see cref="AtomicJsonFile"/> so a crash mid-write
/// can't leave a truncated file we'd fail to parse on next boot. Binary
/// downloads stream to <c>&lt;version&gt;.&lt;ext&gt;.tmp</c>, are SHA-validated
/// in place, then renamed.
/// </summary>
public sealed class FirmwareStore : IFirmwareStore
{
    private const int CopyBufferSize = 81920;

    private readonly string _root;
    private readonly HttpClient _http;

    public FirmwareStore(string root, HttpClient http)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Convenience constructor that resolves the platform-default firmware root.</summary>
    public FirmwareStore(HttpClient http) : this(ResolveDefaultRoot(), http)
    {
    }

    public string GetDeviceDir(string deviceType)
    {
        ValidateDeviceType(deviceType);
        var dir = Path.Combine(_root, deviceType);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetBinaryPath(string deviceType, string version, string extension)
    {
        ValidateDeviceType(deviceType);
        ValidateVersion(version);
        var ext = NormalizeExtension(extension);
        return Path.Combine(GetDeviceDir(deviceType), version + ext);
    }

    public async Task<FirmwareManifest?> LoadLocalManifestAsync(string deviceType, CancellationToken ct)
    {
        var path = Path.Combine(GetDeviceDir(deviceType), "manifest.json");
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, PersistenceJsonContext.Default.FirmwareManifest, ct);
        }
        catch (Exception ex)
        {
            // Treat a corrupt manifest as missing so the source can refetch.
            // A stale partial write here would otherwise wedge updates.
            Console.Error.WriteLine($"[firmware-store] manifest read failed for {deviceType}: {ex.Message}");
            return null;
        }
    }

    public Task SaveLocalManifestAsync(string deviceType, FirmwareManifest manifest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var path = Path.Combine(GetDeviceDir(deviceType), "manifest.json");
        var json = JsonSerializer.Serialize(manifest, PersistenceJsonContext.Default.FirmwareManifest);
        AtomicJsonFile.Write(path, json);
        return Task.CompletedTask;
    }

    public async Task<FirmwareManifest> FetchRemoteManifestAsync(string manifestUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) throw new ArgumentException("Manifest URL is required.", nameof(manifestUrl));

        using var resp = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var parsed = await JsonSerializer.DeserializeAsync(stream, PersistenceJsonContext.Default.FirmwareManifest, ct)
            ?? throw new InvalidDataException("Remote firmware manifest deserialized to null.");
        if (string.IsNullOrEmpty(parsed.Latest) || parsed.Files.Count == 0)
            throw new InvalidDataException("Remote firmware manifest is missing 'latest' or 'files'.");
        return parsed;
    }

    public async Task<string> DownloadAsync(
        string deviceType,
        string version,
        string extension,
        string url,
        string expectedSha256,
        long expectedSize,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Download URL is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(expectedSha256)) throw new ArgumentException("Expected SHA-256 is required.", nameof(expectedSha256));

        var finalPath = GetBinaryPath(deviceType, version, extension);
        if (await HasValidBinaryAsync(deviceType, version, extension, expectedSha256, ct))
        {
            // Already cached & valid — caller can use it directly.
            return finalPath;
        }

        var tmpPath = finalPath + ".tmp";
        // Drop any leftover from a previously aborted download. Don't try to
        // resume partial content — the SHA wouldn't match anyway.
        TryDelete(tmpPath);

        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await CopyWithProgressAsync(src, dst, progress, ct);
            }
        }

        var actualSize = new FileInfo(tmpPath).Length;
        if (expectedSize > 0 && actualSize != expectedSize)
        {
            TryDelete(tmpPath);
            throw new InvalidDataException($"Downloaded {deviceType}/{version}{NormalizeExtension(extension)} size {actualSize} does not match manifest size {expectedSize}.");
        }

        var actualSha = await ComputeSha256Async(tmpPath, ct);
        if (!string.Equals(actualSha, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            TryDelete(tmpPath);
            throw new InvalidDataException($"Downloaded {deviceType}/{version}{NormalizeExtension(extension)} SHA-256 {actualSha} does not match manifest {expectedSha256.ToLowerInvariant()}.");
        }

        // Rename onto the final path. If a stale copy somehow exists alongside
        // a fresh download, File.Move with overwrite handles it on .NET 5+.
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);
        return finalPath;
    }

    public async Task<bool> HasValidBinaryAsync(string deviceType, string version, string extension, string expectedSha256, CancellationToken ct)
    {
        var path = GetBinaryPath(deviceType, version, extension);
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
        var actual = await ComputeSha256Async(path, ct);
        return string.Equals(actual, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal);
    }

    // ── Helpers ──

    private static async Task CopyWithProgressAsync(Stream src, Stream dst, IProgress<long>? progress, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await src.ReadAsync(buf.AsMemory(0, CopyBufferSize), ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                total += read;
                progress?.Report(total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "";
        return extension.StartsWith('.') ? extension : "." + extension;
    }

    private static void ValidateDeviceType(string deviceType)
    {
        if (string.IsNullOrWhiteSpace(deviceType)) throw new ArgumentException("Device type is required.", nameof(deviceType));
        // Keep it filesystem-safe: lowercase letters, digits, dash. Reject
        // anything that could traverse out of the root or land on a Windows
        // reserved name.
        foreach (var c in deviceType)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                throw new ArgumentException($"Device type '{deviceType}' contains illegal characters.", nameof(deviceType));
        }
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Version is required.", nameof(version));
        foreach (var c in version)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                throw new ArgumentException($"Version '{version}' contains illegal characters.", nameof(version));
        }
    }

    private static string ResolveDefaultRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Machine-scope: the LocalSystem service owns the cache so
            // every user on the box sees the same firmware versions.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Qos", "firmware");
        }

        // Linux. XDG_CACHE_HOME is the right home for downloaded/regenerable
        // binaries per the XDG basedir spec; settings.json uses XDG_CONFIG_HOME
        // because it's user state, not a cache.
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }
        return Path.Combine(xdg, "Qos", "firmware");
    }
}
