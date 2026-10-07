using System;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>Live snapshot of the SLV3 link surfaced to routes and the panel.</summary>
public sealed class Slv3State
{
    public bool IsConnected { get; set; }
    public string MasterMac { get; set; } = "";
    public int Channel { get; set; } = Slv3Protocol.DefaultChannel;
    public int TxFirmwareVersion { get; set; }
    // Volatile int backing (-1 = null): Nullable<int> writes are not atomic,
    // and the route serializer reads this lock-free while the hub writes it.
    private volatile int _moboPwmPercent = -1;

    /// <summary>Motherboard PWM-header duty sensed by the RX (GetDev header bytes [2..3]); null when unavailable.</summary>
    public int? MotherboardPwmPercent
    {
        get => _moboPwmPercent < 0 ? null : _moboPwmPercent;
        set => _moboPwmPercent = value is null ? -1 : Math.Clamp(value.Value, 0, 100);
    }
    public Slv3FanInfo[] Fans { get; set; } = Array.Empty<Slv3FanInfo>();

    /// <summary>
    /// Why the link is down, for the device page's disconnected state. One of
    /// <see cref="Slv3LinkStatus"/>. Only a connect attempt writes it, so it
    /// falls back to <see cref="Slv3LinkStatus.Unknown"/> whenever no attempt
    /// has run yet or one stopped being made (control gate off, teardown).
    /// </summary>
    public string LinkStatus { get; set; } = Slv3LinkStatus.Unknown;
}

/// <summary>
/// Values of <see cref="Slv3State.LinkStatus"/>. Strings, not an enum: the
/// source-generated serializer emits them verbatim for the web client to switch on.
/// </summary>
public static class Slv3LinkStatus
{
    public const string Ok = "ok";
    /// <summary>No connect attempt has run, or the last link was torn down without one.</summary>
    public const string Unknown = "unknown";
    /// <summary>Neither dongle enumerated.</summary>
    public const string None = "none";
    /// <summary>Only the RX half of the module enumerated.</summary>
    public const string TxMissing = "txMissing";
    /// <summary>Only the TX half of the module enumerated.</summary>
    public const string RxMissing = "rxMissing";
    /// <summary>Open refused because another app holds the WinUSB interface.</summary>
    public const string Busy = "busy";
    /// <summary>Open failed for any other OS reason.</summary>
    public const string OpenFailed = "openFailed";
    /// <summary>Both dongles opened, but the master-MAC handshake got no reply.</summary>
    public const string NoResponse = "noResponse";
}

/// <summary>One wireless fan as last reported by the RX device-list poll.</summary>
public sealed class Slv3FanInfo
{
    public string Mac { get; set; } = "";
    /// <summary>Bound master MAC, or empty when unbound.</summary>
    public string MasterMac { get; set; } = "";
    public bool BoundToUs { get; set; }
    public int Channel { get; set; }
    /// <summary>rx_type slot (1..13); 0 or 0xFE means unbound.</summary>
    public int Slot { get; set; }
    public int DevType { get; set; }
    /// <summary>Fan subtype from fans_type[0] (0x18=24 SLV3-LCD, 20-23 SLV3-LED, 36-39 SL-Infinity).</summary>
    public int FanType { get; set; }
    public int FanCount { get; set; }
    public int[] Rpm { get; set; } = Array.Empty<int>();
    /// <summary>Per-port fans_pwm echo on the firmware's 0..255 scale (6 = following the motherboard header), not a percent.</summary>
    public int[] Pwm { get; set; } = Array.Empty<int>();
    /// <summary>Hex of the RGB effect_index this fan last confirmed (device-list echo); empty until an RGB push lands.</summary>
    public string EffectIndex { get; set; } = "";
    /// <summary>True when the chain has missed recent device-list polls (beacon unheard); telemetry is last-known, not live.</summary>
    public bool Stale { get; set; }
    /// <summary>A HydroShift II that is also on the USB link (same radio MAC), where its screen has its own panel page.</summary>
    public bool UsbConnected { get; set; }
    /// <summary>HydroShift II coolant temperature in °C; null for other devices or when not reported.</summary>
    public int? CoolantTempC { get; set; }
    /// <summary>Chain RF firmware version; 0 when not reported.</summary>
    public int FirmwareVersion { get; set; }
    /// <summary>The chain's ARGB sync cable to a motherboard header is plugged in.</summary>
    public bool ArgbCableConnected { get; set; }
    /// <summary>The chain is playing its motherboard ARGB input instead of the host's frames.</summary>
    public bool PlayingMotherboardArgb { get; set; }
    /// <summary>The chain's PWM cable to a motherboard fan header is plugged in; without it, following the header has no input.</summary>
    public bool PwmCableConnected { get; set; }
}
