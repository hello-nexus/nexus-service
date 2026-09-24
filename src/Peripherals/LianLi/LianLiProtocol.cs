using System;

namespace Nexus.Service.Peripherals.LianLi;

/// <summary>
/// Pure static byte-level builders and parsers for the Lian Li Uni Hub family
/// (SL-Infinity and the SL v1) HID protocol. No IO here; the hub owns the
/// transport. All reports start with report id 0xE0 on HID interface MI_01
/// (UsagePage 0xFF72, Usage 0xA1). Per-family differences (quantity register,
/// channel layout, colour transport) live in <see cref="LianLiFanProfile"/>.
/// Color order on the wire is R, B, G (green and blue are swapped vs RGB).
/// </summary>
public static class LianLiProtocol
{
    public const int VendorId = 0x0CF2;
    /// <summary>SL-Infinity PID. Used as the DeviceKey in LianLiZoneSupport for lighting community mappings.</summary>
    public const int ProductId = 0xA102;
    public const int VendorUsagePage = 0xFF72;
    public const int VendorUsage = 0xA1;

    public const int OutputReportSize = 353;
    public const int InputReportSize = 65;

    public const byte ReportId = 0xE0;

    /// <summary>Physical fan ports on the hub.</summary>
    public const int PortCount = 4;

    /// <summary>Fans a single port group can daisy-chain.</summary>
    public const int MaxFansPerPort = 4;

    // Per-fan LED counts, hardware-confirmed on fw 1.4 (2026-08-27) by lighting
    // single indices with the service stopped and reading them off a camera:
    // inner index 0/8/16 landed on fan 1/2/3, outer index 12 on fan 2. Matches
    // L-Connect's converters exactly (convertToInnerLEDColor Color[32] = 4 fans
    // x 8, convertToOuterLEDColor Color[48] = 4 x 12).
    // NOT 16 each: that was OpenRGB's UNIHUB_SLINF_CHAN_LED_COUNT (0x10*6 = 96),
    // a max-per-channel buffer size, misread as a per-fan count.
    public const int InnerLedsPerFan = 8;
    public const int OuterLedsPerFan = 12;

    /// <summary>SL v1 (0xA100): one ring of 16 LEDs per fan on a single channel per port.</summary>
    public const int SlLedsPerFan = 16;

    /// <summary>Largest per-fan count across every family, for shared scratch buffers.</summary>
    public const int MaxLedsPerFanPerChannel = SlLedsPerFan;

    /// <summary>Scale down R+G+B if their sum exceeds this value.</summary>
    public const int EnergyCapSum = 460;

    /// <summary>Direct/static color mode (host-driven frame delivery). UNIHUB_SLINF_LED_MODE_STATIC_COLOR.</summary>
    public const byte EffectStatic = 0x01;

    public const byte EffectBreathing = 0x02;

    /// <summary>
    /// Minimum gap between manual-mode and duty writes; firmware drops the duty
    /// byte if it arrives before the mode-transition settles.
    /// Source: FanControl.LianLi / L-Connect 3.
    /// </summary>
    public const int FanCommandSettleMs = 200;

    public const byte SpeedDefault = 0x00;
    public const byte DirectionDefault = 0x00;
    /// <summary>UNIHUB_SLINF_LED_BRIGHTNESS_100 (full). 0x00=full..0x03=25%; 0x08=off.</summary>
    public const byte BrightnessDefault = 0x00;

    /// <summary>
    /// Set number of fans on a port group (g=0..3). Feature report.
    /// SL-Infinity form: E0 10 &lt;quantityRegister&gt; (g+1) (qty 0..4) 00 00.
    /// SL v1 form (PackedQuantity): E0 10 &lt;quantityRegister&gt; ((g shl 4) or qty) 00 00 00.
    /// </summary>
    public static byte[] BuildSetQuantity(in LianLiFanProfile profile, int group, int qty)
    {
        var q = (byte)Math.Clamp(qty, 0, MaxFansPerPort);
        if (profile.PackedQuantity)
        {
            return new byte[]
            {
                ReportId, 0x10, profile.QuantityRegister,
                (byte)((group << 4) | q),
                0x00, 0x00, 0x00
            };
        }
        return new byte[]
        {
            ReportId, 0x10, profile.QuantityRegister,
            (byte)(group + 1),
            q,
            0x00, 0x00
        };
    }

    /// <summary>
    /// Commit an effect on channel ch (0..7): E0 (0x10|ch) effect speed dir
    /// brightness 00. STATIC_COLOR (0x01) latches the streamed per-LED frame;
    /// firmware modes animate on-chip from this single commit.
    /// </summary>
    public static byte[] BuildEffectCommit(int ch, byte effect, byte speed, byte dir, byte brightness)
    {
        return new byte[]
        {
            ReportId,
            (byte)(0x10 | (ch & 0x0F)),
            effect, speed, dir, brightness,
            0x00
        };
    }

