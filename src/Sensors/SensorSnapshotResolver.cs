using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>
/// The sensor sources <see cref="SensorSnapshotResolver"/> cannot read off
/// <see cref="ISensorProvider"/>'s cheap path, supplied by callers that
/// resolve several sensors in one pass. Every member is optional and the
/// default value (all null) reproduces the resolver's own lookups.
/// </summary>
/// <param name="Extras">
/// A pre-gathered snapshot for the extras-backed categories.
/// <see cref="ISensorProvider.GetSensorExtras"/> rebuilds every component on
/// each call, so a caller resolving a batch should gather it once.
/// </param>
/// <param name="FpsSensors">IFpsProvider's sensors; this class does not depend on that provider. Null resolves "fps" to nothing.</param>
/// <param name="NetworkSensors">
/// Opts "network" into an aggregate (see
/// <see cref="SensorSnapshotResolver.BuildNicNetworkSensors"/>) instead of the
/// per-adapter sensors. An EMPTY list is not the same as null: it means the
/// caller wants the aggregate and there is none, so the category resolves to
/// nothing rather than silently falling back to a per-adapter reading.
/// </param>
/// <param name="FanHeaderNames">
/// Cooling-page fan-header renames keyed by tach sensor id, from
/// <see cref="Nexus.Service.Monitoring.FanSensorNames.BuildMap"/>, null when nothing is
/// renamed. Callers that RENDER a sensor's name must pass it, or a renamed header reads
/// differently here than in the monitoring topic the pickers are built from. Applied to the
/// resolved sensor, never to the category list, so a key stored by the hardware name still
/// matches after a rename.
/// </param>
public readonly record struct SensorSnapshotSources(
    SensorExtras? Extras = null,
    IReadOnlyList<HardwareSensor>? FpsSensors = null,
    IReadOnlyList<HardwareSensor>? NetworkSensors = null,
    IReadOnlyDictionary<string, string>? FanHeaderNames = null);

