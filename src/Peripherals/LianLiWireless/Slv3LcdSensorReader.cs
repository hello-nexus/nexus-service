using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>One sensor's display-ready reading: an already unit-converted value plus the gauge scale and labels.</summary>
public readonly record struct Slv3LcdSensorReading(float Value, float Min, float Max, string Label, string Unit, string ValueType = "");

/// <summary>
/// Reads one named live sensor value from <see cref="ISensorProvider"/> for
/// the LCD sensor content renderer. CPU/GPU values come from
/// <see cref="SummarySensors"/> so this reader and the "summary" broadcast
/// topic pick the same underlying sensor: gpuLoad/gpuTemp now prefer a
/// "Core"-named sensor over the first Load/Temperature sensor on the GPU,
/// matching TryxPanoramaHub's convention. GPU sources read 0 on hosts where
/// LHM exposes no GPU sensors (a known service-wide limitation); CPU
/// load/temp and fan RPM are the reliable live values.
/// </summary>
public sealed class Slv3LcdSensorReader
{
    private const float FanRpmCeiling = 3000f;

    private readonly ISensorProvider _sensors;

    public Slv3LcdSensorReader(ISensorProvider sensors)
    {
        _sensors = sensors;
    }

    public Slv3LcdSensorReading Read(string? source, string? tempUnit)
    {
        var fahrenheit = string.Equals(tempUnit, "f", StringComparison.OrdinalIgnoreCase);
        return source switch
        {
            "cpuLoad" => new Slv3LcdSensorReading(SummarySensors.Value(_sensors, SummarySensorKind.CpuUsage) ?? 0f, 0f, 100f, "CPU", "%", "Load"),
            "gpuLoad" => new Slv3LcdSensorReading(SummarySensors.Value(_sensors, SummarySensorKind.GpuUsage) ?? 0f, 0f, 100f, "GPU", "%", "Load"),
            "gpuTemp" => TempReading(SummarySensors.Value(_sensors, SummarySensorKind.GpuTemp) ?? 0f, "GPU", fahrenheit),
            "memoryUsage" => new Slv3LcdSensorReading(SummarySensors.Value(_sensors, SummarySensorKind.MemoryUsage) ?? 0f, 0f, 100f, "MEM", "%", "Load"),
            "vramUsage" => new Slv3LcdSensorReading(SummarySensors.Value(_sensors, SummarySensorKind.VramUsage) ?? 0f, 0f, 100f, "VRAM", "%", "Load"),
            "fanRpm" => FanReading(),
            // "cpuTemp" and any unrecognized/missing source default to CPU temperature.
            _ => TempReading(SummarySensors.Value(_sensors, SummarySensorKind.CpuTemp) ?? 0f, "CPU", fahrenheit),
        };
    }

    private Slv3LcdSensorReading FanReading()
    {
        var mobo = _sensors.GetMotherboardSensors();
        var max = 0f;
        for (var i = 0; i < mobo.Count; i++)
        {
            if (string.Equals(mobo[i].Type, "Fan", StringComparison.OrdinalIgnoreCase) && mobo[i].Value > max)
            {
                max = mobo[i].Value;
            }
        }
        return new Slv3LcdSensorReading(max, 0f, FanRpmCeiling, "FAN", "RPM", "Fan");
    }

    private static Slv3LcdSensorReading TempReading(float celsius, string label, bool fahrenheit)
    {
        if (!fahrenheit)
        {
            return new Slv3LcdSensorReading(celsius, 0f, 100f, label, "°C", "Temperature");
        }
        var fahrenheitValue = celsius * 9f / 5f + 32f;
        return new Slv3LcdSensorReading(fahrenheitValue, 32f, 212f, label, "°F", "Temperature");
    }
}
