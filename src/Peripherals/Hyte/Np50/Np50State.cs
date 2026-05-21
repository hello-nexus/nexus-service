using System.Collections.Generic;

namespace Qos.Service.Peripherals.Hyte.Np50;

// State records that the heartbeat worker fills in from device polls and
// that the cooling/lighting capabilities + REST endpoints read from. Plain
// POCOs so AppJsonContext can carry them straight through to the wire.

/// <summary>Top-level snapshot of an NP50 hub's last known state.</summary>
public sealed class Np50State
{
    /// <summary>Serial number reported by the USB enumeration. Used as the device id namespace.</summary>
    public string Serial { get; set; } = "";

    /// <summary>Firmware version string in "Major.Minor.Build.Hardware" form. Empty until first poll.</summary>
    public string FirmwareVersion { get; set; } = "";

    /// <summary>Hub-level info: legacy 4-pin RPM, cable sensor temp, cooling mode, warning summary.</summary>
    public Np50HubInfo HubInfo { get; set; } = new();

    /// <summary>Per-port child device lists. Index 0 = port 1, 1 = port 2, 2 = port 3.</summary>
    public List<Np50Port> Ports { get; set; } = new() { new() { Index = 1 }, new() { Index = 2 }, new() { Index = 3 } };

    /// <summary>Most recent detailed per-port warning state, parsed from FF CC 06.</summary>
    public Np50WarningDetail Warnings { get; set; } = new();

    /// <summary>Unix-ms timestamp of the last successful poll. 0 if never.</summary>
    public long LastPollMs { get; set; }
}

/// <summary>Hub-level fields parsed from "Get NP50 Info" (FF CC 01 00).</summary>
public sealed class Np50HubInfo
{
    /// <summary>RPM of the legacy 4-pin PWM fan, 0 if no fan attached.</summary>
    public int LegacyFanRpm { get; set; }

    /// <summary>Optional cable temp sensor reading (°C). Null when no probe present.</summary>
    public float? CableTempC { get; set; }

    /// <summary>"Software" | "Motherboard" | "Static".</summary>
    public string CoolingMode { get; set; } = "Motherboard";

    /// <summary>Summary warning byte from the info response. Non-zero means a detail-read should follow.</summary>
    public byte WarningSummary { get; set; }

    /// <summary>Firmware-side LED animation kind (0x01..0x04). Informational only.</summary>
    public byte FirmwareAnimation { get; set; }

    /// <summary>Firmware-side animation color RGB.</summary>
    public byte FirmwareAnimR { get; set; }
    public byte FirmwareAnimG { get; set; }
    public byte FirmwareAnimB { get; set; }

    /// <summary>Firmware-side animation brightness 0-100.</summary>
    public byte FirmwareAnimBrightness { get; set; }
}

/// <summary>One of the three Nexus Link Type-C ports.</summary>
public sealed class Np50Port
{
    /// <summary>Port index 1..3.</summary>
    public int Index { get; set; }

    /// <summary>Fan modules attached to this port, in daisy-chain order.</summary>
    public List<Np50FanDevice> Devices { get; set; } = new();
}

/// <summary>A single fan module (LS10 / LS30 / FP12) reported under a port.</summary>
public sealed class Np50FanDevice
{
    /// <summary>1-based index within the port's daisy chain.</summary>
    public int Index { get; set; }

    /// <summary>"LS10" | "LS30" | "FP12" | "Unknown".</summary>
    public string Model { get; set; } = "Unknown";

    /// <summary>LEDs reported by the fan (20 for LS10, 62 for LS30, varies otherwise).</summary>
    public int LedCount { get; set; }

    /// <summary>Hardware revision byte the fan reports.</summary>
    public byte HardwareVersion { get; set; }

    /// <summary>Fan RPM. 0 when stopped.</summary>
    public int Rpm { get; set; }

    /// <summary>Per-fan temp probe reading (°C). 0 when absent.</summary>
    public float? TempC { get; set; }

    /// <summary>"Back" | "Down" | "Up" | "Front".</summary>
    public string Orientation { get; set; } = "Back";

    /// <summary>True iff the FP12 touch sensor is active. Always false for non-FP12 modules.</summary>
    public bool Touching { get; set; }
}

/// <summary>Per-port warning bitfield from "Get Warning Detail" (FF CC 06).</summary>
public sealed class Np50WarningDetail
{
    public Np50PortWarning Port1 { get; set; } = new();
    public Np50PortWarning Port2 { get; set; } = new();
    public Np50PortWarning Port3 { get; set; } = new();
}

/// <summary>Decoded warning bits for one port.</summary>
public sealed class Np50PortWarning
{
    /// <summary>Raw byte (0x00 - 0x0F) as reported by the hub. Useful for clients that want the original wire value.</summary>
    public byte Raw { get; set; }

    /// <summary>Port carries more than 249 LEDs total.</summary>
    public bool LedCountExceeded { get; set; }

    /// <summary>AmpScale current overload active; fans on this port are throttled.</summary>
    public bool CurrentOverflow { get; set; }

    /// <summary>More than 18 daisy-chained devices on this single port.</summary>
    public bool PortDeviceCountExceeded { get; set; }

    /// <summary>More than 36/54 total devices across all ports (port-3 limit is 36, others 54).</summary>
    public bool TotalDeviceCountExceeded { get; set; }
}
