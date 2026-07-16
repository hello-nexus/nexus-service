using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Peripherals.Corsair.XeneonEdge;

/// <summary>
/// Pure builders/parsers for the Corsair Xeneon Edge orientation sensor and
/// native display settings, reverse-engineered from a live USBPcap capture
/// and bench read/write/read-back verification (T1 bench, 2026-07-15). No
/// IO; <see cref="XeneonEdgeOrientationWorker"/> owns the HID handle.
///
/// 64-byte reports, byte 0 is report id 0x01:
/// <c>OUT: 01 MSGID 00 00 00 00 + zero pad</c> (query),
/// <c>IN:  01 MSGID 00 00 00 LEN PAYLOAD...</c> (response), where LEN is
/// byte 5 and PAYLOAD starts at byte 6. The orientation message (0x11) has
/// LEN=0x02, payload <c>00 &lt;code&gt;</c> - the code is byte 7. The settings
/// block (0x0e) also uses this LEN framing. The set command (0x0f) does NOT:
/// its OUT byte 5 is always 0x00 and its GROUP/ITEM/VALUE sit at fixed bytes
/// 6/7/8 regardless of a length field; applying the LEN rule to 0x0f makes
/// every write look like a no-op.
///
/// The device reports nothing until armed: send <see cref="BuildOrientationQuery"/>
/// once after opening. It replies with the current orientation, then pushes an
/// unsolicited 0x11 report on every change with no further host traffic. This
/// is a debounced quadrant classifier (codes 0-3), not raw accelerometer axes -
/// intermediate tilt produces no report.
/// </summary>
public static class XeneonEdgeProtocol
{
    public const int VendorId = 0x1B1C;
    public const int ProductId = 0x1D0D;
    public const int UsagePage = 0xFF1B;
    public const int Usage = 0x0091;

    public const byte ReportId = 0x01;
    public const int ReportLength = 64;

    public const byte MsgIdOrientation = 0x11;
    public const byte MsgIdSettingsBlock = 0x0e;
    public const byte MsgIdSet = 0x0f;
    private const int LengthByteOffset = 5;
    private const int PayloadOffset = 6;
    private const byte OrientationPayloadLength = 0x02;
    private const byte SettingsBlockPayloadLength = 0x1d;

    // Set (0x0f) OUT command: GROUP/ITEM/VALUE at fixed bytes, no LEN field.
    private const int SetGroupOffset = 6;
    private const int SetItemOffset = 7;
    private const int SetValueOffset = 8;

    // Set (0x0f) ack: same byte positions carry GROUP/OK-flag/VALUE-echo -
    // a different meaning per byte than the OUT command above, kept as
    // separate named offsets so the two are never conflated.
    private const int AckGroupOffset = 6;
    private const int AckOkOffset = 7;
    private const int AckValueOffset = 8;
    private const byte AckOk = 0x01;

    /// <summary>Arms the orientation push stream: <c>01 11 00 00 00 00</c> + zero pad to 64.</summary>
    public static byte[] BuildOrientationQuery()
    {
        var buf = new byte[ReportLength];
        buf[0] = ReportId;
        buf[1] = MsgIdOrientation;
        return buf;
    }

    /// <summary>
    /// Parses a 64-byte interrupt-IN report. Returns false for anything that
    /// is not a well-formed 0x11 orientation response (wrong msgid, short
    /// read, unexpected payload length).
    /// </summary>
    public static bool TryParseOrientationReport(ReadOnlySpan<byte> report, out byte code)
    {
        code = 0;
        if (report.Length < PayloadOffset + OrientationPayloadLength) return false;
        if (report[0] != ReportId || report[1] != MsgIdOrientation) return false;
        if (report[LengthByteOffset] != OrientationPayloadLength) return false;
        code = report[PayloadOffset + 1];
        return true;
    }

    /// <summary>Queries the whole settings block: <c>01 0e 00 00 00 00</c> + zero pad to 64.</summary>
    public static byte[] BuildSettingsQuery()
    {
        var buf = new byte[ReportLength];
        buf[0] = ReportId;
        buf[1] = MsgIdSettingsBlock;
        return buf;
    }

    /// <summary>Writes one control: <c>01 0f 00 00 00 00 GROUP ITEM VALUE</c> + zero pad to 64.</summary>
    public static byte[] BuildSetCommand(byte group, byte item, byte value)
    {
        var buf = new byte[ReportLength];
        buf[0] = ReportId;
        buf[1] = MsgIdSet;
        buf[SetGroupOffset] = group;
        buf[SetItemOffset] = item;
        buf[SetValueOffset] = value;
        return buf;
    }

    /// <summary>
    /// Parses a 0x0e settings-block reply. <paramref name="block"/> is only
    /// valid when this returns true.
    /// </summary>
    public static bool TryParseSettingsBlock(ReadOnlySpan<byte> report, out XeneonEdgeSettingsBlock block)
    {
        block = default;
        if (report.Length < PayloadOffset + SettingsBlockPayloadLength) return false;
        if (report[0] != ReportId || report[1] != MsgIdSettingsBlock) return false;
        if (report[LengthByteOffset] != SettingsBlockPayloadLength) return false;
        var p = report.Slice(PayloadOffset);
        block = new XeneonEdgeSettingsBlock(
            Brightness: p[0],
            Backlight: p[1],
            Contrast: p[2],
            Red: p[3],
            Green: p[4],
            Blue: p[5]);
        return true;
    }

