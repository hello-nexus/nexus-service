using System;
using System.Text;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The LCD on the ASUS ROG Crosshair X870E Glacial motherboard (USB 0B05:1D93), decoded from
/// MB Manager traffic. Commands are 65-byte HID output reports, report id 0xEC; custom
/// pictures ride a vendor bulk interface as one baseline JPEG in fixed-size transfers.
///
/// Device replies are HID input reports that echo the opcode; the 0x5C state reply is the
/// only one with a payload and has the same layout as the 0x5C write.
/// </summary>
public static class GlacialLcdProtocol
{
    public const int ProductId = 0x1D93;
    public const int ReportBytes = 65;
    public const byte ReportId = 0xEC;

    /// <summary>
    /// The size of every JPEG MB Manager uploaded. The panel is taken to be this size; the
    /// capture carries no query that reports it.
    /// </summary>
    public const int Width = 720;
    public const int Height = 1280;

    /// <summary>Baseline JPEG, 4:2:0; the quantization tables in MB Manager's uploads are the standard ones at this quality.</summary>
    public const int JpegQuality = 75;

    public const int ChunkBytes = 4096;

    /// <summary>
    /// Spacing between bulk transfer submissions in the capture, steady across every chunk.
    /// Whether the panel or the app sets that pace is not visible in a submit-only trace,
    /// so the driver keeps it.
    /// </summary>
    public const int ChunkIntervalMs = 2100;

    /// <summary>Mode byte of a 0x51 report.</summary>
    public const byte ModeOff = 0x00;
    public const byte ModeStaticAsset = 0x11;
    public const byte ModeAnimatedAsset = 0x14;
    public const byte ModeHardwareMonitor = 0x21;

    /// <summary>The hardware monitor shows one to three widgets.</summary>
    public const int MaxMonitorSlots = 3;

    /// <summary>Label field of a slot report: bytes 3 to 20. The value text starts at byte 21.</summary>
    public const int SlotLabelBytes = 18;
    public const int SlotValueOffset = 21;

    /// <summary>Value of the 0x5C sleep field when the LCD stays lit while the system sleeps.</summary>
    public const byte SleepKeepOn = 0x14;

    private static byte[] Report(byte opcode)
    {
        var report = new byte[ReportBytes];
        report[0] = ReportId;
        report[1] = opcode;
        return report;
    }

    /// <summary>MB Manager sends this once at startup, before its first state query. Purpose not visible.</summary>
    public static byte[] EncodeHello() => Report(0x82);

    /// <summary>State query; the app sends it ahead of every settings or mode change.</summary>
    public static byte[] EncodeQuery() => Report(0xDC);

    /// <summary>
    /// Settings write. Brightness (percent) appears three times, the panel mode once and the
    /// sleep behavior once; the app echoes every field from the last 0x5C reply and changes one.
    /// </summary>
    public static byte[] EncodeSettings(int brightnessPercent, byte mode, bool keepOnWhileAsleep)
    {
        byte percent = (byte)Math.Clamp(brightnessPercent, 0, 100);
        var report = Report(0x5C);
        report[2] = 0x01;
        report[5] = percent;
        report[9] = percent;
        report[10] = mode;
        report[15] = percent;
        report[16] = keepOnWhileAsleep ? SleepKeepOn : (byte)0;
        return report;
    }

    /// <summary>Reads the fields back out of a 0x5C state reply, or null when it is some other report.</summary>
    public static (int BrightnessPercent, byte Mode, bool KeepOnWhileAsleep)? ParseState(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 17 || reply[0] != ReportId || reply[1] != 0x5C)
        {
            return null;
        }
        return (reply[5], reply[10], reply[16] == SleepKeepOn);
    }

    /// <summary>Mode select. <paramref name="asset"/> is the predefined picture or animation index.</summary>
    public static byte[] EncodeMode(byte mode, int asset = 0)
    {
        var report = Report(0x51);
        report[2] = mode;
        report[4] = (byte)asset;
        return report;
    }

    public static byte[] EncodeOff() => EncodeMode(ModeOff);

    /// <summary>Hardware monitor display: <paramref name="slots"/> widgets, then the mode select.</summary>
    public static byte[] EncodeSlotCount(int slots)
    {
        var report = Report(0x52);
        report[3] = (byte)Math.Clamp(slots - 1, 0, MaxMonitorSlots - 1);
        return report;
    }

    public static byte[] EncodeHardwareMonitor() => EncodeMode(ModeHardwareMonitor);

    /// <summary>
    /// One monitor widget: slot index, label and value as UTF-8, each NUL padded in a fixed
    /// field. The app resends every slot about every 5 s while the mode is active, and writes
    /// temperatures as the number, a dot, one decimal, then the degree Celsius sign (U+2103).
    /// </summary>
    public static byte[] EncodeSlot(int slot, string label, string value)
    {
        var report = Report(0x53);
        report[2] = (byte)slot;
        CopyField(label, report.AsSpan(3, SlotLabelBytes));
        CopyField(value, report.AsSpan(SlotValueOffset));
        return report;
    }

    private static void CopyField(string text, Span<byte> field)
    {
        // Cut on a character boundary so a long label never leaves half a code point.
        var bytes = Encoding.UTF8.GetBytes(text);
        int length = Math.Min(bytes.Length, field.Length);
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }
        bytes.AsSpan(0, length).CopyTo(field);
    }

    /// <summary>The four reports MB Manager sends, in order, before it announces a custom picture.</summary>
    public static byte[][] EncodeCustomImagePrelude()
    {
        var begin = Report(0x71);
        begin[2] = 0x01;
        begin[3] = 0x01;
        var flush = Report(0xF1);
        var slot = Report(0x72);
        slot[2] = 0x01;
        slot[3] = 0x01;
        var target = Report(0x73);
        target[2] = 0x01;
        return new[] { begin, flush, slot, target };
    }

    /// <summary>Announces a JPEG of <paramref name="jpegBytes"/> on the bulk pipe (24-bit little endian).</summary>
    public static byte[] EncodeJpegAnnounce(int jpegBytes)
    {
        if (jpegBytes <= 0 || jpegBytes > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(jpegBytes));
        }
        var report = Report(0x7F);
        report[2] = 0x02;
        report[3] = (byte)jpegBytes;
        report[4] = (byte)(jpegBytes >> 8);
        report[5] = (byte)(jpegBytes >> 16);
        return report;
    }
}
