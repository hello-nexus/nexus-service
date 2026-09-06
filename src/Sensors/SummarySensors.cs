using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>Which "Quick" summary reading a caller wants derived from live sensors.</summary>
public enum SummarySensorKind
{
    CpuTemp,
    CpuUsage,
    CpuClock,
    GpuTemp,
    GpuUsage,
    GpuClock,
    MemoryUsage,
    VramUsage,
}

/// <summary>
/// Single derivation of the "Quick" summary sensor set (CPU temp/usage/clock, GPU
/// temp/usage/clock, memory usage, VRAM usage), shared by the summary WebSocket
/// topic, the Lian Li wireless LCD reader and the AW5 pump displays so they all pick
/// the same underlying sensor. Most kinds clone a live sensor; VramUsage is computed
/// (used / total) since LHM exposes VRAM as SmallData used+total, not a fill-percent
/// Load sensor.
/// </summary>
public static class SummarySensors
{
    private const string SummaryComponentId = "summary";
    private const string SummaryComponentName = "Quick";

    /// <summary>Builds the summary sensor list in fixed order (CpuTemp, CpuUsage,
    /// CpuClock, GpuTemp, GpuUsage, GpuClock, MemoryUsage, VramUsage), omitting any kind
    /// whose source sensor is absent.</summary>
    public static List<HardwareSensor> Build(ISensorProvider sensors)
        => BuildFrom(sensors.GetCpuSensors(), PrimaryGpuSensors(sensors), sensors.GetMemorySensors());

    /// <summary>Value of the summary reading for <paramref name="kind"/> (a raw sensor value,
    /// or the computed fill percent for VramUsage), or null if the source is absent.</summary>
    public static float? Value(ISensorProvider sensors, SummarySensorKind kind)
        => Resolve(sensors.GetCpuSensors(), PrimaryGpuSensors(sensors), sensors.GetMemorySensors(), kind)?.Value;

    /// <summary>Same derivation as <see cref="Value"/>, applied to sensor lists the
    /// caller already fetched; a kind that reads only one list (CpuTemp reads cpu) can
    /// be passed empty lists for the others.</summary>
    internal static float? ValueFrom(
        IReadOnlyList<HardwareSensor> cpuSensors,
        IReadOnlyList<HardwareSensor> gpuSensors,
        IReadOnlyList<HardwareSensor> memorySensors,
        SummarySensorKind kind)
        => kind == SummarySensorKind.VramUsage
            ? VramUsagePercent(gpuSensors)
            : Pick(cpuSensors, gpuSensors, memorySensors, kind)?.Value;

