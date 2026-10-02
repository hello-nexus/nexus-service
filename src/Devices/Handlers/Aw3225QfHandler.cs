using System.Collections.Generic;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Devices.Handlers;

/// <summary>AW3225QF monitor; its crosshair is controlled over video DDC/CI.</summary>
public sealed class Aw3225QfHandler : IDeviceHandler
{
    private readonly Aw3225QfCrosshairController _crosshair;

    public Aw3225QfHandler(Aw3225QfCrosshairController crosshair) => _crosshair = crosshair;

    public string Id => "aw3225qf";
    public string Name => "Alienware AW3225QF";
    public string Category => "display";
    public IReadOnlyList<UsbId> Identifiers { get; } = System.Array.Empty<UsbId>();
    public bool SupportsNexusControl => false;
    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> _) => _crosshair.FindDisplayId() is not null;
    public string GetFirmwareVersion() => "";
}
