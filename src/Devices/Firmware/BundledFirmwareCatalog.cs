using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// Read-only catalog of firmware images shipped inside the service binary.
///
/// Firmware is embedded at build time from the firmware directory the csproj
/// finds (vendor images kept outside this repository; see the README's Build
/// section) under the manifest name
/// <c>firmware/&lt;deviceId&gt;/&lt;version&gt;.hex</c>, where
/// <c>deviceId</c> matches <see cref="IDeviceHandler.Id"/>. There is no OTA /
/// download path - the only versions a machine can install are the ones baked
/// into the build it is running.
///
/// The catalog is what the Firmware Updates page reads to show the "available"
/// version next to each device's reported current version.
/// </summary>
public sealed class BundledFirmwareCatalog
{
    private const string ResourcePrefix = "firmware/";
    private const string HexExtension = ".hex";

    // deviceId -> versions, newest first.
    // deviceId -> versions (newest first).
    private readonly Dictionary<string, List<string>> _versions = new(StringComparer.Ordinal);
    // "deviceId/version" -> the ACTUAL manifest resource name. The build host's
    // separator differs (macOS '/' vs Windows '\'), so we can't reconstruct the
    // name with a fixed separator - store and reuse what the assembly reports.
    private readonly Dictionary<string, string> _resourceNames = new(StringComparer.Ordinal);
    private readonly Assembly _assembly;

    public BundledFirmwareCatalog() : this(typeof(BundledFirmwareCatalog).Assembly)
    {
    }

    // Assembly-injectable for tests.
    public BundledFirmwareCatalog(Assembly assembly)
    {
        _assembly = assembly;
        Scan(assembly);
    }

    /// <summary>All device ids that have at least one bundled firmware version.</summary>
    public IReadOnlyCollection<string> DeviceIds => _versions.Keys;

    /// <summary>
    /// Newest bundled version for the device, or empty string when nothing is
    /// bundled for it. Device id matches <see cref="IDeviceHandler.Id"/>.
    /// </summary>
    public string GetLatestVersion(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return "";
        return _versions.TryGetValue(deviceId, out var list) && list.Count > 0 ? list[0] : "";
    }

    /// <summary>All bundled versions for the device, newest first.</summary>
    public IReadOnlyList<string> GetAvailableVersions(string deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId) && _versions.TryGetValue(deviceId, out var list))
            return list;
        return Array.Empty<string>();
    }

    /// <summary>
    /// Opens the embedded .hex for (device, version), or null when it isn't
    /// bundled. The caller owns the stream. Used by the flasher to extract the
    /// image to a temp file before handing it to dfu-util.
    /// </summary>
    public Stream? OpenFirmware(string deviceId, string version)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(version)) return null;
        return _resourceNames.TryGetValue($"{deviceId}/{version}", out var actual)
            ? _assembly.GetManifestResourceStream(actual)
            : null;
    }

    /// <summary>
    /// True when <paramref name="available"/> is strictly newer than
    /// <paramref name="current"/>. Returns false if either is empty or
    /// unparseable (e.g. the device hasn't reported its version yet).
    /// </summary>
    public static bool IsNewer(string available, string current)
    {
        if (string.IsNullOrEmpty(available) || string.IsNullOrEmpty(current)) return false;
        return CompareVersions(available, current) > 0;
    }

    private void Scan(Assembly assembly)
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // RecursiveDir can use either separator depending on the build host
            // (macOS AOT build vs Windows installer build); normalize to '/' for
            // parsing, but keep the ORIGINAL name for opening the stream.
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            if (!normalized.EndsWith(HexExtension, StringComparison.OrdinalIgnoreCase)) continue;

            var rel = normalized.Substring(ResourcePrefix.Length);
            var slash = rel.IndexOf('/');
            if (slash <= 0 || slash >= rel.Length - 1) continue;

            var deviceId = rel.Substring(0, slash);
            var version = rel.Substring(slash + 1, rel.Length - slash - 1 - HexExtension.Length);
            if (version.Length == 0) continue;

            if (!_versions.TryGetValue(deviceId, out var list))
            {
                list = new List<string>();
                _versions[deviceId] = list;
            }
            if (!list.Contains(version)) list.Add(version);
            _resourceNames[$"{deviceId}/{version}"] = name;
        }

        foreach (var list in _versions.Values)
            list.Sort((a, b) => CompareVersions(b, a)); // newest first
    }

    /// <summary>
    /// Numeric dotted-version compare ("2.0.9.1" vs "1.0.3.1"). Missing
    /// trailing parts count as 0; non-numeric parts count as 0.
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var na = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            var nb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (na != nb) return na.CompareTo(nb);
        }
        return 0;
    }
}