/// <summary>
/// Resolves a single sensor by (category, sensorId) over an ISensorProvider
/// snapshot, sharing the category vocabulary the Tryx overlay picker and the
/// deck monitoring picker both use: the deck's whole DEVICE_OPTION_KEYS, of
/// which the Tryx overlay's SENSOR_CATEGORIES is a strict subset (it omits
/// smart and the extras-topic groups).
/// "gpu" scans every GPU's sensors since GPU sensor ids are unique across
/// GPUs on a single machine, so a linear scan across all of them is correct.
///
/// Categories that need a source beyond <see cref="ISensorProvider"/> take it
/// from <see cref="SensorSnapshotSources"/>; see that type for which and why.
/// </summary>
public static class SensorSnapshotResolver
{
    public static HardwareSensor? Resolve(
        ISensorProvider sensors,
        string category,
        string sensorId,
        SensorSnapshotSources sources = default)
    {
        var list = GetCategorySensors(sensors, category, sources);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == sensorId)
            {
                return Nexus.Service.Monitoring.FanSensorNames.WithRename(list[i], sources.FanHeaderNames);
            }
        }
        return null;
    }

    /// <summary>
    /// Mirrors nexus-web's MonitoringWidget resolveSensor, so a tile renders
    /// the same reading on the deck as in the editor
    /// when the stored id is stale or was never concrete (an imported
    /// third-party profile, a swapped GPU). Two shapes are load-bearing: an
    /// empty id goes straight to the category default, since matching it would
    /// let a nameless sensor outrank that default; and with a non-empty id cpu
    /// and memory stop at their named default rather than the first sensor.
    /// Returns null where resolveSensor returns undefined - the caller keeps
    /// the unresolved placeholder, matching the editor.
    /// </summary>
    public static HardwareSensor? ResolveOrDefault(
        ISensorProvider sensors,
        string category,
        string sensorId,
        SensorSnapshotSources sources = default)
    {
        var match = ResolveOrDefaultRaw(sensors, category, sensorId, sources);
        return match is null
            ? null
            : Nexus.Service.Monitoring.FanSensorNames.WithRename(match, sources.FanHeaderNames);
    }

    /// <summary>
    /// The lookup itself, against hardware names, so a key stored before a rename still
    /// matches. A key stored AFTER one holds the custom name, which no hardware sensor
    /// carries, so that is tried too before falling through to the category default.
    /// </summary>
    private static HardwareSensor? ResolveOrDefaultRaw(
        ISensorProvider sensors,
        string category,
        string sensorId,
        SensorSnapshotSources sources)
    {
        var list = GetCategorySensors(sensors, category, sources);
        if (list.Count == 0)
        {
            return null;
        }
        if (!string.IsNullOrEmpty(sensorId))
        {
            var match = FindById(list, sensorId)
                ?? FindByName(list, sensorId)
                ?? FindByFanHeaderName(list, sensorId, sources.FanHeaderNames);
            if (match is not null)
            {
                return match;
            }
            return category switch
            {
                "cpu" => FindByName(list, "CPU Total"),
                "memory" => FindByName(list, "Memory Usage"),
                "gpu" => FindGpuCoreLoad(list) ?? list[0],
                // fps has no default: resolveSensor's fps branch returns
                // undefined for an unmatched key, so falling back here would
                // paint a reading on the physical key that the touch tile
                // renders as "--".
                "fps" => null,
                _ => list[0],
            };
        }
        return category switch
        {
            "cpu" => FindByName(list, "CPU Total") ?? list[0],
            "gpu" => FindGpuCoreLoad(list) ?? list[0],
            "memory" => FindByName(list, "Memory Usage") ?? list[0],
            _ => list[0],
        };
    }

    /// <summary>Matches a key that holds a renamed header's custom name back to its tach sensor.</summary>
    private static HardwareSensor? FindByFanHeaderName(
        IReadOnlyList<HardwareSensor> list, string name, IReadOnlyDictionary<string, string>? byRpmSensor)
    {
        if (byRpmSensor is null) return null;
        foreach (var pair in byRpmSensor)
        {
            if (!string.Equals(pair.Value, name, StringComparison.Ordinal)) continue;
            var match = FindById(list, pair.Key);
            if (match is not null) return match;
        }
        return null;
    }

    private static HardwareSensor? FindById(IReadOnlyList<HardwareSensor> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id)
            {
                return list[i];
            }
        }
        return null;
    }

    private static HardwareSensor? FindByName(IReadOnlyList<HardwareSensor> list, string name)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Name == name)
            {
                return list[i];
            }
        }
        return null;
    }

    private static HardwareSensor? FindGpuCoreLoad(IReadOnlyList<HardwareSensor> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Name == "GPU Core" && list[i].Type == "Load")
            {
                return list[i];
            }
        }
        return null;
    }

    public static IReadOnlyList<HardwareSensor> GetCategorySensors(
        ISensorProvider sensors,
        string category,
        SensorSnapshotSources sources = default) => category switch
    {
        "quick" => SummarySensors.Build(sensors),
        "cpu" => sensors.GetCpuSensors(),
        "gpu" => FlattenGpus(sensors.GetGpus()),
        "memory" => sensors.GetMemorySensors(),
        "motherboard" => sensors.GetMotherboardSensors(),
        // includeSmart: false matches both callers' pickers - the Tryx overlay
        // and the deck monitoring key each source "storage" from nexus-web's
        // storageSensors, which filters smart/* out. A deck key that wants a
        // SMART reading stores the separate "smart" category below.
        "storage" => FlattenComponents(sensors.GetStorageComponents(includeSmart: false).Values),
        "smart" => SmartStorageSensors(sensors),
        "memoryModule" => FlattenComponents(Extras(sensors, sources).MemoryModules),
        "battery" => FlattenComponents(Extras(sensors, sources).Batteries),
        "cooler" => FlattenComponents(Extras(sensors, sources).Coolers),
        "psu" => FlattenComponents(Extras(sensors, sources).Psus),
        "embeddedController" => FlattenComponents(Extras(sensors, sources).EmbeddedControllers),
        "network" => sources.NetworkSensors ?? FlattenComponents(Extras(sensors, sources).Nics),
        "fps" => sources.FpsSensors ?? (IReadOnlyList<HardwareSensor>)Array.Empty<HardwareSensor>(),
        _ => Array.Empty<HardwareSensor>(),
    };

    /// <summary>Categories <see cref="GetCategorySensors"/> reads out of <see cref="SensorExtras"/>, so a caller knows when gathering it is worth the walk. Mirrors nexus-web's deckCategoryUsesExtras.</summary>
    public static bool CategoryUsesExtras(string category) =>
        category is "network" or "memoryModule" or "battery" or "cooler" or "psu" or "embeddedController";

    /// <summary>Categories whose sensors come from IFpsProvider, which this class does not depend on. Mirrors nexus-web's deckCategoryUsesFps.</summary>
    public static bool CategoryUsesFps(string category) => category == "fps";

    private static SensorExtras Extras(ISensorProvider sensors, SensorSnapshotSources sources) =>
        sources.Extras ?? sensors.GetSensorExtras();

    /// <summary>The "smart/"-prefixed components only, mirroring nexus-web's smartStorageSensors.</summary>
    private static IReadOnlyList<HardwareSensor> SmartStorageSensors(ISensorProvider sensors)
    {
        var list = new List<HardwareSensor>();
        foreach (var entry in sensors.GetStorageComponents(includeSmart: true))
        {
            if (LhmComponentIdentifiers.IsSmartStorageComponent(entry.Key))
            {
                list.AddRange(entry.Value.Sensors);
            }
        }
        return list;
    }

    /// <summary>
    /// The Network Total / In / Out aggregate a deck monitoring key resolves,
    /// summed over every adapter's throughput sensors. Mirrors nexus-web's
    /// buildNicNetworkSensors (networkSensors.ts) - same ids, names and
    /// Formatted shape - so the physical key and the touch tile show the same
    /// reading; the two sum in float and double respectively, so the last
    /// displayed digit can still differ. Direction comes from the
    /// sensor name, the only discriminator both providers share:
    /// LibreHardwareMonitor names them "Download Speed" / "Upload Speed",
    /// LinuxSensorProvider "&lt;iface&gt; RX" / "&lt;iface&gt; TX". Adapters
    /// reporting no throughput sensor at all, or only a non-directional one
    /// ("Network Utilization"), yield an empty result, which hides the
    /// category from the picker instead of offering three flat zeros.
    /// </summary>
    internal static IReadOnlyList<HardwareSensor> BuildNicNetworkSensors(IReadOnlyList<HardwareComponent> nics)
    {
        float bytesIn = 0f;
        float bytesOut = 0f;
        var matched = false;
        for (var i = 0; i < nics.Count; i++)
        {
            var sensors = nics[i].Sensors;
            for (var j = 0; j < sensors.Count; j++)
            {
                var sensor = sensors[j];
                if (!string.Equals(sensor.Type, "Throughput", StringComparison.Ordinal))
                {
                    continue;
                }
                var direction = NicThroughputDirection(sensor.Name);
                if (direction == 0)
                {
                    continue;
                }
                // The adapter counts as present even when this reading is
                // unusable, so one bad NIC hides neither the category nor the
                // other adapters' rates.
                matched = true;
                var value = float.IsFinite(sensor.Value) ? Math.Max(0f, sensor.Value) : 0f;
                if (direction < 0)
                {
                    bytesIn += value;
                }
                else
                {
                    bytesOut += value;
                }
            }
        }
        if (!matched)
        {
            return Array.Empty<HardwareSensor>();
        }
        return new List<HardwareSensor>
        {
            NetworkSensor("network-total", "Network Total", bytesIn + bytesOut),
            NetworkSensor("network-in", "Network In", bytesIn),
            NetworkSensor("network-out", "Network Out", bytesOut),
        };
    }

    /// <summary>-1 inbound, 1 outbound, 0 for a throughput sensor that is neither (e.g. "Network Utilization").</summary>
    private static int NicThroughputDirection(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("download", StringComparison.Ordinal) || lower.EndsWith(" rx", StringComparison.Ordinal))
        {
            return -1;
        }
        if (lower.Contains("upload", StringComparison.Ordinal) || lower.EndsWith(" tx", StringComparison.Ordinal))
        {
            return 1;
        }
        return 0;
    }

    /// <summary>Mirrors networkSensors.ts' networkSensor + formatNetworkRate.</summary>
    private static HardwareSensor NetworkSensor(string id, string name, float value)
    {
        var safe = float.IsFinite(value) && value > 0f ? value : 0f;
        return new HardwareSensor
        {
            Id = id,
            Name = name,
            Type = "Rate",
            Value = safe,
            Units = "B/s",
            Formatted = FormatNetworkRate(safe),
            Parent = new SensorParent { Id = "network", Name = "Network" },
        };
    }

    private static readonly string[] NetworkRateUnits = { "KB/s", "MB/s", "GB/s", "TB/s" };

    private static string FormatNetworkRate(float bytesPerSecond)
    {
        var value = Math.Max(0f, bytesPerSecond);
        if (value < 1024f)
        {
            return $"{Math.Round((double)value, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)} B/s";
        }

        var scaled = value / 1024f;
        var unitIndex = 0;
        while (scaled >= 1024f && unitIndex < NetworkRateUnits.Length - 1)
        {
            scaled /= 1024f;
            unitIndex++;
        }

        var decimals = scaled < 10f ? 1 : 0;
        var rounded = Math.Round((double)scaled, decimals, MidpointRounding.AwayFromZero);
        return $"{rounded.ToString("F" + decimals, CultureInfo.InvariantCulture)} {NetworkRateUnits[unitIndex]}";
    }

    private static IReadOnlyList<HardwareSensor> FlattenGpus(IReadOnlyList<GpuReadout> gpus)
    {
        if (gpus.Count == 0)
        {
            return System.Array.Empty<HardwareSensor>();
        }
        if (gpus.Count == 1)
        {
            return gpus[0].Sensors;
        }
        var list = new List<HardwareSensor>();
        foreach (var gpu in gpus)
        {
            list.AddRange(gpu.Sensors);
        }
        return list;
    }

    private static IReadOnlyList<HardwareSensor> FlattenComponents(IEnumerable<HardwareComponent> components)
    {
        var list = new List<HardwareSensor>();
        foreach (var component in components)
        {
            list.AddRange(component.Sensors);
        }
        return list;
    }
}
