using System.Collections.Generic;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Monitoring;

/// <summary>
/// Projects first-party cooling-hub temperature probes (NP50 cable + per-FP12, Q-series coolant,
/// iCUE LINK fan probes) and USB AIO pump speeds into the monitoring "extras" shape so they
/// appear in the widget sensor picker's Cooler category. The cooling providers own these; the
/// platform sensor providers only ever see coolers LibreHardwareMonitor recognises, so without
/// this the two lists disagree.
/// </summary>
internal static class HubCoolerSensors
{
    /// <summary>
    /// Group every device-owned temperature source by its device. Sources with no DeviceId are
    /// motherboard/CPU/GPU channels that the platform provider already reports, and are skipped.
    /// Ids match the curve-source ids, so a probe is the same sensor in both pickers.
    /// </summary>
    public static List<HardwareComponent> Build(IReadOnlyList<TemperatureSource> sources, IReadOnlyList<FanChannel>? pumps = null)
    {
        var byDevice = new Dictionary<string, HardwareComponent>();
        var ordered = new List<HardwareComponent>();

        HardwareComponent ComponentFor(string deviceId, string? deviceName)
        {
            if (!byDevice.TryGetValue(deviceId, out var component))
            {
                component = new HardwareComponent
                {
                    Id = deviceId,
                    Name = string.IsNullOrEmpty(deviceName) ? deviceId : deviceName,
                };
                byDevice[deviceId] = component;
                ordered.Add(component);
            }
            return component;
        }

        foreach (var s in sources)
        {
            if (string.IsNullOrEmpty(s.DeviceId)) continue;
            // A non-finite float fails serialization of the whole extras envelope, not just this row.
            if (!float.IsFinite(s.Value)) continue;

            var component = ComponentFor(s.DeviceId, s.DeviceName);
            component.Sensors.Add(new HardwareSensor
            {
                Id = s.Id,
                Name = s.Name,
                Type = "Temperature",
                Value = s.Value,
                Units = "°C",
                Formatted = $"{s.Value:F1} °C",
                // Min/Max stay at their defaults: the cooling providers track no session extremes,
                // and echoing the live value here would read as one.
                Parent = new SensorParent { Id = component.Id, Name = component.Name },
            });
        }

        if (pumps is not null)
        {
            // Pump speed beside the coolant probe, so an AIO screen can show both; ids are the channel ids.
            foreach (var p in pumps)
            {
                if (string.IsNullOrEmpty(p.DeviceId) || p.RpmUnavailable) continue;
                var component = ComponentFor(p.DeviceId, p.DeviceName);
                component.Sensors.Add(new HardwareSensor
                {
                    Id = p.Id,
                    Name = p.Name,
                    Type = "Fan",
                    Value = p.Rpm,
                    Units = "RPM",
                    Formatted = $"{p.Rpm} RPM",
                    Parent = new SensorParent { Id = component.Id, Name = component.Name },
                });
            }
        }

        return ordered;
    }
}
