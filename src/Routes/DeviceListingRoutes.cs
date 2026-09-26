using Nexus.Service.Devices;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapDeviceListingEndpoints(WebApplication app)
    {
        // Unified device list - registered handlers with connection status
        app.MapGet("/devices/all", (DeviceManager dm) => dm.GetAll());

        // Raw USB device list - every device the OS reports, with full details
        app.MapGet("/devices/usb/all", (DeviceManager dm) => dm.GetUsbDevices());

        // Toggle Nexus Control for one handler id. Off keeps the device detectable
        // (USB enumeration still sees it) but stops Nexus from claiming its port.
        app.MapPost("/devices/control", (
            DeviceControlRequest body,
            DeviceControlGate gate,
            DeviceManager dm,
            Nexus.Service.Devices.DeviceBroadcaster broadcaster,
            Nexus.Service.Sockets.MultiplexHub hub) =>
        {
            gate.SetEnabled(body.Id, body.Enabled);
            broadcaster.BroadcastNow();
            // The flip can move the device's competing app on or off the conflict whitelist.
            Nexus.Service.Sockets.PanelTopics.BroadcastPrefs(hub);
            return dm.GetAll();
        });
    }
}
