using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
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

    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);
    private static bool _readFailureLogged;
}
