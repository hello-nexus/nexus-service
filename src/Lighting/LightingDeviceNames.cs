using System.Collections.Generic;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Lighting;

/// <summary>
/// Applies the lighting page's user-renamed cards to the device list the SPA
/// reads. Only the /devices/lighting-devices/all route calls this: mappings,
/// telemetry and diagnostics keep reading the provider's hardware name, so a
/// rename never reaches a published community mapping. Mirrors the fan-header
/// rename the cooling routes apply over <see cref="Nexus.Service.Persistence.CoolingSettings.FanNames"/>.
/// </summary>
public static class LightingDeviceNames
{
    /// <summary>
    /// Renames in place - every provider builds its cards fresh per GetAll, so
    /// nothing cached is mutated. The replaced hardware name moves to
    /// <see cref="LightingDevice.OriginalName"/> so the UI can still show it.
    /// A group header carries no card of its own, so its rename is stored under
    /// the parent device id and handed to every member as
    /// <see cref="LightingDevice.ParentName"/>. A split card's header is the
    /// same case one level down - several cards, one device, no card of its own
    /// - so a rename stored under the DEVICE id reaches every zone as
    /// <see cref="LightingDevice.DeviceName"/>. Without it an ARGB port could
    /// not be renamed at all: its device id is neither a card id nor a parent.
    /// </summary>
    public static void Apply(List<LightingDevice> devices, IReadOnlyDictionary<string, string> names)
    {
        if (names.Count == 0) return;
        foreach (var dev in devices)
        {
            if (names.TryGetValue(dev.Id, out var custom) && !string.IsNullOrWhiteSpace(custom))
            {
                dev.OriginalName = dev.Name;
                dev.Name = custom;
            }
            if (dev.DeviceId is { } deviceId
                && names.TryGetValue(deviceId, out var deviceCustom)
                && !string.IsNullOrWhiteSpace(deviceCustom))
            {
                dev.DeviceName = deviceCustom;
            }
            if (dev.ParentDeviceId is { } parentId
                && names.TryGetValue(parentId, out var parentCustom)
                && !string.IsNullOrWhiteSpace(parentCustom))
            {
                dev.ParentName = parentCustom;
            }
        }
    }
}
