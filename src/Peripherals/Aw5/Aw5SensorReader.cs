using System;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.Aw5;

/// <summary>What the panels show, already rounded to the whole units both wire formats carry.</summary>
public readonly record struct Aw5PanelReading(int TempC, int LoadPct, int Mhz);

/// <summary>
/// Builds a panel reading from live sensors. CPU values come from
/// <see cref="SummarySensors"/> so the panels and the "summary" broadcast topic pick
/// the same underlying sensor.
/// </summary>
public sealed class Aw5SensorReader
{
    private readonly ISensorProvider _sensors;

    public Aw5SensorReader(ISensorProvider sensors)
    {
        _sensors = sensors;
    }

    public Aw5PanelReading Read()
    {
        return new Aw5PanelReading(
            TempC: Round(SummarySensors.Value(_sensors, SummarySensorKind.CpuTemp) ?? 0f),
            LoadPct: Round(SummarySensors.Value(_sensors, SummarySensorKind.CpuUsage) ?? 0f),
            Mhz: Round(SummarySensors.Value(_sensors, SummarySensorKind.CpuClock) ?? 0f));
    }

    /// <summary>Non-finite guard: a hardware sensor can report NaN/Infinity, which would round to a wild digit.</summary>
    private static int Round(float v) => float.IsFinite(v) ? (int)MathF.Round(v) : 0;

}
