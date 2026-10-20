using System;
using System.Text;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The LCD on the ASUS ROG Crosshair X870E Glacial motherboard (USB VID/PID in <see cref="ProductId"/>), decoded from
/// MB Manager traffic. Commands are fixed-size HID output reports (<see cref="ReportBytes"/>)
/// led by <see cref="ReportId"/>. The picture upload over the vendor bulk interface is not
/// implemented.
///
/// Device replies are HID input reports that echo the opcode; the 0x5C state reply is the
/// only one with a payload and has the same layout as the 0x5C write.
/// </summary>
public static class GlacialLcdProtocol
{
    public const int ProductId = 0x1D93;
    public const int ReportBytes = 65;
    public const byte ReportId = 0xEC;

    /// <summary>Mode byte of a 0x51 report.</summary>
    public const byte ModeOff = 0x00;
    public const byte ModeStaticAsset = 0x11;
    public const byte ModeAnimatedAsset = 0x14;
    public const byte ModeHardwareMonitor = 0x21;

    /// <summary>Predefined static picture ids the vendor app was seen selecting.</summary>
    public static readonly int[] StaticAssets = { 3, 4 };

    /// <summary>Predefined animation ids the vendor app was seen selecting.</summary>
    public static readonly int[] AnimatedAssets = { 0, 1 };

    /// <summary>Most widgets the hardware monitor shows at once.</summary>
    public const int MaxMonitorSlots = 3;

    /// <summary>Label field of a slot report, from the byte after the slot index up to <see cref="SlotValueOffset"/>.</summary>
    public const int SlotLabelBytes = 18;
    public const int SlotValueOffset = 21;

    /// <summary>Value of the 0x5C display field while the LCD is on.</summary>
    public const byte DisplayOnFlag = 0x14;

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
    /// Settings write. Brightness (percent) appears three times, then a display flag (on or
    /// off, not the picture mode: the state reply still held the on value while a static
    /// picture was selected) and the sleep behavior. The app echoes every field from the last
    /// 0x5C reply and changes one.
    /// </summary>
    public static byte[] EncodeSettings(int brightnessPercent, bool displayOn, bool keepOnWhileAsleep)
    {
        var report = Report(0x5C);
        report[2] = 0x01;
        PatchSettings(report, brightnessPercent, displayOn, keepOnWhileAsleep);
        return report;
    }

    /// <summary>
    /// The read-modify-write form: the fresh state reply with only the given fields changed
    /// and the write flag set, so every other byte, set or not in the captures, is carried
    /// through unchanged.
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

    private static void PatchSettings(byte[] report, int brightnessPercent, bool displayOn, bool keepOnWhileAsleep)
    {
        byte percent = (byte)Math.Clamp(brightnessPercent, 0, 100);
        report[5] = percent;
        report[9] = percent;
        report[10] = displayOn ? DisplayOnFlag : (byte)0;
        report[15] = percent;
        report[16] = keepOnWhileAsleep ? SleepKeepOn : (byte)0;
    }

    /// <summary>Reads the fields back out of a 0x5C state reply, or null when it is some other report.</summary>
    public static (int BrightnessPercent, bool DisplayOn, bool KeepOnWhileAsleep)? ParseState(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 17 || reply[0] != ReportId || reply[1] != 0x5C)
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
}
