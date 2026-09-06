using System.Collections.Generic;
using Nexus.Service.Peripherals.Ibp;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// iBUYPOWER RGB keyboards (Chimera KM7 / KM10, MK9 / MK9 Pro, MEK 4). One
/// handler for the family: they share a vendor id, a lighting stack
/// (<see cref="IbpPeripheralHub"/>) and a Nexus Control gate. The label
/// follows the attached model. No device page - the LEDs live on the
/// Lighting page.
/// </summary>
public sealed class IbpKeyboardHandler : IDeviceHandler
{
    private readonly IbpPeripheralHub _hub;

    public IbpKeyboardHandler(IbpPeripheralHub hub)
    {
        _hub = hub;
    }

    public string Id => IbpPeripheralProtocol.KeyboardHandlerId;

    public string Name => IbpPeripheralHandlerSupport.Name(_hub, IbpPeripheralKind.Keyboard, "iBUYPOWER Keyboard");

    public string Category => "keyboard";

    public bool HasPage => false;

    public IReadOnlyList<UsbId> Identifiers { get; } = IbpPeripheralHandlerSupport.Identifiers(IbpPeripheralKind.Keyboard);

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        IbpPeripheralHandlerSupport.IsConnected(Identifiers, detectedDevices);

    public string GetFirmwareVersion() => "";
}

/// <summary>
/// iBUYPOWER RGB mice (Chimera KM7 / KM10). Same shape as
/// <see cref="IbpKeyboardHandler"/>; its own gate so a kit's mouse and
/// keyboard can be released independently.
/// </summary>
public sealed class IbpMouseHandler : IDeviceHandler
{
    private readonly IbpPeripheralHub _hub;

    public IbpMouseHandler(IbpPeripheralHub hub)
    {
        _hub = hub;
    }

    public string Id => IbpPeripheralProtocol.MouseHandlerId;

    public string Name => IbpPeripheralHandlerSupport.Name(_hub, IbpPeripheralKind.Mouse, "iBUYPOWER Mouse");

    public string Category => "mouse";

    public bool HasPage => false;

    public IReadOnlyList<UsbId> Identifiers { get; } = IbpPeripheralHandlerSupport.Identifiers(IbpPeripheralKind.Mouse);

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        IbpPeripheralHandlerSupport.IsConnected(Identifiers, detectedDevices);

    public string GetFirmwareVersion() => "";
}

internal static class IbpPeripheralHandlerSupport
{
    public static IReadOnlyList<UsbId> Identifiers(IbpPeripheralKind kind)
    {
        var ids = new List<UsbId>();
        foreach (var model in IbpPeripheralProtocol.Models)
        {
            if (model.Kind == kind) ids.Add(new UsbId(IbpPeripheralProtocol.VendorId, model.ProductId));
        }
        return ids;
    }

    public static bool IsConnected(IReadOnlyList<UsbId> identifiers, IReadOnlyList<UsbDeviceEntry> detected)
    {
        foreach (var d in detected)
        {
            foreach (var id in identifiers)
            {
                if (id.VendorId == d.VendorId && id.ProductId == d.ProductId) return true;
            }
        }
        return false;
    }

    // The attached model's marketed name once the hub holds it; the family
    // label before that (gate off, or not yet opened).
    public static string Name(IbpPeripheralHub hub, IbpPeripheralKind kind, string fallback)
    {
        foreach (var a in hub.Attached)
        {
            if (a.Model.Kind == kind) return a.Model.Name;
        }
        return fallback;
    }
}
