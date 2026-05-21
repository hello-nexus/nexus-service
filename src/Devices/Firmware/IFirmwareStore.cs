using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Qos.Service.Devices.Firmware;

/// <summary>
/// Local cache + download manager for device firmware binaries. Owns the
/// <c>&lt;firmware-root&gt;/&lt;deviceType&gt;/</c> directory layout, atomic
/// manifest read/write, and HTTP downloads with SHA-256 validation.
///
/// Not opinionated about bundled-fallback vs cache vs remote precedence —
/// that combination lives in per-device "firmware source" classes
/// (e.g. <c>Np50FirmwareSource</c>) that use this store as a building block.
/// </summary>
public interface IFirmwareStore
{
    /// <summary>Resolve the absolute directory used to cache binaries for the given device type. Created on demand.</summary>
    string GetDeviceDir(string deviceType);

    /// <summary>Resolve the absolute path where a given version's binary lives on disk. Does not check existence.</summary>
    string GetBinaryPath(string deviceType, string version, string extension);

    /// <summary>Load the cached manifest, or null if it has never been saved or is unreadable.</summary>
    Task<FirmwareManifest?> LoadLocalManifestAsync(string deviceType, CancellationToken ct);

    /// <summary>Atomically write the manifest to disk. Replaces any existing copy.</summary>
    Task SaveLocalManifestAsync(string deviceType, FirmwareManifest manifest, CancellationToken ct);

    /// <summary>Fetch a remote manifest from a URL, validating it parses. Does not write to disk.</summary>
    Task<FirmwareManifest> FetchRemoteManifestAsync(string manifestUrl, CancellationToken ct);

    /// <summary>
    /// Download the binary at <paramref name="url"/> into the cache for
    /// <paramref name="deviceType"/>, validate its SHA-256 matches
    /// <paramref name="expectedSha256"/>, and return the local path.
    /// If the binary already exists and validates, the network is not hit.
    /// On checksum mismatch the partial file is discarded and an exception is thrown.
    /// </summary>
    Task<string> DownloadAsync(
        string deviceType,
        string version,
        string extension,
        string url,
        string expectedSha256,
        long expectedSize,
        IProgress<long>? progress,
        CancellationToken ct);

    /// <summary>Return true if the version's binary exists on disk and its SHA-256 matches.</summary>
    Task<bool> HasValidBinaryAsync(string deviceType, string version, string extension, string expectedSha256, CancellationToken ct);
}
