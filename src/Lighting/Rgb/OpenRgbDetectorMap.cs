using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Reads <c>detector-map.json</c>, which the daemon rewrites whenever its
/// device list changes: device name -> the REGISTER_*_DETECTOR name that
/// produced it. The SDK carries only the device name, and the OpenRGB.json
/// denylist is keyed by the detector, so an exclusion built from the device
/// name is a silent no-op wherever the two differ.
/// </summary>
public static class OpenRgbDetectorMap
{
    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);

    /// <summary>Loads the map, or an empty one when the file is absent or
    /// unreadable; a daemon predating the map never writes it.</summary>
    public static IReadOnlyDictionary<string, string> Load(string configDir)
    {
        try
        {
            var path = Path.Combine(configDir, "detector-map.json");
            if (!File.Exists(path))
            {
                return EmptyMap;
            }
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            // Read once per settle; warn once per process.
            if (!_readFailureLogged)
            {
                _readFailureLogged = true;
                ServiceLog.Warn($"[openrgb] detector map read failed: {ex.Message}");
            }
            return EmptyMap;
        }
    }

    private static IReadOnlyDictionary<string, string>? _override;

    /// <summary>Test seam: replaces the on-disk map for the process until set back to null.</summary>
    internal static IReadOnlyDictionary<string, string>? OverrideForTests
    {
        set => Volatile.Write(ref _override, value);
    }

    private static readonly object CacheLock = new();
    private static string? _cachedPath;
    private static DateTime _cachedWriteUtc;
    private static IReadOnlyDictionary<string, string> _cached = EmptyMap;

    /// <summary>The map for the service config dir, re-read only when the daemon rewrites the file.</summary>
    public static IReadOnlyDictionary<string, string> Current
    {
        get
        {
            if (Volatile.Read(ref _override) is { } forced)
            {
                return forced;
            }
            var dir = OpenRgbProcessManager.ResolveConfigDir();
            var path = Path.Combine(dir, "detector-map.json");
            DateTime writeUtc;
            try
            { writeUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return EmptyMap; }
            lock (CacheLock)
            {
                if (_cachedPath != path || _cachedWriteUtc != writeUtc)
                {
                    _cached = Load(dir);
                    _cachedPath = path;
                    _cachedWriteUtc = writeUtc;
                }
                return _cached;
            }
        }
    }

    /// <summary>Parses the document body. Separate from the read so the shape
    /// is testable without a filesystem; throws on text that is not JSON,
    /// which only <see cref="Load"/> catches.</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["devices"] is not JsonObject devices)
        {
            return EmptyMap;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in devices)
        {
            if (kv.Value is JsonValue value
                && value.TryGetValue<string>(out var detector)
                && !string.IsNullOrEmpty(kv.Key)
                && !string.IsNullOrEmpty(detector))
            {
                map[kv.Key] = detector;
            }
        }
        return map;
    }

    /// <summary>The detector to denylist for this device name, falling back to
    /// the device name when the map has no entry.</summary>
    public static string Resolve(IReadOnlyDictionary<string, string>? map, string deviceName)
        => map is not null && map.TryGetValue(deviceName, out var detector) ? detector : deviceName;

    private static bool _readFailureLogged;
}
