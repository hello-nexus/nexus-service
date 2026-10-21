using System;
using System.Text;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The LCD on the ASUS ROG Crosshair X870E Glacial motherboard (USB VID/PID in <see cref="ProductId"/>), decoded from
/// MB Manager traffic. Commands are fixed-size HID output reports (<see cref="ReportBytes"/>)
/// led by <see cref="ReportId"/>. A custom picture is stored on the panel: a short report
/// sequence announces it, one baseline JPEG follows on the vendor bulk interface in fixed-size
/// transfers, and a last report pair shows it.
///
/// Device replies are HID input reports that echo the opcode; the state reply is the only
/// one with a payload and has the same layout as the settings write.
/// </summary>
public static class GlacialLcdProtocol
{
    public const int ProductId = 0x1D93;
    public const int ReportBytes = 65;
    public const byte ReportId = 0xEC;

    /// <summary>Size of every picture MB Manager uploaded, which is taken to be the panel's own.</summary>
    public const int PanelWidth = 720;
    public const int PanelHeight = 1280;

    /// <summary>Baseline JPEG, 4:2:0, at the standard quantization tables scaled for this quality.</summary>
    public const int JpegQuality = 75;

    /// <summary>The quality frames are encoded at for upload: lower than the vendor app's, so a picture is fewer transfers, with text still clean at this size.</summary>
    public const int StreamJpegQuality = 65;

    /// <summary>Every bulk transfer is this size; the last one is zero padded past the picture.</summary>
    public const int ChunkBytes = 4096;

    /// <summary>Spacing between bulk transfers in the captures, steady across every chunk.</summary>
    public const int ChunkIntervalMs = 2100;

    /// <summary>Gap between the size announce and the first bulk transfer.</summary>
    public const int ChunkLeadMs = 502;

    /// <summary>Gap between the end-of-upload report and the report pair that shows the picture.</summary>
    public const int FinishSettleMs = 5898;

    /// <summary>
    /// Custom picture slots seen in the vendor captures (the apps upload to 0, 1 and 2). The
    /// stream alternates between these two, so the slot on display is never the one being written.
    /// </summary>
    public static readonly int[] CustomSlots = { 1, 2 };

    /// <summary>Mode bytes of the mode-select report.</summary>
    public const byte ModeOff = 0x00;
    public const byte ModeStaticAsset = 0x11;
    public const byte ModeAnimatedAsset = 0x14;
    public const byte ModeHardwareMonitor = 0x21;

    /// <summary>Predefined static picture ids the vendor app was seen selecting.</summary>
    public static readonly int[] StaticAssets = { 0, 1, 2, 3, 4, 5 };

    /// <summary>Predefined animation ids the vendor app was seen selecting.</summary>
    public static readonly int[] AnimatedAssets = { 0, 1 };

    /// <summary>Most widgets the hardware monitor shows at once.</summary>
    public const int MaxMonitorSlots = 3;

    /// <summary>Label field of a slot report, from the byte after the slot index up to <see cref="SlotValueOffset"/>.</summary>
    public const int SlotLabelBytes = 18;
    public const int SlotValueOffset = 21;

    /// <summary>Value of the settings report's display field while the LCD is on.</summary>
    public const byte DisplayOnFlag = 0x14;

    /// <summary>Value of the settings report's sleep field when the LCD stays lit while the system sleeps.</summary>
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
    /// The settings write, read-modify-write: the fresh state reply with only the given fields
    /// changed and the write flag set, so every other byte is carried through unchanged.
    /// Brightness appears three times in the report, then a display flag (on or off, not the
    /// picture mode: the state reply still held the on value while a static picture was
    /// selected) and the sleep behavior.
    /// </summary>
    public static byte[] EncodeSettingsEdit(ReadOnlySpan<byte> stateReply, int? brightnessPercent, bool? displayOn, bool? keepOnWhileAsleep)
    {
        var report = new byte[ReportBytes];
        stateReply[..Math.Min(stateReply.Length, ReportBytes)].CopyTo(report);
        report[2] = 0x01;
        if (brightnessPercent is int percent)
        {
            byte level = (byte)Math.Clamp(percent, 0, 100);
            report[5] = level;
            report[9] = level;
            report[15] = level;
        }
        if (displayOn is bool on)
        {
            report[10] = on ? DisplayOnFlag : (byte)0;
        }
        if (keepOnWhileAsleep is bool keep)
        {
            report[16] = keep ? SleepKeepOn : (byte)0;
        }
        return report;
    }

    /// <summary>Reads the fields back out of a state reply, or null when it is some other report.</summary>
    public static (int BrightnessPercent, bool DisplayOn, bool KeepOnWhileAsleep)? ParseState(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 17 || reply[0] != ReportId || reply[1] != 0x5C || reply[2] != 0)
        {
            return null;
        }
        return (reply[5], reply[10] != 0, reply[16] == SleepKeepOn);
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
    /// field. The app resends every slot on a short timer while the mode is active, and writes
    /// temperatures as the number, a dot, one decimal, then the degree Celsius sign.
    /// </summary>
    public static byte[] EncodeSlot(int slot, string label, string value)
    {
        var report = Report(0x53);
        report[2] = (byte)slot;
        // One byte short of the field, so a NUL always separates the label from the value.
        CopyField(label, report.AsSpan(3, SlotLabelBytes - 1));
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

    /// <summary>
    /// The reports MB Manager sends before the bulk transfers, each with the gap it left before
    /// it, in the order captured: begin, flush, target slot, stage, then the 24-bit size.
    /// </summary>
    public const int PreludeGapTotalMs = 291 + 25 + 67 + 112 + 516;

    public static (byte[] Report, int GapMs)[] EncodeCustomImagePrelude(int jpegBytes, int slotId)
    {
        if (jpegBytes <= 0 || jpegBytes > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(jpegBytes));
        }
        var begin = Report(0x71);
        begin[2] = 0x01;
        begin[3] = 0x01;
        var flush = Report(0xF1);
        var slot = Report(0x72);
        slot[2] = 0x01;
        slot[3] = 0x01;
        slot[4] = (byte)slotId;
        var stage = Report(0x73);
        stage[2] = 0x01;
        var announce = Report(0x7F);
        announce[2] = 0x02;
        announce[3] = (byte)jpegBytes;
        announce[4] = (byte)(jpegBytes >> 8);
        announce[5] = (byte)(jpegBytes >> 16);
        return new[] { (begin, 291), (flush, 25), (slot, 67), (stage, 112), (announce, 516) };
    }

    /// <summary>Sent after the last bulk transfer.</summary>
    public static byte[] EncodeUploadEnd()
    {
        var report = Report(0x73);
        report[2] = 0xFF;
        return report;
    }

    /// <summary>Shows the stored custom picture: the static mode with the custom source and its slot.</summary>
    public static byte[] EncodeCustomPicture(int slot)
    {
        var report = EncodeMode(ModeStaticAsset, slot);
        report[3] = 0x01;
        return report;
    }
}

