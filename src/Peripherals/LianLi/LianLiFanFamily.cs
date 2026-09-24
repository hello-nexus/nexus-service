namespace Nexus.Service.Peripherals.LianLi;

public enum LianLiFanFamily
{
    Sl,
    Al,
    SlInfinity,
    SlV2,
    AlV2,
}

/// <summary>
/// Per-PID fan descriptor. Parameterises the Uni fan builders; fields map
/// directly to protocol byte positions. Source: FanControl.LianLi / L-Connect 3.
/// </summary>
public readonly struct LianLiFanProfile
{
    public LianLiFanFamily Family { get; init; }

    /// <summary>USB product id the profile was matched on; keys community LED mappings.</summary>
    public int ProductId { get; init; }

    /// <summary>
    /// When true, duty 0 maps to 1 and duty 1..9 maps to 10 (firmware stall floor).
    /// When false, duty passes through clamped to 0..100.
    /// </summary>
    public bool FlooredDuty { get; init; }

    /// <summary>Byte 2 of the manual-mode command (E0 10 &lt;reg&gt; ...).</summary>
    public byte ManualRegister { get; init; }

    /// <summary>Byte 2 of the ARGB/effect channel selector command.</summary>
    public byte ArgbRegister { get; init; }

    /// <summary>
    /// Byte 2 of the per-port fan-quantity command. The SL v1 packs
    /// <c>(port &lt;&lt; 4) | qty</c> into byte 3 (<see cref="PackedQuantity"/>);
    /// every later family sends <c>port+1, qty</c> as bytes 3 and 4.
    /// </summary>
    public byte QuantityRegister { get; init; }

    public bool PackedQuantity { get; init; }

    /// <summary>Base offset into the RPM input report: rpm[ch] = BE16(buf[RpmOffset + ch*2]).</summary>
    public int RpmOffset { get; init; }

    /// <summary>
    /// LED channels a port drives: 1 = one ring per fan (SL v1), 2 = inner ring on
    /// channel 2p, outer on 2p+1 (SL-Infinity).
    /// </summary>
    public int ChannelsPerPort { get; init; }

    /// <summary>Per-fan LED count on a port's first channel (the only one when <see cref="ChannelsPerPort"/> is 1).</summary>
    public int InnerLedsPerFan { get; init; }

    /// <summary>Per-fan LED count on a port's second channel; unused when <see cref="ChannelsPerPort"/> is 1.</summary>
    public int OuterLedsPerFan { get; init; }

    /// <summary>Colour reports go out over the interrupt-OUT pipe (WriteFile) instead of the control pipe.</summary>
    public bool ColorViaInterruptOut { get; init; }

    /// <summary>
    /// The quantity command precedes every colour push (SL-Infinity: without it
    /// the panel repaints on its own ~0.6 Hz cadence). Off, it is sent once on
    /// attach and on fan-count edits.
    /// </summary>
    public bool StartActionPerFrame { get; init; }

    /// <summary>Send the merge-off command on attach so a merged port-0 profile left by another app cannot mute ports 1-3.</summary>
    public bool ClearMergeOnAttach { get; init; }

    /// <summary>Static and breathing take one colour per fan replicated over its ring, not the 4-slot palette.</summary>
    public bool PerFanStaticPalette { get; init; }

    public string? ModelName { get; init; }

    /// <summary>Per-fan LED count for a hub channel index (even = inner/only, odd = outer).</summary>
    public int LedsPerFanForChannel(int ch) =>
        ChannelsPerPort == 1 || (ch & 1) == 0 ? InnerLedsPerFan : OuterLedsPerFan;
}

