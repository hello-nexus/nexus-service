using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>Turns a saved widget into the label and value text the panel shows.</summary>
public static class GlacialLcdSensors
{
    public static (string Label, string Value) Read(ISensorProvider sensors, GlacialLcdSlot slot, int index, SensorSnapshotSources sources = default)
    {
        var sensor = Find(sensors, slot, index, sources);
        string label = !string.IsNullOrEmpty(slot.Label) ? slot.Label : sensor?.Name ?? "";
        return (label, sensor is null ? GlacialLcdScreen.NoReading : GlacialLcdScreen.FormatValue(sensor.Type, sensor.Value));
    }

    /// <summary>The category an unpicked widget reads from.</summary>
    public static string DefaultCategory(GlacialLcdSlot slot, int index) =>
        !string.IsNullOrEmpty(slot.Category) ? slot.Category : index == 2 ? "gpu" : "cpu";

    /// <summary>The sensor a widget reads: the picked one, or the slot's default when none is picked.</summary>
    public static HardwareSensor? Find(ISensorProvider sensors, GlacialLcdSlot slot, int index, SensorSnapshotSources sources = default)
    {
        if (!string.IsNullOrEmpty(slot.Sensor))
        {
            return SensorSnapshotResolver.ResolveOrDefault(sensors, slot.Category ?? "", slot.Sensor, sources);
        }
        // Unpicked: the first temperature of the slot's category, the package reading for the second widget.
        string category = DefaultCategory(slot, index);
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

    /// <summary>
    /// The extra sources a widget's category needs, gathered only for that category: the extras
    /// snapshot, the capture sensors, the network aggregate and the fan header renames.
    /// </summary>
    public static SensorSnapshotSources Gather(
        ISensorProvider sensors, string category, Nexus.Service.Fps.IFpsProvider? fps, Func<IReadOnlyDictionary<string, string>?>? fanNames)
    {
        SensorExtras? extras = null;
        IReadOnlyList<HardwareSensor>? network = null;
        if (SensorSnapshotResolver.CategoryUsesExtras(category))
        {
            extras = sensors.GetSensorExtras();
            if (category == "network")
            {
                network = SensorSnapshotResolver.BuildNicNetworkSensors(extras.Nics);
            }
        }
        return new SensorSnapshotSources(
            extras,
            SensorSnapshotResolver.CategoryUsesFps(category) ? fps?.GetComponent().Sensors : null,
            network,
            category is "motherboard" or "gpu" ? fanNames?.Invoke() : null);
    }
}
