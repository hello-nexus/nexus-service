using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Snapshot of the connected HYTE Q-series cooler controller: which variant is
/// attached and the firmware version it reports.
/// </summary>
public sealed class QSeriesCoolerState
{
    /// <summary>
    /// "q60" or "q80" (the bundled-firmware directory key), or empty when no
    /// cooler is connected. Determined from the matched USB PID at connect time.
    /// </summary>
    public string Variant { get; set; } = "";

    /// <summary>Firmware version in "Major.Minor.Build.Hw" form (e.g. "2.0.9.1"). Empty until first poll.</summary>
    public string FirmwareVersion { get; set; } = "";

    /// <summary>USB instance-id segment used as the device-id namespace. Empty until connected.</summary>
    public string Serial { get; set; } = "";

    /// <summary>Pump-head RPM from the last Port-0 poll. 0 until first poll / when no sensor.</summary>
    public int PumpRpm { get; set; }

    /// <summary>Second-pump RPM (Q80 dual-pump). 0 on single-pump units.</summary>
    public int Pump2Rpm { get; set; }

    /// <summary>True once a Q80 second pump has reported a non-zero RPM.</summary>
    public bool HasPump2 { get; set; }

    private IReadOnlyList<QSeriesLinkDevice> _channel1Devices = Array.Empty<QSeriesLinkDevice>();
    private IReadOnlyList<QSeriesLinkDevice> _channel2Devices = Array.Empty<QSeriesLinkDevice>();

    /// <summary>
    /// Nexus Link devices chained on channel 1 (the FAN1/UART1 connector), in wire order.
    /// Each poll publishes a freshly parsed list rather than mutating this one in place, so a
    /// reader that took a reference before a concurrent publish keeps iterating a complete,
    /// never-mutated snapshot instead of racing a Clear()/Add() rebuild.
    /// </summary>
    public IReadOnlyList<QSeriesLinkDevice> Channel1Devices
    {
        get => Volatile.Read(ref _channel1Devices);
        set => Volatile.Write(ref _channel1Devices, value);
    }

    /// <summary>Nexus Link devices chained on channel 2 (the FAN2/UART2 connector). Same publish discipline as <see cref="Channel1Devices"/>.</summary>
    public IReadOnlyList<QSeriesLinkDevice> Channel2Devices
    {
        get => Volatile.Read(ref _channel2Devices);
        set => Volatile.Write(ref _channel2Devices, value);
    }

    /// <summary>Hub control mode byte from the last Port-0 poll (Software/Motherboard/Firmware/Mix).</summary>
    public byte ControlMode { get; set; } = QSeriesCoolerProtocol.ControlModeMotherboard;

    /// <summary>Turbo state from the last Port-0 poll.</summary>
    public bool TurboOn { get; set; }

    /// <summary>Coolant inlet temperature (°C) from the last Port-0 poll. Null when no probe reads in range.</summary>
    public float? CoolantTempInC { get; set; }

    /// <summary>Coolant outlet temperature (°C) from the last Port-0 poll. Null when no probe reads in range.</summary>
    public float? CoolantTempOutC { get; set; }
}

/// <summary>
/// One Nexus Link device (LS10 / LS30 / FP12 solo/Duo/Trio / LN60 / LN70) chained on a
/// Q-series channel, parsed from its 12-byte channel-info slot.
/// </summary>
public sealed class QSeriesLinkDevice
{
    /// <summary>1-based position in the channel's daisy chain (the firmware's device-index byte).</summary>
    public int Slot { get; set; }

    /// <summary>"LS10" | "LS30" | "FP12" | "FT12 Duo" | "FT12 Trio" | "LN60" | "LN70" | "Unknown".</summary>
    public string Model { get; set; } = "Unknown";

    /// <summary>LEDs on this device. 0 for FT12/FP12 - the firmware reports no LED count for that family.</summary>
    public int LedCount { get; set; }

    /// <summary>0 for a light strip, 1/2/3 for a solo/Duo/Trio FT12 unit.</summary>
    public int FanCount { get; set; }

    /// <summary>Per-fan RPM, length equal to <see cref="FanCount"/>. Empty for a light strip.</summary>
    public int[] FanRpm { get; set; } = Array.Empty<int>();

    /// <summary>Thermistor reading (°C). Null for a light strip - only an FT12 unit carries a probe.</summary>
    public float? TemperatureC { get; set; }

    /// <summary>"Back" | "Down" | "Up" | "Front".</summary>
    public string Orientation { get; set; } = "Back";

    /// <summary>True when the firmware marks this device as the end of its connection group; a channel can carry more than one group, so this is not the same as being the chain's last physical device.</summary>
    public bool GroupEnd { get; set; }
}
