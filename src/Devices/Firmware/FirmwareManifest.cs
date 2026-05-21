using System.Collections.Generic;

namespace Qos.Service.Devices.Firmware;

/// <summary>
/// On-disk + remote firmware manifest for a single device type. Mirrors the
/// shape documented in STRATEGY.md. Lives at
/// <c>&lt;firmware-root&gt;/&lt;deviceType&gt;/manifest.json</c>.
/// </summary>
public sealed class FirmwareManifest
{
    /// <summary>The latest version key in <see cref="Files"/>.</summary>
    public string Latest { get; set; } = "";

    /// <summary>Map of version string → file metadata. Versions follow each device's native scheme (NP50 uses "MAJ.MIN.BUILD.REV").</summary>
    public Dictionary<string, FirmwareFile> Files { get; set; } = new();
}

public sealed class FirmwareFile
{
    /// <summary>Lowercase hex SHA-256 of the binary.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Expected file size in bytes. Pre-check before download, sanity check after.</summary>
    public long Size { get; set; }

    /// <summary>Remote URL to fetch the binary from. Null/empty when this entry only describes a bundled-in-installer binary.</summary>
    public string? Url { get; set; }

    /// <summary>Optional human-readable changelog shown in the UI.</summary>
    public string? Changelog { get; set; }
}
