using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Serialization;

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
        MapGlacialLcdEndpoints(app);
        MapDeepCoolMystiqueEndpoints(app);
        MapDeepCoolMystiqueMediaEndpoints(app);
        MapHydroShift2Endpoints(app);
        MapHydroShift2CurveEndpoints(app);
        MapKrakenEndpoints(app);
        MapCorsairEndpoints(app);
        MapCorsairLcdEndpoints(app);
        MapStrimerEndpoints(app);
        MapNollieEndpoints(app);
        MapAsiahorseUmbraEndpoints(app);
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

    /// <summary>
    /// A firmware read that returned no data on a connected device. The read can lose a race
    /// with an in-flight write or time out, so this is a retryable 503 rather than a 500 that
    /// the error reporter would count as a service fault.
    /// </summary>
    internal static IResult FirmwareReadBusy(string message) =>
        Results.Json(ApiResponse.Fail(message), AppJsonContext.Default.ApiResponse,
            statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>A device route refusal in the ApiResponse envelope, serialized with the source-generated type info.</summary>
    internal static IResult DeviceFail(int statusCode, string message) =>
        Results.Json(ApiResponse.Fail(message), AppJsonContext.Default.ApiResponse, statusCode: statusCode);
}