    /// <summary>
    /// Set port ch (0..3) to manual/host mode. Re-entering resets the fan to its
    /// default RPM. Selector: 0x10 left-shifted by ch.
    /// Command: E0 10 &lt;manualRegister&gt; (0x10 shl ch) 00 00 00
    /// </summary>
    public static byte[] BuildManualMode(int ch, byte manualRegister)
    {
        return new byte[]
        {
            ReportId, 0x10, manualRegister,
            (byte)(0x10 << ch),
            0x00, 0x00, 0x00
        };
    }

    /// <summary>
    /// Release port ch (0..3) back to motherboard PWM sync.
    /// Selector: 0x11 left-shifted by ch (sync bit set vs manual-mode's 0x10).
    /// Command: E0 10 &lt;manualRegister&gt; (0x11 shl ch) 00 00 00
    /// </summary>
    public static byte[] BuildReleaseMode(int ch, byte manualRegister)
    {
        return new byte[]
        {
            ReportId, 0x10, manualRegister,
            (byte)(0x11 << ch),
            0x00, 0x00, 0x00
        };
    }

    /// <summary>
    /// Set fan duty on port ch (0..3).
    /// Command: E0 (0x20|ch) 00 DutyByte(duty, flooredDuty) 00 00 00
    /// </summary>
    public static byte[] BuildSetSpeed(int ch, int duty, bool flooredDuty)
    {
        return new byte[]
        {
            ReportId,
            (byte)(0x20 | ch),
            0x00,
            DutyByte(duty, flooredDuty),
            0x00, 0x00, 0x00
        };
    }

    /// <summary>SL v1 merge off: E0 10 34 00 00 00. Feature report.</summary>
    public static byte[] BuildStopMerge()
    {
        return new byte[] { ReportId, 0x10, 0x34, 0x00, 0x00, 0x00, 0x00 };
    }

    /// <summary>
    /// Frame sync, sent once after every lighting apply. Without it the firmware
    /// keeps rendering the previous effect settings - a mode change lands on the
    /// per-channel commit, but speed and brightness do not take until this
    /// arrives. Feature report. E0 60 00 01 00 00 00.
    /// Source: L-Connect 3 SLInfinityController.syncLightingFrame -> SetFrame(1).
    /// </summary>
    public static byte[] BuildFrameSync()
    {
        return new byte[] { ReportId, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00 };
    }

    /// <summary>
    /// Feature report that primes the device to return RPM telemetry on the next input report read.
    /// Command: E0 50 00 00 00 00 00
    /// </summary>
    public static byte[] BuildRpmPrimer()
    {
        return new byte[] { ReportId, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00 };
    }

    /// <summary>
    /// Fill <paramref name="report"/> (must be <see cref="OutputReportSize"/> bytes)
    /// with the RGB output report for channel ch. Trailing bytes past the supplied
    /// LEDs are zeroed so a reused buffer never leaks a previous frame's tail.
    /// </summary>
    public static void WriteColorData(Span<byte> report, int ch, ReadOnlySpan<byte> leds)
    {
        report.Clear();
        report[0] = ReportId;
        report[1] = (byte)(0x30 | (ch & 0x0F));

        var dst = 2;
        var src = 0;
        while (src + 2 < leds.Length && dst + 2 < OutputReportSize)
        {
            var r = leds[src];
            var g = leds[src + 1];
            var b = leds[src + 2];
            ApplyEnergyCap(ref r, ref g, ref b);
            report[dst]     = r;
            report[dst + 1] = b;
            report[dst + 2] = g;
            dst += 3;
            src += 3;
        }
    }

    /// <summary>
    /// Decode RPM for port ch (0..3) from an input report.
    /// Layout: buf[rpmOffset + ch*2] = high byte, buf[rpmOffset + ch*2 + 1] = low byte (big-endian).
    /// rpmOffset is 1 for most families, 2 for Uni SL v2 / Uni AL v2.
    /// </summary>
    public static int DecodeRpm(ReadOnlySpan<byte> buf, int ch, int rpmOffset)
    {
        var offset = rpmOffset + ch * 2;
        if (offset + 1 >= buf.Length) return 0;
        var rpm = (buf[offset] << 8) | buf[offset + 1];
        return rpm <= 6000 ? rpm : -1;
    }

    /// <summary>
    /// Floored families clamp to a minimum to prevent firmware fan stall.
    /// Source: FanControl.LianLi / L-Connect 3.
    /// </summary>
    public static byte DutyByte(int duty, bool flooredDuty)
    {
        if (flooredDuty)
        {
            if (duty <= 0) return 1;
            return (byte)Math.Clamp(duty, 10, 100);
        }
        return (byte)Math.Clamp(duty, 0, 100);
    }

    private static void ApplyEnergyCap(ref byte r, ref byte g, ref byte b)
    {
        var sum = r + g + b;
        if (sum <= EnergyCapSum) return;
        var scale = (double)EnergyCapSum / sum;
        r = (byte)(r * scale);
        g = (byte)(g * scale);
        b = (byte)(b * scale);
    }
}
