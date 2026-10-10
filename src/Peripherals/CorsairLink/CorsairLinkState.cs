using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>One auto-detected device on the daisy chain, with its live telemetry.</summary>
public sealed class CorsairLinkDevice
{
    /// <summary>1-based daisy-chain position; the LED/speed/temperature addressing index.</summary>
    public int Channel { get; init; }
    public int Type { get; init; }
    public int Model { get; init; }
    public string Name { get; init; } = "";
    public CorsairLinkClass Class { get; init; }
    public int LedCount { get; init; }
    public bool HasSpeed { get; init; }
    public bool HasTemperature { get; init; }
    public string Serial { get; init; } = "";

    /// <summary>Physical hub port: 0 for channels 1..12, 1 for channels 13..24.</summary>
    public int PortId { get; init; }

    /// <summary>Last RPM read; -1 = no data.</summary>
    public int Rpm { get; set; } = -1;

    /// <summary>Last temperature in Celsius; NaN = no probe / no data.</summary>
    public float TempC { get; set; } = float.NaN;
}

/// <summary>
/// Live snapshot of the hub: connection, firmware, and the auto-detected device
/// topology. Mutated under the hub lock; readers take a consistent
/// <see cref="Devices"/> reference.
/// </summary>
public sealed class CorsairLinkState
{
    public bool IsConnected { get; set; }

    /// <summary>Firmware version string, e.g. "3.2.571".</summary>
    public string Firmware { get; set; } = "";

    /// <summary>Auto-detected devices in channel order. Replaced wholesale on each topology refresh.</summary>
    public IReadOnlyList<CorsairLinkDevice> Devices { get; set; } = Array.Empty<CorsairLinkDevice>();

    /// <summary>True when a pump-LCD device (type 6 or 14) is present in the chain.</summary>
    public bool HasLcd { get; set; }

    /// <summary>Listed speed-capable channels the hub has no sensor slot for (dark until re-detected).</summary>
    public IReadOnlyList<int> UnmappedChannels { get; set; } = Array.Empty<int>();
}
