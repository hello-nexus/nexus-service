using System;
using System.Collections.Generic;
using Nexus.Service.Cooling;
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
        => Build(sources, pumps, fanProbes: false);

    /// <summary>Probes built into fans (iCUE LINK QX), grouped per device, for the "Fan sensors" category.</summary>
    public static List<HardwareComponent> BuildFanSensors(IReadOnlyList<TemperatureSource> sources)
        => Build(sources, null, fanProbes: true);

    /// <summary>
    /// Adds the first-party hub sensors to a platform extras snapshot so every reader of the central
    /// list sees them. Hub state is walked lock-free while its poll thread mutates it, so a torn read
    /// can throw; that is contained here rather than losing the whole extras read.
    /// </summary>
    public static void Merge(SensorExtras extras, IFanControlProvider? fans)
    {
        if (fans is null) return;
        try
        {
            var sources = fans.GetDeviceTemperatureSources();
            extras.Coolers.AddRange(Build(sources, fans.GetDevicePumpChannels()));
            extras.FanSensors.AddRange(BuildFanSensors(sources));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hub-sensors] hub sensors skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<HardwareComponent> Build(IReadOnlyList<TemperatureSource> sources, IReadOnlyList<FanChannel>? pumps, bool fanProbes)
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
            if (string.IsNullOrEmpty(s.DeviceId) || s.FanProbe != fanProbes) continue;
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
