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
    /// <summary>
    /// A wireless chain has one name, set on the cooling page or the wireless
    /// controller page and stored under its device id in
    /// <see cref="Nexus.Service.Persistence.CoolingSettings.FanNames"/>. The
    /// lighting list reads it as the fallback for that device id, so a name
    /// set there reaches every lighting zone of the chain; a rename made on the
    /// lighting page itself still wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> WithWirelessChainNames(
        IReadOnlyDictionary<string, string> lightingNames,
        IReadOnlyDictionary<string, string> fanNames)
    {
        Dictionary<string, string>? merged = null;
        foreach (var (id, name) in fanNames)
        {
            if (string.IsNullOrWhiteSpace(name)
                || !id.StartsWith(WirelessDevicePrefix, System.StringComparison.Ordinal)
                || id.IndexOf(':', WirelessDevicePrefix.Length) >= 0
                || lightingNames.ContainsKey(id))
            {
                continue;
            }
            merged ??= new Dictionary<string, string>(lightingNames);
            merged[id] = name;
        }
        return merged ?? lightingNames;
    }

    private const string WirelessDevicePrefix = Nexus.Service.Lighting.Slv3LightingDeviceProvider.GroupId + ":";

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
