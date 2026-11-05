using System.Linq;
using Nexus.Service.Models.Peripherals;
using Nexus.Service.Peripherals;

namespace Nexus.Service.Routes;

public static class PeripheralRoutes
{
    public static void MapPeripheralEndpoints(this WebApplication app)
    {
        // Static catalog - input peripherals Nexus drives natively.
        app.MapGet("/peripherals/supported", () =>
            new GetSupportedDevicesResponse { Items = SupportedDevicesCatalog.All.ToList() });

        // Static catalog - everything: peripherals + lighting merged and deduped.
        app.MapGet("/peripherals/all-supported", () =>
            new GetSupportedDevicesResponse { Items = AllSupportedDevices.Listed.ToList() });
    }
}
