namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// A connected device whose serial hub can drop it into DFU bootloader mode for
/// flashing. Implemented by the HYTE serial hubs (CNVS, Q-series, Y70, MiniHub,
/// NP50). <see cref="FirmwareFlasher"/> picks the target that matches the
/// requested firmware-catalog key.
/// </summary>
public interface IDfuFlashTarget
{
    bool IsConnected { get; }

    /// <summary>
    /// The connected device's firmware-catalog key (its variant, e.g.
    /// "cnvs-left", "q60", "y70-infinite"), or empty when not connected/known.
    /// The only images this device is ever flashed with.
    /// </summary>
    string FirmwareType { get; }

    /// <summary>
    /// Release the COM port and send the in-app DFU-entry handshake. After this
    /// the device reboots into the bootloader and re-enumerates as 3402:0a00.
    /// Returns true if the handshake was sent.
    /// </summary>
    bool EnterDfuMode();
}
