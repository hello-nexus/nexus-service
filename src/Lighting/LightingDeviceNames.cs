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
    private const string WirelessDevicePrefix = Slv3LightingDeviceProvider.GroupId + ":";

    /// <summary>
    /// A wireless chain's bare device id, the key its one shared name is stored
    /// under in <see cref="Nexus.Service.Persistence.CoolingSettings.FanNames"/>.
    /// A zone or port id of the chain carries a further segment and is not one.
    /// </summary>
    public static bool IsWirelessChainId(string id) =>
        id.StartsWith(WirelessDevicePrefix, System.StringComparison.Ordinal)
        && id.IndexOf(':', WirelessDevicePrefix.Length) < 0;

    /// <summary>
    /// The lighting page's rename write. A chain's name lives in the cooling
    /// store so the cooling page, the wireless controller page and this one
    /// share it; the lighting entry of an older rename is dropped so the
    /// stores converge. Every other id stays a lighting rename. An empty
    /// name resets. Returns true when the cooling store changed.
    /// </summary>
    public static bool SetName(Nexus.Service.Persistence.NexusSettings s, string id, string? name)
    {
        var chain = IsWirelessChainId(id);
        var store = chain ? s.Cooling.FanNames : s.Lighting.DeviceNames;
        if (chain)
        {
            s.Lighting.DeviceNames.Remove(id);
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            store.Remove(id);
        }
        else
        {
            store[id] = name.Trim();
        }
        return chain;
    }

    /// <summary>
    /// The names the lighting list applies: the lighting renames plus, for a
    /// chain without one, the cooling name of its device id. Both sources are
    /// copied, so call it inside <c>IConfigStore.Read</c> and hand the result
    /// out; nothing live is enumerated afterwards.
    /// </summary>
    public static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> lightingNames,
        IReadOnlyDictionary<string, string> fanNames)
    {
        var merged = new Dictionary<string, string>(lightingNames);
        foreach (var (id, name) in fanNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && IsWirelessChainId(id) && !merged.ContainsKey(id))
            {
                merged[id] = name;
            }
        }
        return merged;
    }

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