    /// <summary>
    /// Parses a 0x0f set-ack. Returns false when the report is not a 0x0f
    /// reply for this device or the device reported failure (byte 7 != 0x01).
    /// </summary>
    public static bool TryParseSetAck(ReadOnlySpan<byte> report, out byte group, out byte value)
    {
        group = 0;
        value = 0;
        if (report.Length < AckValueOffset + 1) return false;
        if (report[0] != ReportId || report[1] != MsgIdSet) return false;
        if (report[AckOkOffset] != AckOk) return false;
        group = report[AckGroupOffset];
        value = report[AckValueOffset];
        return true;
    }

    /// <summary>
    /// Sensor code (0-3) to <see cref="DisplayOrientations"/>: the sensor code IS the DMDO
    /// value (0=Landscape, 1=Portrait, 2=LandscapeFlipped, 3=PortraitFlipped). Established
    /// on the bench with auto-orient disabled (so nothing re-applied over the probe), by
    /// setting each orientation and confirming by eye that it rendered upright.
    ///
    /// A display rect cannot establish this table: it separates portrait from landscape but
    /// never which of the two flips. An earlier table had the landscape entries 180 deg out
    /// while portrait was correct, which is the signature of a reflected (not rotated) map.
    /// </summary>
    public static readonly string[] OrientationBySensorCode =
    {
        DisplayOrientations.Landscape,        // code 0
        DisplayOrientations.Portrait,         // code 1 (measured)
        DisplayOrientations.LandscapeFlipped, // code 2 (measured)
        DisplayOrientations.PortraitFlipped,  // code 3
    };

    /// <summary>Maps a sensor code to a display orientation, or null when out of range.</summary>
    public static string? ResolveOrientation(byte code)
        => code < OrientationBySensorCode.Length ? OrientationBySensorCode[code] : null;
}

/// <summary>Parsed 0x0e settings-block payload. Byte order here is the BLOCK order, not the write order.</summary>
public readonly record struct XeneonEdgeSettingsBlock(int Brightness, int Backlight, int Contrast, int Red, int Green, int Blue);

/// <summary>The six controls exposed on the Xeneon Edge's vendor settings channel.</summary>
public enum XeneonEdgeControl
{
    Brightness,
    Backlight,
    Contrast,
    Red,
    Green,
    Blue,
}

/// <summary>
/// One control's write coordinates (msgid 0x0f GROUP/ITEM) and its offset in
/// the msgid 0x0e settings-block payload. The write order (backlight=item0,
/// contrast=item1, brightness=item2) and the block order (brightness=byte0,
/// backlight=byte1, contrast=byte2) differ, so both are recorded per control
/// instead of one being derived from the other.
/// </summary>
public readonly record struct XeneonEdgeControlCoords(byte Group, byte Item, int BlockIndex, int Min, int Max);

/// <summary>Bench-measured (group, item, block index, range) per control. See <see cref="XeneonEdgeProtocol"/>.</summary>
public static class XeneonEdgeControls
{
    /// <summary>Reads one control's current value out of a parsed settings block.</summary>
    public static int Read(XeneonEdgeSettingsBlock block, XeneonEdgeControl control) => control switch
    {
        XeneonEdgeControl.Brightness => block.Brightness,
        XeneonEdgeControl.Backlight => block.Backlight,
        XeneonEdgeControl.Contrast => block.Contrast,
        XeneonEdgeControl.Red => block.Red,
        XeneonEdgeControl.Green => block.Green,
        XeneonEdgeControl.Blue => block.Blue,
        _ => -1,
    };

    public static readonly IReadOnlyDictionary<XeneonEdgeControl, XeneonEdgeControlCoords> Coords =
        new Dictionary<XeneonEdgeControl, XeneonEdgeControlCoords>
        {
            [XeneonEdgeControl.Backlight] = new XeneonEdgeControlCoords(Group: 0x02, Item: 0x00, BlockIndex: 1, Min: 0, Max: 100),
            [XeneonEdgeControl.Contrast] = new XeneonEdgeControlCoords(Group: 0x02, Item: 0x01, BlockIndex: 2, Min: 0, Max: 100),
            [XeneonEdgeControl.Brightness] = new XeneonEdgeControlCoords(Group: 0x02, Item: 0x02, BlockIndex: 0, Min: 0, Max: 100),
            [XeneonEdgeControl.Red] = new XeneonEdgeControlCoords(Group: 0x03, Item: 0x01, BlockIndex: 3, Min: 0, Max: 255),
            [XeneonEdgeControl.Green] = new XeneonEdgeControlCoords(Group: 0x03, Item: 0x02, BlockIndex: 4, Min: 0, Max: 255),
            [XeneonEdgeControl.Blue] = new XeneonEdgeControlCoords(Group: 0x03, Item: 0x03, BlockIndex: 5, Min: 0, Max: 255),
        };
}

/// <summary>Factory-default values measured on an untouched panel; RGB re-confirmed after a colors restore.</summary>
public static class XeneonEdgeDefaults
{
    public const int Brightness = 50;
    public const int Backlight = 100;
    public const int Contrast = 50;
    public const int Red = 151;
    public const int Green = 127;
    public const int Blue = 139;

    /// <summary>Every control paired with its factory value, for a full restore.</summary>
    public static readonly (XeneonEdgeControl Control, int Value)[] All =
    {
        (XeneonEdgeControl.Brightness, Brightness),
        (XeneonEdgeControl.Backlight, Backlight),
        (XeneonEdgeControl.Contrast, Contrast),
        (XeneonEdgeControl.Red, Red),
        (XeneonEdgeControl.Green, Green),
        (XeneonEdgeControl.Blue, Blue),
    };
}
