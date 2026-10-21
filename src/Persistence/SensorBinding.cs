using System;
using System.Collections.Generic;

namespace Nexus.Service.Persistence;

/// <summary>
/// A sensor reading mapped onto 0..1 for a lighting shader. Category and Sensor name the
/// reading the way the monitoring pickers do (see <c>SensorSnapshotResolver</c>); Min and Max
/// are in the sensor's own units. The same shape is the persisted form, the wire form and the
/// default a shader authors in its <c>// sensor {json}</c> annotation.
/// </summary>
public sealed class SensorBinding
{
    public string Category { get; set; } = "";
    public string Sensor { get; set; } = "";
    public float Min { get; set; }
    public float Max { get; set; } = 100f;

    /// <summary>True when the binding names a sensor and a finite, non-empty range.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUsable =>
        Category.Length > 0 && Sensor.Length > 0 && float.IsFinite(Min) && float.IsFinite(Max) && Max > Min;

    /// <summary>The reading as 0..1 over [Min, Max]; a missing or non-finite reading is 0.</summary>
    public float Normalize(float value)
    {
        if (!float.IsFinite(value) || !IsUsable)
        {
            return 0f;
        }
        return Math.Clamp((value - Min) / (Max - Min), 0f, 1f);
    }

    public SensorBinding Copy() => new() { Category = Category, Sensor = Sensor, Min = Min, Max = Max };

    /// <summary>Identity of the reading alone, so bindings that differ only in range share one poll.</summary>
    internal string FeedKey => Category + "\n" + Sensor;

    /// <summary>A deep copy of a sensor map, null for null or empty and with null entries dropped.</summary>
    public static Dictionary<string, SensorBinding>? CopyMap(IReadOnlyDictionary<string, SensorBinding>? map)
    {
        if (map is null || map.Count == 0)
        {
            return null;
        }
        var copy = new Dictionary<string, SensorBinding>(map.Count, StringComparer.Ordinal);
        foreach (var kv in map)
        {
            if (kv.Value is not null)
            {
                copy[kv.Key] = kv.Value.Copy();
            }
        }
        return copy.Count == 0 ? null : copy;
    }

    /// <summary>Same keys and same bindings; null and empty are equal.</summary>
    public static bool MapEquals(IReadOnlyDictionary<string, SensorBinding>? a, IReadOnlyDictionary<string, SensorBinding>? b)
    {
        var ac = a?.Count ?? 0;
        var bc = b?.Count ?? 0;
        if (ac != bc)
        {
            return false;
        }
        if (ac == 0)
        {
            return true;
        }
        foreach (var kv in a!)
        {
            if (!b!.TryGetValue(kv.Key, out var other) || !BindingEquals(kv.Value, other))
            {
                return false;
            }
        }
        return true;
    }

    private static bool BindingEquals(SensorBinding? x, SensorBinding? y) =>
        x is null || y is null
            ? ReferenceEquals(x, y)
            : x.Category == y.Category && x.Sensor == y.Sensor && x.Min.Equals(y.Min) && x.Max.Equals(y.Max);

    /// <summary>Canonical text of a map, ordered by key, for tags and cache keys. Empty for null or empty.</summary>
    public static string MapKey(IReadOnlyDictionary<string, SensorBinding>? map)
    {
        if (map is null || map.Count == 0)
        {
            return "";
        }
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder();
        foreach (var k in keys)
        {
            var b = map[k];
            sb.Append(';').Append(k).Append('=');
            if (b is not null)
            {
                sb.Append(b.Category).Append('/').Append(b.Sensor).Append('/')
                  .Append(b.Min.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
                  .Append(b.Max.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    /// <summary>The defaults overlaid with the overrides: an override replaces the default of the same uniform.</summary>
    public static Dictionary<string, SensorBinding>? Merge(
        IReadOnlyDictionary<string, SensorBinding>? baseMap, IReadOnlyDictionary<string, SensorBinding>? overrides)
    {
        if (baseMap is null || baseMap.Count == 0)
        {
            return CopyMap(overrides);
        }
        var merged = CopyMap(baseMap) ?? new Dictionary<string, SensorBinding>(StringComparer.Ordinal);
        if (overrides is not null)
        {
            foreach (var kv in overrides)
            {
                if (kv.Value is not null)
                {
                    merged[kv.Key] = kv.Value.Copy();
                }
            }
        }
        return merged.Count == 0 ? null : merged;
    }
}
