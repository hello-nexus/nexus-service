using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Update;

/// <summary>
/// Downloads an update installer to the updates staging directory and verifies its
/// SHA-256 hash. Reports byte progress via an optional callback so the progress
/// endpoint can show a percentage.
///
/// Uses a dedicated long-timeout HttpClient; the standard 10s timeout is far too
/// short for a large installer download.
/// </summary>
public sealed class UpdateDownloader
{
    private const int CopyBufferSize = 81920;

    /// <summary>Machine-wide staging dir: %ProgramData%\Nexus\updates\</summary>
    public static string StagingDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Nexus",
            "updates");

    /// <summary>
    /// Creates the staging dir and, on the LocalSystem service, locks it to
    /// SYSTEM + Administrators. The dir holds the install directive (marker), the
    /// installer, and the .cmd launched as SYSTEM, so a non-admin able to write
    /// here is a privilege escalation. %ProgramData% is user-writable by default.
    /// No-op when running interactively (a dev is not the LocalSystem threat).
    /// </summary>
    public static void EnsureSecureStagingDir()
    {
        Directory.CreateDirectory(StagingDir);
        if (OperatingSystem.IsWindows() && WindowsDirectorySecurity.IsLocalSystem())
        {
            try { WindowsDirectorySecurity.Protect(StagingDir, resetOwner: true); }
            catch { /* never block staging on an ACL failure */ }
        }
    }

    private readonly IHttpClientFactory _http;

    public UpdateDownloader(IHttpClientFactory http)
    {
        _http = http;
    }

    /// <summary>
    /// Downloads <paramref name="manifest"/> to the staging directory. Streams with
    /// progress, then verifies SHA-256. Returns the full path of the verified installer.
    /// Throws when the hash is missing or mismatched.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateManifest manifest,
        IProgress<long>? byteProgress,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(manifest.Sha256))
        {
            throw new InvalidOperationException(
                "Cannot download: no SHA-256 hash available for integrity verification.");
        }

        EnsureSecureStagingDir();

        var fileName = $"Nexus-Setup-{manifest.Version}.exe";
        var finalPath = Path.Combine(StagingDir, fileName);
        var tmpPath = finalPath + ".tmp";

        // Keep only the installer for the version being staged. Each installer is
        // tens of MB and a new release supersedes any queued one, so a stale
        // installer must not survive (it would also leave a marker/path pointing
        // at an old version). Runs before the valid-skip below so re-staging the
        // same version still reuses its file.
        PruneStaleInstallers(StagingDir, fileName);

        // Skip re-download if the staged file is already valid.
        if (File.Exists(finalPath) && await IsValidAsync(finalPath, manifest.Sha256, manifest.AssetSize, ct))
        {
            return finalPath;
        }

        TryDelete(tmpPath);

        using var client = BuildClient();
        using var resp = await client.GetAsync(manifest.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        // Flush + close the write stream before reading the file back: a buffered
        // FileStream leaves up to one CopyBufferSize unwritten to disk until it is
        // disposed, so a size/hash check on the still-open file sees a short file.
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
        {
            await CopyWithProgressAsync(src, dst, byteProgress, ct);
        }

        if (manifest.AssetSize > 0)
        {
            var actualSize = new FileInfo(tmpPath).Length;
            if (actualSize != manifest.AssetSize)
            {
                TryDelete(tmpPath);
                throw new InvalidDataException(
                    $"Downloaded size {actualSize} does not match expected {manifest.AssetSize}.");
            }
        }

        var actualSha = await ComputeSha256Async(tmpPath, ct);
        if (!string.Equals(actualSha, manifest.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            TryDelete(tmpPath);
            throw new InvalidDataException(
                $"SHA-256 mismatch: got {actualSha}, expected {manifest.Sha256.ToLowerInvariant()}.");
        }

        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);
        return finalPath;
    }

    private static async Task<bool> IsValidAsync(string path, string expectedSha256, long expectedSize, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        if (expectedSize > 0 && new FileInfo(path).Length != expectedSize) return false;
        var actual = await ComputeSha256Async(path, ct);
        return string.Equals(actual, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

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

    private HttpClient BuildClient()
    {
        var client = _http.CreateClient("UpdateDownload");
        // No timeout: a ~350MB download takes minutes on a slow link. The
        // cancellation token on DownloadAsync is the caller's kill switch.
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// Deletes every <c>Nexus-Setup-*</c> file (installers and any partial
    /// <c>.tmp</c>) in <paramref name="stagingDir"/> except
    /// <paramref name="keepFileName"/>, capping the staging dir at the single
    /// installer being staged. Marker / run-ota / log files use other prefixes
    /// and are left in place.
    /// </summary>
    internal static void PruneStaleInstallers(string stagingDir, string keepFileName)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(stagingDir))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("Nexus-Setup-", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(f);
                }
            }
        }
        catch { }
    }
}
