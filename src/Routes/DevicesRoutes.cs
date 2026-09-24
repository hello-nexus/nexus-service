
namespace Nexus.Service.Routes;

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
        MapZoneEndpoints(app);
        MapOpenRgbManualDeviceEndpoints(app);
        MapMappingEndpoints(app);
        MapSmartLightsEndpoints(app);
        MapHomeAssistantEndpoints(app);
        MapNp50Endpoints(app);
        MapSmartHubEndpoints(app);
        MapMiniHubEndpoints(app);
        MapQSeriesCoolerEndpoints(app);
        MapFirmwareEndpoints(app);
        MapLianLiEndpoints(app);
        MapLianLiTlEndpoints(app);
        MapGalahad2Endpoints(app);
        MapKrakenEndpoints(app);
        MapCorsairEndpoints(app);
        MapCorsairLcdEndpoints(app);
        MapStrimerEndpoints(app);
        MapNollieEndpoints(app);
    }

    /// <summary>
    /// The device's stored overrides that do NOT belong to the given card's
    /// zone - the per-card save/reset endpoints replace only their own zone's
    /// entries and must leave sibling zones untouched.
    /// </summary>
    internal static List<Nexus.Service.Persistence.SegmentLedOverride> CollectOverridesOutsideZone(
        Nexus.Service.Persistence.NexusSettings settings,
        Nexus.Service.Lighting.Zones.ZoneOverrideContext ctx)
    {
        var result = new List<Nexus.Service.Persistence.SegmentLedOverride>();
        if (settings.Devices.DeviceLedOverrides.TryGetValue(ctx.DeviceId, out var existing))
        {
            foreach (var o in existing)
            {
                if (ctx.MapFromSegment(o.Segment, o.LedIndex) < 0)
                    result.Add(o);
            }
        }
        return result;
    }
}
