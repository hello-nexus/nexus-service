using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// Removes the per-zone state of a device's outgoing cards when its
/// partition changes (fresh defaults - simple and predictable). Device-scoped
/// state survives by design: DeviceLedOverrides / DeviceAspectRatios live in
/// segment space, and ZoneLedCounts is keyed by hardware segment (the wiring
/// does not change when the user re-draws zone boundaries).
/// </summary>
public static class ZoneStateDrop
{
    public static void Drop(NexusSettings settings, IEnumerable<string> zoneIds)
    {
        foreach (var id in zoneIds)
        {
            settings.Devices.LightingDevicePrefs.Remove(id);
            settings.Lighting.DeviceLayouts.Remove(id);
            settings.Devices.AppliedMappings.Remove(id);
            settings.Devices.LedGroups.Remove(id);
            settings.Devices.MappingAutoApplyDeclined.Remove(id);
            if (settings.Devices.DisabledLightingDevices.Contains(id))
            {
                // Replace rather than mutate: the frame writers read this list
                // lock-free at frame rate.
                var next = new List<string>(settings.Devices.DisabledLightingDevices.Count);
                foreach (var d in settings.Devices.DisabledLightingDevices)
                {
                    if (d != id)
                    {
                        next.Add(d);
                    }
                }
                settings.Devices.DisabledLightingDevices = next;
            }
            if (settings.Devices.UncontrolledLightingDevices.Contains(id))
            {
                var next = new List<string>(settings.Devices.UncontrolledLightingDevices.Count);
                foreach (var d in settings.Devices.UncontrolledLightingDevices)
                {
                    if (d != id)
                    {
                        next.Add(d);
                    }
                }
                settings.Devices.UncontrolledLightingDevices = next;
            }
        }
    }
}
