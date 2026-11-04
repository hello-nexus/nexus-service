using System;
using System.Collections.Generic;
using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Devices.Handlers;

/// <summary>A ROG Astral graphics card, listed once a GPU read found its power monitor; Nexus only reads it, so there is no Nexus Control.</summary>
public sealed class AstralHandler : IDeviceHandler
{
    public const string HandlerId = "asus-astral";

    private readonly AstralMonitor _monitor;

    public AstralHandler(AstralMonitor monitor)
    {
        _monitor = monitor;
    }

    public string Id => HandlerId;
    public string Name => _monitor.CardName() ?? "ASUS ROG Astral";
    public string Category => "gpu";
    public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
    public bool SupportsNexusControl => false;
    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => _monitor.CardName() is not null;
    public string GetFirmwareVersion() => "";
}
