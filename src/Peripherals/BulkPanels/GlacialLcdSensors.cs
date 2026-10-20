using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>Turns a saved widget into the label and value text the panel shows.</summary>
public static class GlacialLcdSensors
{
    public static (string Label, string Value) Read(ISensorProvider sensors, GlacialLcdSlot slot, int index)
    {
        var sensor = Find(sensors, slot, index);
        string label = slot.Label.Length > 0 ? slot.Label : sensor?.Name ?? "";
        return (label, sensor is null ? GlacialLcdScreen.NoReading : GlacialLcdScreen.FormatValue(sensor.Type, sensor.Value));
    }

    private static HardwareSensor? Find(ISensorProvider sensors, GlacialLcdSlot slot, int index)
    {
        if (slot.Sensor.Length > 0)
        {
            return SensorSnapshotResolver.Resolve(sensors, slot.Category, slot.Sensor);
        }
        // Unpicked: the first temperature of the slot's category, the package reading for the second widget.
        string category = slot.Category.Length > 0 ? slot.Category : index == 2 ? "gpu" : "cpu";
        var list = SensorSnapshotResolver.GetCategorySensors(sensors, category);
        HardwareSensor? first = null;
        foreach (var candidate in list)
        {
            if (candidate.Type != "Temperature")
            {
                continue;
            }
            first ??= candidate;
            if (index == 1 && candidate.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        return first;
    }
}