/// <summary>
/// PID-keyed profile table for the Lian Li Uni fan family (vendor 0x0CF2).
/// Source: FanControl.LianLi / L-Connect 3.
/// </summary>
public static class LianLiFanProfiles
{
    // SL v1 (0xA100, Redragon OEM 0xA106): one 16-LED ring per fan on one channel
    // per port, quantity packed into a single byte, colours over interrupt-OUT.
    // AL, SL v2 and AL v2 ring counts are unverified; they keep the SL-Infinity layout.
    private static readonly (int Pid, LianLiFanProfile Profile)[] s_table =
    {
        (0x7750, SlInfinityLayout(new LianLiFanProfile { Family = LianLiFanFamily.Sl,         ProductId = 0x7750, FlooredDuty = false, ManualRegister = 0x31, ArgbRegister = 0x30, QuantityRegister = 0x60, RpmOffset = 1, ModelName = "Uni Hub" })),
        (0xA100, SlLayout(        new LianLiFanProfile { Family = LianLiFanFamily.Sl,         ProductId = 0xA100, FlooredDuty = false, ManualRegister = 0x31, ArgbRegister = 0x30, QuantityRegister = 0x32, RpmOffset = 1, ModelName = "Uni SL" })),
        (0xA101, SlInfinityLayout(new LianLiFanProfile { Family = LianLiFanFamily.Al,         ProductId = 0xA101, FlooredDuty = false, ManualRegister = 0x42, ArgbRegister = 0x41, QuantityRegister = 0x40, RpmOffset = 1, ModelName = "Uni AL" })),
        (0xA102, SlInfinityLayout(new LianLiFanProfile { Family = LianLiFanFamily.SlInfinity, ProductId = 0xA102, FlooredDuty = true,  ManualRegister = 0x62, ArgbRegister = 0x61, QuantityRegister = 0x60, RpmOffset = 1, ModelName = "SL-Infinity" })),
        (0xA103, SlInfinityLayout(new LianLiFanProfile { Family = LianLiFanFamily.SlV2,       ProductId = 0xA103, FlooredDuty = true,  ManualRegister = 0x62, ArgbRegister = 0x61, QuantityRegister = 0x60, RpmOffset = 2, ModelName = "Uni SL v2" })),
        (0xA104, SlInfinityLayout(new LianLiFanProfile { Family = LianLiFanFamily.AlV2,       ProductId = 0xA104, FlooredDuty = true,  ManualRegister = 0x62, ArgbRegister = 0x61, QuantityRegister = 0x60, RpmOffset = 2, ModelName = "Uni AL v2" })),
        (0xA105, SlInfinityLayout(new LianLiFanProfile { Family = LianLiFanFamily.SlV2,       ProductId = 0xA105, FlooredDuty = true,  ManualRegister = 0x62, ArgbRegister = 0x61, QuantityRegister = 0x60, RpmOffset = 2, ModelName = "Uni SL v2" })),
        (0xA106, SlLayout(        new LianLiFanProfile { Family = LianLiFanFamily.Sl,         ProductId = 0xA106, FlooredDuty = false, ManualRegister = 0x31, ArgbRegister = 0x30, QuantityRegister = 0x32, RpmOffset = 1, ModelName = "Uni SL (Redragon OEM)" })),
    };

    private static LianLiFanProfile SlLayout(LianLiFanProfile p) => p with
    {
        PackedQuantity = true,
        ChannelsPerPort = 1,
        InnerLedsPerFan = LianLiProtocol.SlLedsPerFan,
        OuterLedsPerFan = 0,
        ColorViaInterruptOut = true,
        StartActionPerFrame = false,
        ClearMergeOnAttach = true,
        PerFanStaticPalette = true,
    };

    private static LianLiFanProfile SlInfinityLayout(LianLiFanProfile p) => p with
    {
        PackedQuantity = false,
        ChannelsPerPort = 2,
        InnerLedsPerFan = LianLiProtocol.InnerLedsPerFan,
        OuterLedsPerFan = LianLiProtocol.OuterLedsPerFan,
        ColorViaInterruptOut = false,
        StartActionPerFrame = true,
        ClearMergeOnAttach = false,
        PerFanStaticPalette = false,
    };

    /// <summary>The SL-Infinity profile: the layout composition code assumes while no hub is attached.</summary>
    public static LianLiFanProfile Default
    {
        get
        {
            TryGet(LianLiProtocol.ProductId, out var p);
            return p;
        }
    }

    /// <summary>All Uni fan PIDs for HID enumeration.</summary>
    public static readonly int[] AllProductIds =
    {
        0x7750, 0xA100, 0xA101, 0xA102, 0xA103, 0xA104, 0xA105, 0xA106,
    };

    public static bool TryGet(int productId, out LianLiFanProfile profile)
    {
        foreach (var (pid, p) in s_table)
        {
            if (pid == productId)
            {
                profile = p;
                return true;
            }
        }
        profile = default;
        return false;
    }
}