    /// <summary>Same derivation as <see cref="Build(ISensorProvider)"/>, applied to sensor
    /// lists a caller already fetched, so a caller that has already read cpu/gpu/memory
    /// sensors this tick does not trigger a second underlying read for the summary set.</summary>
    internal static List<HardwareSensor> BuildFrom(
        IReadOnlyList<HardwareSensor> cpuSensors,
        IReadOnlyList<HardwareSensor> gpuSensors,
        IReadOnlyList<HardwareSensor> memorySensors)
    {
        var result = new List<HardwareSensor>(8);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.CpuTemp);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.CpuUsage);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.CpuClock);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.GpuTemp);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.GpuUsage);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.GpuClock);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.MemoryUsage);
        AddIfPresent(result, cpuSensors, gpuSensors, memorySensors, SummarySensorKind.VramUsage);
        return result;
    }

    private static void AddIfPresent(
        List<HardwareSensor> result,
        IReadOnlyList<HardwareSensor> cpuSensors,
        IReadOnlyList<HardwareSensor> gpuSensors,
        IReadOnlyList<HardwareSensor> memorySensors,
        SummarySensorKind kind)
    {
        var sensor = Resolve(cpuSensors, gpuSensors, memorySensors, kind);
        if (sensor is not null)
        {
            result.Add(sensor);
        }
    }

    private static HardwareSensor? Resolve(
        IReadOnlyList<HardwareSensor> cpuSensors,
        IReadOnlyList<HardwareSensor> gpuSensors,
        IReadOnlyList<HardwareSensor> memorySensors,
        SummarySensorKind kind)
    {
        if (kind == SummarySensorKind.VramUsage)
        {
            var percent = VramUsagePercent(gpuSensors);
            return percent is null ? null : SynthPercent(kind, percent.Value);
        }
        var source = Pick(cpuSensors, gpuSensors, memorySensors, kind);
        return source is null ? null : Clone(source, kind);
    }

    private static HardwareSensor? Pick(
        IReadOnlyList<HardwareSensor> cpuSensors,
        IReadOnlyList<HardwareSensor> gpuSensors,
        IReadOnlyList<HardwareSensor> memorySensors,
        SummarySensorKind kind) => kind switch
    {
        SummarySensorKind.CpuTemp => PreferElseFirst(cpuSensors, "Temperature", "Package"),
        SummarySensorKind.CpuUsage => PreferElseFirst(cpuSensors, "Load", "CPU Total"),
        // The mean across cores, synthesized by CpuClockAggregates: LHM exposes only
        // per-core clocks. Not PreferElseFirst - falling back to "the first Clock
        // sensor" would land on Bus Speed (~100 MHz) and read as a dead CPU.
        SummarySensorKind.CpuClock => FindSensor(cpuSensors, "Clock", "Core Average"),
        SummarySensorKind.GpuTemp => PreferElseFirst(gpuSensors, "Temperature", "Core"),
        SummarySensorKind.GpuUsage => PreferElseFirst(gpuSensors, "Load", "Core"),
        // A GPU reports one core clock, so there is no mean to take. No fallback, for
        // the same reason as CpuClock: the first Clock sensor may be memory or shader.
        SummarySensorKind.GpuClock => FindSensor(gpuSensors, "Clock", "Core"),
        SummarySensorKind.MemoryUsage => FindSensor(memorySensors, "Load", null),
        _ => null,
    };

    // VRAM fill percent = GPU Memory Used / its total. The provider sets the used
    // sensor's TheoreticalMaximum to total VRAM (LibreHardwareSensorProvider.GetGpus,
    // LinuxSensorProvider), so the ratio is unit-independent (MB on Windows, GB on Linux).
    private static float? VramUsagePercent(IReadOnlyList<HardwareSensor> gpuSensors)
    {
        var used = FindSensor(gpuSensors, "SmallData", "Used");
        if (used is null || used.TheoreticalMaximum <= 0f)
        {
            return null;
        }
        // Clamp to the 100 ceiling the synthesized sensor advertises; a degenerate
        // used > total reading would otherwise overflow a 0-100 gauge.
        return Math.Min(100f, used.Value / used.TheoreticalMaximum * 100f);
    }

    private static HardwareSensor SynthPercent(SummarySensorKind kind, float percent) => new()
    {
        Id = CanonicalId(kind),
        Name = CanonicalName(kind),
        Type = "Load",
        Value = percent,
        TheoreticalMaximum = 100f,
        Units = "%",
        Formatted = percent.ToString("F0", CultureInfo.InvariantCulture) + "%",
        Parent = new SensorParent { Id = SummaryComponentId, Name = SummaryComponentName },
    };

    private static HardwareSensor Clone(HardwareSensor source, SummarySensorKind kind) => new()
    {
        Id = CanonicalId(kind),
        Name = CanonicalName(kind),
        Type = source.Type,
        Value = source.Value,
        Min = source.Min,
        Max = source.Max,
        Average = source.Average,
        Usage = source.Usage,
        TheoreticalMaximum = source.TheoreticalMaximum,
        Units = source.Units,
        Formatted = source.Formatted,
        FormattedMax = source.FormattedMax,
        FormattedMin = source.FormattedMin,
        FormattedAverage = source.FormattedAverage,
        FormattedUsage = source.FormattedUsage,
        Parent = new SensorParent { Id = SummaryComponentId, Name = SummaryComponentName },
    };

    private static string CanonicalId(SummarySensorKind kind) => kind switch
    {
        SummarySensorKind.CpuTemp => "summary/cpu-temp",
        SummarySensorKind.CpuUsage => "summary/cpu-usage",
        SummarySensorKind.CpuClock => "summary/cpu-clock",
        SummarySensorKind.GpuTemp => "summary/gpu-temp",
        SummarySensorKind.GpuUsage => "summary/gpu-usage",
        SummarySensorKind.GpuClock => "summary/gpu-clock",
        SummarySensorKind.MemoryUsage => "summary/memory-usage",
        SummarySensorKind.VramUsage => "summary/vram-usage",
        _ => "",
    };

    private static string CanonicalName(SummarySensorKind kind) => kind switch
    {
        SummarySensorKind.CpuTemp => "CPU Temperature",
        SummarySensorKind.CpuUsage => "CPU Usage",
        SummarySensorKind.CpuClock => "CPU Clock",
        SummarySensorKind.GpuTemp => "GPU Temperature",
        SummarySensorKind.GpuUsage => "GPU Usage",
        SummarySensorKind.GpuClock => "GPU Clock",
        SummarySensorKind.MemoryUsage => "Memory Usage",
        SummarySensorKind.VramUsage => "VRAM Usage",
        _ => "",
    };

    private static IReadOnlyList<HardwareSensor> PrimaryGpuSensors(ISensorProvider sensors)
    {
        var gpus = sensors.GetGpus();
        for (var i = 0; i < gpus.Count; i++)
        {
            if (!gpus[i].Integrated)
            {
                return gpus[i].Sensors;
            }
        }
        return gpus.Count > 0 ? gpus[0].Sensors : Array.Empty<HardwareSensor>();
    }

    private static HardwareSensor? PreferElseFirst(IReadOnlyList<HardwareSensor> sensors, string type, string nameContains)
        => FindSensor(sensors, type, nameContains) ?? FindSensor(sensors, type, null);

    private static HardwareSensor? FindSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
    {
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (nameContains is null || s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }
}
