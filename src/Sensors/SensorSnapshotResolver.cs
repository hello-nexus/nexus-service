using System.Collections.Generic;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>
/// Resolves a single sensor by (category, sensorId) over an ISensorProvider
/// snapshot, sharing the category vocabulary the Tryx overlay picker and the
/// deck monitoring picker both use (a superset - "quick" is deck-only,
/// "network" is Tryx-only per each caller's own category list). "gpu" scans
/// every GPU's sensors since GPU sensor ids are unique across GPUs on a
/// single machine, so a linear scan across all of them is correct.
/// </summary>
public static class SensorSnapshotResolver
{
    public static HardwareSensor? Resolve(ISensorProvider sensors, string category, string sensorId)
    {
        var list = GetCategorySensors(sensors, category);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == sensorId)
            {
                return list[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Mirrors nexus-web's MonitoringWidget resolveSensor (MonitoringWidget.tsx
    /// :51-98), so a tile renders the same reading on the deck as in the editor
    /// when the stored id is stale or was never concrete (an imported
    /// third-party profile, a swapped GPU). Two shapes are load-bearing: an
    /// empty id goes straight to the category default, since matching it would
    /// let a nameless sensor outrank that default; and with a non-empty id cpu
    /// and memory stop at their named default rather than the first sensor.
    /// Returns null where resolveSensor returns undefined - the caller keeps
    /// the unresolved placeholder, matching the editor.
    /// </summary>
    public static HardwareSensor? ResolveOrDefault(ISensorProvider sensors, string category, string sensorId)
    {
        var list = GetCategorySensors(sensors, category);
        if (list.Count == 0)
        {
            return null;
        }
        if (!string.IsNullOrEmpty(sensorId))
        {
            var match = FindById(list, sensorId) ?? FindByName(list, sensorId);
            if (match is not null)
            {
                return match;
            }
            return category switch
            {
                "cpu" => FindByName(list, "CPU Total"),
                "memory" => FindByName(list, "Memory Usage"),
                "gpu" => FindGpuCoreLoad(list) ?? list[0],
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

    public static IReadOnlyList<HardwareSensor> GetCategorySensors(ISensorProvider sensors, string category) => category switch
    {
        "quick" => SummarySensors.Build(sensors),
        "cpu" => sensors.GetCpuSensors(),
        "gpu" => FlattenGpus(sensors.GetGpus()),
        "memory" => sensors.GetMemorySensors(),
        "motherboard" => sensors.GetMotherboardSensors(),
        // includeSmart: false matches both callers' pickers - the Tryx overlay
        // and the deck monitoring key each source "storage" from nexus-web's
        // storageSensors, which filters smart/* out, so neither can hold a
        // SMART sensor id to resolve.
        "storage" => FlattenComponents(sensors.GetStorageComponents(includeSmart: false).Values),
        "network" => FlattenComponents(sensors.GetSensorExtras().Nics),
        _ => System.Array.Empty<HardwareSensor>(),
    };

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
