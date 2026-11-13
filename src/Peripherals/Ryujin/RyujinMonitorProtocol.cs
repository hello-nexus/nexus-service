using System;
using System.Globalization;
using System.Text;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>
/// The Ryujin III Extreme's built-in hardware-monitor screen, driven the way Armoury Crate does:
/// a startup sequence that selects the layout, then sensor text slots resent every cycle. Every
/// frame here was seen in two Armoury Crate captures; what the device does with them is inferred
/// from their position in those captures, and no reply was captured.
/// </summary>
internal static class RyujinMonitorProtocol
{
    private const byte Id = RyujinCoolingProtocol.ReportId;
    private const int NameOffset = 4;
    private const int ValueOffset = 22;

    public const int DefaultBrightness = 0x28;
    public const int MinBrightness = 10;
    public const int MaxBrightness = 100;

    /// <summary>The screen pauses after the reset before it takes more.</summary>
    public const int ResetGapMs = 500;

    /// <summary>Gap Armoury Crate leaves between selecting a layout and committing it.</summary>
    public const int LayoutGapMs = 100;

    public const string CpuName = "CPU";
    public const string GpuName = "GPU";
    public const string PumpName = "AIO Pump";

    public const int CpuSlot = 0;
    public const int GpuSlot = 1;
    public const int PumpSlot = 2;

    private const string DegreeCelsius = "℃";

    /// <summary>The glyph Armoury Crate appends to an rpm value (U+218C).</summary>
    private const string RpmGlyph = "↌";

    public static byte[] EncodeReset() => Simple(0xD0);

    /// <summary>Sent around every settings change and twice at startup; its meaning is not known.</summary>
    public static byte[] EncodeDc() => Simple(0xDC);

    public static byte[] EncodeFirmwareQuery() => Simple(0x82);

    /// <summary>Month, day, weekday (Monday 1 to Sunday 7), hour, minute, second as BCD.</summary>
    public static byte[] EncodeClock(DateTime local)
    {
        var report = Simple(0x11);
        report[2] = 0xC6;
        report[3] = Bcd(local.Month);
        report[4] = Bcd(local.Day);
        report[5] = (byte)(local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek);
        report[6] = 0x00;
        report[7] = Bcd(local.Hour);
        report[8] = Bcd(local.Minute);
        report[9] = Bcd(local.Second);
        report[10] = 0x01;
        return report;
    }

    /// <summary>The colors report as sent at startup: the default style and six white groups.</summary>
    public static byte[] EncodeColors()
    {
        var report = Simple(0x5E);
        report[4] = 0x01;
        report[6] = 0x01;
        for (int group = 0; group < 6; group++)
        {
            int at = 8 + group * 4;
            report[at] = 0xFF;
            report[at + 1] = 0xFF;
            report[at + 2] = 0xFF;
        }
        return report;
    }

    /// <summary>Selects the hardware-monitor layout: a layout report, then the commit.</summary>
    public static byte[][] EncodeSelectMonitorLayout()
    {
        var layout = Simple(0x5D);
        layout[2] = 0x00;
        layout[3] = 0x01;
        layout[4] = 0x21;
        layout[5] = 0x00;
        layout[6] = 0x01;
        layout[7] = 0x00;
        layout[8] = 0x00;
        layout[9] = 0x05;
        var commit = Simple(0x51);
        commit[2] = 0x1F;
        return new[] { layout, commit };
    }

    /// <summary>One sensor text slot: the name from byte 4, the value and its unit glyph from byte 22.</summary>
    public static byte[] EncodeSensorText(int slot, string name, string value)
    {
        var report = Simple(0x5F);
        report[3] = (byte)slot;
        WriteText(report, NameOffset, ValueOffset - NameOffset, name);
        WriteText(report, ValueOffset, RyujinCoolingProtocol.ReportLength - ValueOffset, value);
        return report;
    }

    public static string FormatTemperature(double celsius) =>
        celsius.ToString("0.0", CultureInfo.InvariantCulture) + DegreeCelsius;

    public static string FormatRpm(int rpm) =>
        rpm.ToString(CultureInfo.InvariantCulture) + RpmGlyph;

    /// <summary>
    /// The settings report in its single form. Armoury Crate sends it with the three-packet burst
    /// below for a brightness change and alone, followed by the layout, for a rotation change.
    /// </summary>
    public static byte[] EncodeDisplay(int rotationDegrees, int brightnessPercent) => Display(rotationDegrees, brightnessPercent, tail: true, marker: false);

    /// <summary>
    /// A brightness change: the previous settings long and short, then the new ones, each behind a
    /// DC report, as in the capture.
    /// </summary>
    public static byte[][] EncodeBrightnessBurst(int rotationDegrees, int oldPercent, int newPercent) => new[]
    {
        EncodeDc(),
        Display(rotationDegrees, oldPercent, tail: true, marker: true),
        EncodeDc(),
        Display(rotationDegrees, oldPercent, tail: false, marker: false),
        EncodeDc(),
        Display(rotationDegrees, newPercent, tail: true, marker: false),
    };

    /// <summary>A rotation change: the settings, DC twice, then the clock and layout again.</summary>
    public static byte[][] EncodeRotationChange(DateTime local, int rotationDegrees, int brightnessPercent)
    {
        var select = EncodeSelectMonitorLayout();
        return new[]
        {
            EncodeDc(),
            EncodeDisplay(rotationDegrees, brightnessPercent),
            EncodeDc(),
            EncodeDc(),
            EncodeClock(local),
            EncodeColors(),
            select[0],
            select[1],
        };
    }

    /// <summary>
    /// The startup run once on attach, in the order Armoury Crate sends it, then the stored
    /// brightness and rotation, placed before the clock and layout as in the captured rotation
    /// change. The reset may have cleared them, and a change made while the cooler was away
    /// never reached it.
    /// </summary>
    public static byte[][] EncodeStartup(DateTime local, int rotationDegrees, int brightnessPercent)
    {
        var select = EncodeSelectMonitorLayout();
        return new[]
        {
            EncodeReset(),
            EncodeDc(),
            EncodeDc(),
            EncodeDc(),
            EncodeDisplay(rotationDegrees, brightnessPercent),
            EncodeDc(),
            EncodeDc(),
            EncodeClock(local),
            EncodeColors(),
            select[0],
            select[1],
            EncodeFirmwareQuery(),
        };
    }

    /// <summary>How long Armoury Crate waits after a report with this header.</summary>
    public static int GapAfterMs(byte header) => header switch
    {
        0xD0 => ResetGapMs,
        0x5D => LayoutGapMs,
        _ => 0,
    };

    private static byte[] Display(int rotationDegrees, int brightness, bool tail, bool marker)
    {
        byte b = (byte)Math.Clamp(brightness, 0, 100);
        var report = Simple(0x5C);
        report[2] = 0x01;
        report[3] = (byte)(rotationDegrees == 90 ? 0x5A : 0x00);
        report[5] = b;
        report[9] = b;
        report[10] = 0x14;
        if (tail)
        {
            report[15] = b;
        }
        if (marker)
        {
            report[16] = 0x14;
        }
        return report;
    }

    private static byte[] Simple(byte header)
    {
        var report = new byte[RyujinCoolingProtocol.ReportLength];
        report[0] = Id;
        report[1] = header;
        return report;
    }

    private static void WriteText(byte[] report, int offset, int capacity, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        int n = Math.Min(bytes.Length, capacity);
        Array.Copy(bytes, 0, report, offset, n);
    }

    private static byte Bcd(int value) => (byte)(((value / 10) << 4) | (value % 10));
}
