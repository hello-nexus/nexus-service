namespace Nexus.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Top-level snapshot of a HYTE IBP MiniHub. All four physical ports
/// can carry LEDs:
///
///   • Port 1: 1 RGB fan via Nexus-Link (motor PWM + ring on the same
///     cable). Streamed on channel 1. Default 16 LEDs (one fan ring).
///   • Port 2: up to 3 RGB fans, streamed on channel 2. Default 48
///     LEDs (3 fan rings).
///   • Port 3: LED-strip output, streamed on channel 3. Default 16
///     LEDs per the spec table.
///   • Port 4: LED-strip output, streamed on channel 4. Default 0
///     LEDs - user must declare how many they wired.
///
/// The MiniHub firmware does NOT enumerate connected hardware - the
/// official HYTE tool keeps these counts in a user-edited config
/// (MiniHubLayoutConfig) and we mirror that via
/// settings.Devices.ZoneLedCounts overrides exposed on the lighting page.
/// </summary>
public sealed class MiniHubState
{
    /// <summary>USB device instance id segment (e.g. "205D36703632"). Used as the device-id namespace.</summary>
    public string Serial { get; set; } = "";

    /// <summary>Firmware version in "Major.Minor.Build.Hw" form. Empty until first poll.</summary>
    public string FirmwareVersion { get; set; } = "";

    public MiniHubLedZone Port1 { get; set; } = new() { Channel = 1, LedCount = 16 };
    public MiniHubLedZone Port2 { get; set; } = new() { Channel = 2, LedCount = 48 };
    public MiniHubLedZone Port3 { get; set; } = new() { Channel = 3, LedCount = 16 };
    public MiniHubLedZone Port4 { get; set; } = new() { Channel = 4, LedCount = 0 };

    // ── Fan / cooling state ──

    /// <summary>
    /// Fans wired to port 1 (0 or 1). Mirrors HYTE's MiniHubLayoutConfig.Port1Fans.
    /// Drives whether the cooling page renders a Port 1 fan card. Defaults to 1
    /// because HYTE's reference and the Y70 stock configuration both ship with
    /// the rear fan wired to port 1.
    /// </summary>
    public int Port1Fans { get; set; } = 1;

    /// <summary>
    /// Fans wired to port 2 (0..3 daisy-chained). Defaults to 3 to match the
    /// Y70 stock front-fan trio. All chained fans share one PWM duty and one
    /// tach reading - port 2 surfaces as a single logical "Port 2 Fans" card
    /// in the cooling list, not one card per chained fan.
    /// </summary>
    public int Port2Fans { get; set; } = 3;

    /// <summary>Port-1 RPM agreed by <see cref="MiniHubTachConsensus"/>; 0 while <see cref="Port1RpmValid"/> is false.</summary>
    public int Port1Rpm { get; set; }

    /// <summary>True when recent port-1 polls agree on a plausible speed. False hides the readout (RpmUnavailable) instead of showing a corrupted sample.</summary>
    public bool Port1RpmValid { get; set; }

    /// <summary>Port-2 RPM agreed by <see cref="MiniHubTachConsensus"/>; 0 while <see cref="Port2RpmValid"/> is false.</summary>
    public int Port2Rpm { get; set; }

    /// <summary>Port-2 counterpart of <see cref="Port1RpmValid"/>.</summary>
    public bool Port2RpmValid { get; set; }

    /// <summary>Last raw port-1 poll decoded as RPM, before consensus. Trace only.</summary>
    public int Port1RawRpm { get; set; }

    /// <summary>Last raw port-2 poll decoded as RPM, before consensus. Trace only.</summary>
    public int Port2RawRpm { get; set; }

    /// <summary>Last commanded port-1 duty (10..100%). 0 means the port has never been driven from software.</summary>
    public int Port1Duty { get; set; }

    /// <summary>Last commanded port-2 duty (10..100%).</summary>
    public int Port2Duty { get; set; }
}

/// <summary>One MiniHub LED port - the user can adjust LedCount if the strip they wired differs from the firmware default.</summary>
public sealed class MiniHubLedZone
{
    /// <summary>Streaming channel byte (1..4, matching the physical port number).</summary>
    public int Channel { get; set; }

    /// <summary>Number of LEDs on the wired strip / fan ring(s).</summary>
    public int LedCount { get; set; }
}
