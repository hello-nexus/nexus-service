using System;

namespace Nexus.Service.Peripherals.Strimer;

// Source: OpenRGB.
// HID interface MI_01, UsagePage 0xFF72, Usage 0xA1, ReportId 0xE0.
// Color order on the wire is R, B, G (green and blue are swapped vs RGB).
public static class StrimerProtocol
{
    public const int VendorId         = 0x0CF2;
    public const int ProductId        = 0xA200;
    public const int VendorUsagePage  = 0xFF72;
    public const int VendorUsage      = 0xA1;
    public const byte ReportId        = 0xE0;
    // Source: OpenRGB LianLiStrimerLConnectController.h STRIMERLCONNECT_PACKET_SIZE.
    public const int OutputReportSize = 255;

    public const int ZoneCount       = 12;
    public const int AtxZoneCount    = 6;

    /// <summary>
    /// GPU zones on the triple 8-pin harness, and the default: 6 x 27 = 162 LEDs.
    /// </summary>
    public const int GpuZoneCount    = 6;

    /// <summary>
    /// The dual 8-pin harness is the same 27-LED zones, but only four of them (108 LEDs).
    /// Driving it as six leaves two zones addressing hardware that is not there and stretches
    /// the LED map across a strip a third longer than the physical one.
    /// </summary>
    public const int GpuZoneCountDual = 4;

    public const int AtxLedsPerZone  = 20;
    public const int GpuLedsPerZone  = 27;
    public const int MaxLedsPerZone  = 27;

    /// <summary>Clamps a configured GPU zone count to one of the two harnesses.</summary>
    public static int NormalizeGpuZoneCount(int zones) =>
        zones == GpuZoneCountDual ? GpuZoneCountDual : GpuZoneCount;

    // Wire byte for Direct/per-LED host-driven mode.
    public const byte ModeDirect = 0x01;

    // Fill report for a color push to zone. Clears the report first.
    // report[0]=ReportId, report[1]=(0x30|zone), LED data from offset 2 in R,B,G order.
    public static void WriteColorData(Span<byte> report, int zone, ReadOnlySpan<byte> leds)
    {
        report.Clear();
        report[0] = ReportId;
        report[1] = (byte)(0x30 | (zone & 0xFF));

        var dst = 2;
        var src = 0;
        while (src + 2 < leds.Length && dst + 2 < OutputReportSize)
        {
            report[dst]     = leds[src];         // R
            report[dst + 1] = leds[src + 2];     // B (swapped)
            report[dst + 2] = leds[src + 1];     // G (swapped)
            dst += 3;
            src += 3;
        }
    }

    // Returns the 7-byte effect command for zone.
    // byte[0]=ReportId, byte[1]=(0x10|zone), byte[2]=mode, byte[3]=speed, byte[4]=dir, byte[5]=brightness, byte[6]=0x00.
    public static byte[] BuildEffectCommit(int zone, byte mode, byte speed, byte dir, byte brightness)
    {
        return new byte[]
        {
            ReportId,
            (byte)(0x10 | (zone & 0xFF)),
            mode, speed, dir, brightness,
            0x00,
        };
    }

    /// <summary>Effect mode that hands every zone to the motherboard ARGB input; the speed byte is the on/off flag.</summary>
    public const byte ModeMotherboardSync = 0x40;

    /// <summary>Motherboard ARGB sync on or off: E0 10 40 [on] 00 00 00.</summary>
    public static byte[] BuildArgbSync(bool on) => BuildEffectCommit(0, ModeMotherboardSync, on ? (byte)1 : (byte)0, 0, 0);

    /// <summary>Applies the sync switch to the controller: E0 20 00 00.</summary>
    public static byte[] BuildEffectEnable() => new byte[] { ReportId, 0x20, 0x00, 0x00 };

    /// <summary>
    /// One apply-latch per full update cycle. Byte 2/3 are a big-endian bitmask of the zones
    /// the controller should light: bits 0..5 are the 24-pin ATX zones and bits 6.. the GPU
    /// harness, so a triple harness latches 0x0FFF and a dual one 0x03FF. Claiming zones that
    /// are not attached is what a fixed 0x0FFF did.
    /// </summary>
    public static byte[] BuildApplyLatch(int gpuZoneCount = GpuZoneCount)
    {
        var gpu = NormalizeGpuZoneCount(gpuZoneCount);
        var mask = ((1 << AtxZoneCount) - 1) | (((1 << gpu) - 1) << AtxZoneCount);
        return new byte[]
        {
            ReportId, 0x2C, (byte)((mask >> 8) & 0xFF), (byte)(mask & 0xFF), 0x00, 0x00, 0x00, 0x00,
        };
    }

    // 24-Pin ATX hardware zone for segment index seg (0-5).
    public static int AtxZone(int seg) => seg;

    // 8-Pin GPU hardware zone for segment index seg (0-5).
    public static int GpuZone(int seg) => seg + AtxZoneCount;
}
