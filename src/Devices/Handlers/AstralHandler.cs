using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Sensors;
using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// A ROG Astral graphics card, listed under Devices with a page of its own
/// (Level Sense, from the G-sensor <c>AstralGpuSupplement</c> reads, and the
/// Power Detector, from its 12V-2x6 power monitor). The card answers on the
/// GPU's I2C bus, not on USB, so it is present once a GPU read found either, a
/// few seconds after start, and the list names it after the card ("ASUS ROG
/// Astral RTX 5090"). Nexus only reads the card, so there is no Nexus Control
/// to switch.
/// </summary>
public sealed class AstralHandler : IDeviceHandler
{
    public const string HandlerId = "asus-astral";

    private const string FallbackName = "ASUS ROG Astral";

    private readonly ISensorProvider _sensors;
    private readonly AstralPowerDetectorService? _powerDetector;

    // The power detector runs on Windows only; elsewhere it is not registered.
    public AstralHandler(ISensorProvider sensors, AstralPowerDetectorService? powerDetector = null)
    {
        _sensors = sensors;
        _powerDetector = powerDetector;
    }

    public string Id => HandlerId;
    public string Name => CardName() ?? FallbackName;
    public string Category => "gpu";
    public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
    public bool SupportsNexusControl => false;

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => CardName() is not null;

    public string GetFirmwareVersion() => "";

    // Named after the first card found, as the page shows the first card.
    private string? CardName() =>
        _sensors.GetGpuLevelSenseStates().FirstOrDefault()?.Name ?? _powerDetector?.FirstCardName;
}
