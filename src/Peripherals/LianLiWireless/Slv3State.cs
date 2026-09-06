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
}
