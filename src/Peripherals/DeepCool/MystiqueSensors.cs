using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>The readouts the screen takes from Nexus sensors.</summary>
public enum MystiqueValue
{
    CpuTemperature,
    CpuFrequency,
    CpuUsage,
    RamUsage,
    CpuFan,
    Pump,
}

/// <summary>A sensor chosen for a readout and the monitoring category it was found in.</summary>
public readonly record struct MystiqueFound(string Category, HardwareSensor Sensor);

/// <summary>Builds the screen's reading from Nexus's own sensors.</summary>
public static class MystiqueSensors
{
    public static MystiqueReading Read(
        ISensorProvider sensors, DeepCoolMystiqueSettings settings, IReadOnlyDictionary<string, string>? fanNames = null)
    {
        var ctx = new Context(sensors, fanNames);
        return new MystiqueReading(
            TempC: Round(Value(ctx, settings.CpuTemperature, MystiqueValue.CpuTemperature)),
            UsagePct: Round(Value(ctx, settings.CpuUsage, MystiqueValue.CpuUsage)),
            RamPct: Round(Value(ctx, settings.RamUsage, MystiqueValue.RamUsage)),
            CpuMhz: Round(Value(ctx, settings.CpuFrequency, MystiqueValue.CpuFrequency)),
            FanRpm: Round(Value(ctx, settings.CpuFan, MystiqueValue.CpuFan)),
            PumpRpm: Round(Value(ctx, settings.Pump, MystiqueValue.Pump)));
    }

    /// <summary>The sensor type a picked sensor must have for <paramref name="value"/>.</summary>
    public static string RequiredType(MystiqueValue value) => value switch
    {
        MystiqueValue.CpuTemperature => "Temperature",
        MystiqueValue.CpuFrequency => "Clock",
        MystiqueValue.CpuUsage or MystiqueValue.RamUsage => "Load",
        _ => "Fan",
    };

    /// <summary>
    /// The picked sensor, or with none picked the automatic choice. Null when nothing matches, so a
    /// missing sensor reads 0 rather than another sensor's value. Fan headers the user renamed on the
    /// cooling page match by their new name.
    /// </summary>
    public static MystiqueFound? Find(
        ISensorProvider sensors, DeepCoolMystiqueSensorPick pick, MystiqueValue value, IReadOnlyDictionary<string, string>? fanNames = null) =>
        Find(new Context(sensors, fanNames), pick, value);

    private static MystiqueFound? Find(Context ctx, DeepCoolMystiqueSensorPick pick, MystiqueValue value)
    {
        if (!string.IsNullOrEmpty(pick.Sensor))
        {
            var picked = SensorSnapshotResolver.ResolveExact(
                ctx.Sensors, pick.Category, pick.Sensor, new SensorSnapshotSources(null, null, null, ctx.FanNames));
            return picked is not null && picked.Type == RequiredType(value) ? new MystiqueFound(pick.Category, picked) : null;
        }
        return value switch
        {
            MystiqueValue.CpuTemperature => Summary(ctx, "summary/cpu-temp"),
            MystiqueValue.CpuUsage => Summary(ctx, "summary/cpu-usage"),
            MystiqueValue.RamUsage => Summary(ctx, "summary/memory-usage"),
            MystiqueValue.CpuFrequency => HighestCoreClock(ctx) ?? Summary(ctx, "summary/cpu-clock"),
            MystiqueValue.CpuFan => Header(ctx, pump: false),
            _ => Header(ctx, pump: true),
        };
    }

    /// <summary>The value in the unit the screen takes: frequency in MHz whatever unit the sensor reports.</summary>
    private static float? Value(Context ctx, DeepCoolMystiqueSensorPick pick, MystiqueValue value)
    {
        var found = Find(ctx, pick, value);
        if (found is null)
        {
            return null;
        }
        var s = found.Value.Sensor;
        return value == MystiqueValue.CpuFrequency && s.Units.Contains("GHz", StringComparison.OrdinalIgnoreCase) ? s.Value * 1000f : s.Value;
    }

    private static MystiqueFound? Summary(Context ctx, string id)
    {
        foreach (var sensor in ctx.Quick)
        {
            if (sensor.Id == id)
            {
                return new MystiqueFound("quick", sensor);
            }
        }
        return null;
    }

    /// <summary>
    /// The highest per-core clock, which tracks the boost the CPU reaches. The summary clock is the mean
    /// across every core, which an idle or parked core drags far below it.
    /// </summary>
    private static MystiqueFound? HighestCoreClock(Context ctx)
    {
        foreach (var sensor in ctx.Cpu)
        {
            if (sensor.Type == "Clock" && sensor.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
            {
                return new MystiqueFound("cpu", sensor);
            }
        }
        return null;
    }

    /// <summary>The motherboard header named for the CPU fan, or for the pump (CPU_OPT or a pump header).</summary>
    private static MystiqueFound? Header(Context ctx, bool pump)
    {
        foreach (var sensor in ctx.Board)
        {
            if (sensor.Type != "Fan")
            {
                continue;
            }
            bool pumpHeader = Mentions(sensor.Name, "OPT") || Mentions(sensor.Name, "PUMP") || Mentions(sensor.Name, "AIO");
            if (pump ? pumpHeader : Mentions(sensor.Name, "CPU") && !pumpHeader)
            {
                return new MystiqueFound("motherboard", sensor);
            }
        }
        return null;
    }

    /// <summary>Sensor lists read once per reading and only when an automatic readout needs them.</summary>
    private sealed class Context
    {
        private IReadOnlyList<HardwareSensor>? _quick;
        private IReadOnlyList<HardwareSensor>? _cpu;
        private IReadOnlyList<HardwareSensor>? _board;

        public Context(ISensorProvider sensors, IReadOnlyDictionary<string, string>? fanNames)
        {
            Sensors = sensors;
            FanNames = fanNames;
        }

        public ISensorProvider Sensors { get; }
        public IReadOnlyDictionary<string, string>? FanNames { get; }
        public IReadOnlyList<HardwareSensor> Quick => _quick ??= SummarySensors.Build(Sensors);
        public IReadOnlyList<HardwareSensor> Cpu => _cpu ??= Sensors.GetCpuSensors();
        public IReadOnlyList<HardwareSensor> Board => _board ??= FanSensorNames.WithRenames(Sensors.GetMotherboardSensors(), FanNames);
    }

    /// <summary>The cooling page's header renames keyed by tach sensor id, or null when none apply.</summary>
    public static IReadOnlyDictionary<string, string>? FanNames(IConfigStore store, Nexus.Service.Cooling.IFanControlProvider? fans)
    {
        if (fans is null)
        {
            return null;
        }
        Dictionary<string, string>? names = null;
        store.Read(s => names = new Dictionary<string, string>(s.Cooling.FanNames));
        if (names is null || names.Count == 0)
        {
            return null;
        }
        try
        {
            return FanSensorNames.BuildMap(fans.GetFanChannels(), names);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[deepcool-mystique] fan header names skipped: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool Mentions(string name, string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Non-finite guard: a hardware sensor can report NaN or Infinity.</summary>
    private static int Round(float? v) => v is { } f && float.IsFinite(f) ? (int)MathF.Round(f) : 0;
}
