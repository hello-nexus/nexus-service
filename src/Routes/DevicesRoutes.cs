
namespace Qos.Service.Routes;

/// <summary>
/// Devices route group entrypoint. The actual route registrations live in
/// three partial files:
/// - DeviceListingRoutes: /devices/all, /devices/usb/all
/// - DeviceSettingsRoutes: connected check, firmware version, CNVS, mobo
///   LEDs, updates, function-check
/// - LightingDevicesRoutes: /devices/lighting-devices/* (CRUD, layout,
///   power, brightness, hue, saturation, zone-size, identify, rescan,
///   LED map editor)
/// </summary>
public static partial class DevicesRoutes
{
    public static void MapDevicesEndpoints(this WebApplication app)
    {
        MapDeviceListingEndpoints(app);
        MapDeviceSettingsEndpoints(app);
        MapLightingDevicesEndpoints(app);
        MapNp50Endpoints(app);
    }

    /// <summary>
    /// Parse the lighting-device id into (physicalIndex, zoneIndex). "openrgb-N"
    /// returns (N, -1); "openrgb-N-Z" returns (N, Z). Other shapes return (-1, -1).
    /// </summary>
    private static (int physicalIndex, int zoneIndex) ParseDeviceId(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("openrgb-", StringComparison.Ordinal))
        {
            return (-1, -1);
        }
        var tail = id.AsSpan(8);
        var dash = tail.IndexOf('-');
        if (dash < 0)
        {
            return int.TryParse(tail, out var p) ? (p, -1) : (-1, -1);
        }
        if (int.TryParse(tail.Slice(0, dash), out var phys) && int.TryParse(tail.Slice(dash + 1), out var zone))
        {
            return (phys, zone);
        }
        return (-1, -1);
    }

    /// <summary>
    /// Resolve a device + optional zone index from the device id returned by
    /// /devices/lighting-devices/all. Handles three id shapes:
    ///   - Stable by serial:   "openrgb-s-{sanitized-serial}" optionally
    ///     followed by "-{zoneIdx}" for a motherboard zone split.
    ///   - Stable by location: "openrgb-l-{sanitized-hid-path}" optionally
    ///     followed by "-{zoneIdx}".
    ///   - Legacy numeric:     "openrgb-N" or "openrgb-N-Z" (pre-stable-id).
    /// </summary>
    private static (Qos.Service.Lighting.Rgb.RgbDevice? device, int zoneIndex) ResolveDevice(
        string id,
        IReadOnlyList<Qos.Service.Lighting.Rgb.RgbDevice> devices)
    {
        if (string.IsNullOrEmpty(id) || devices is null || devices.Count == 0)
            return (null, -1);

        foreach (var d in devices)
        {
            if (string.Equals(d.StableId, id, StringComparison.Ordinal))
                return (d, -1);
        }

        var lastDash = id.LastIndexOf('-');
        if (lastDash > 0 && lastDash < id.Length - 1)
        {
            var prefix = id.AsSpan(0, lastDash);
            var suffix = id.AsSpan(lastDash + 1);
            if (int.TryParse(suffix, out var zoneIdx) && zoneIdx >= 0)
            {
                foreach (var d in devices)
                {
                    if (prefix.SequenceEqual(d.StableId))
                        return (d, zoneIdx);
                }
            }
        }

        var (phys, zone) = ParseDeviceId(id);
        if (phys >= 0)
        {
            foreach (var d in devices)
            {
                if (d.Index == phys)
                    return (d, zone);
            }
        }

        return (null, -1);
    }

    private static string[] BuildZoneTypeMap(Qos.Service.Lighting.Rgb.RgbDevice device)
    {
        var result = new string[device.LedCount];
        int offset = 0;
        foreach (var z in device.Zones)
        {
            var typeName = z.ZoneType switch
            {
                0 => "single",
                1 => "linear",
                2 => "matrix",
                _ => "unknown",
            };
            for (int i = 0; i < z.LedCount && offset + i < result.Length; i++)
                result[offset + i] = typeName;
            offset += z.LedCount;
        }
        return result;
    }

    private static void RefreshEngineLedMap(string id,
        Qos.Service.Lighting.Engine.LightingEngine engine,
        Qos.Service.Lighting.Rgb.RgbBridge? bridge,
        Qos.Service.Persistence.IConfigStore store)
    {
        if (bridge is null)
            return;
        var (device, zoneIdx) = ResolveDevice(id, bridge.Devices);
        if (device is null)
            return;

        float[] ledU, ledV;
        if (zoneIdx >= 0 && zoneIdx < device.Zones.Count)
        {
            var zone = device.Zones[zoneIdx];
            ledU = new float[zone.LedCount];
            ledV = new float[zone.LedCount];
            for (int i = 0; i < zone.LedCount; i++)
            {
                ledU[i] = zone.LedCount > 1 ? (float)i / (zone.LedCount - 1) : 0.5f;
                ledV[i] = 0.5f;
            }
        }
        else
        {
            var (dU, dV) = Qos.Service.Lighting.Rgb.LedUvComputer.ComputeDefaults(device);
            ledU = dU;
            ledV = dV;
        }
        if (ledU.Length == 0)
        {
            var n = device.LedCount;
            if (n <= 0)
                return;
            ledU = new float[n];
            ledV = new float[n];
            for (int i = 0; i < n; i++)
            { ledU[i] = n > 1 ? (float)i / (n - 1) : 0.5f; ledV[i] = 0.5f; }
        }
        var overrides = store.Load().Devices.LedMapOverrides;
        bool[]? ledDisabled = null;
        if (overrides.TryGetValue(id, out var list))
        {
            bool anyDisabled = false;
            foreach (var o in list)
            {
                if (o.LedIndex >= 0 && o.LedIndex < ledU.Length)
                {
                    ledU[o.LedIndex] = o.U;
                    ledV[o.LedIndex] = o.V;
                    if (o.Disabled)
                        anyDisabled = true;
                }
            }
            if (anyDisabled)
            {
                ledDisabled = new bool[ledU.Length];
                foreach (var o in list)
                {
                    if (o.LedIndex >= 0 && o.LedIndex < ledDisabled.Length && o.Disabled)
                        ledDisabled[o.LedIndex] = true;
                }
            }
        }
        foreach (var frame in engine.Devices)
        {
            if (frame.Id == id)
            {
                frame.LedU = ledU;
                frame.LedV = ledV;
                frame.LedDisabled = ledDisabled;
                break;
            }
        }
    }
}
