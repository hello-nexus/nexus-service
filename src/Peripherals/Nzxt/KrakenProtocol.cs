using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Nzxt;

// Byte facts decoded from the NZXT Kraken Elite V2 (1E71:3012, firmware 1.2.0) on the
// bench and cross-checked against liquidctl's kraken3.py. Everything encoded here was
// exercised against real hardware.
//
// THE trap: this firmware uses 512-byte HID reports, not the 64-byte reports liquidctl
// uses for older Krakens. A short write is rejected outright (Windows returns failure)
// while the device keeps pushing unsolicited status reports, so a caller that ignores the
// write result looks healthy while every command is dropped. Always size buffers to
// ReportLength and always check the write result.
//
// The command byte doubles as the HID report ID and replies come back on report ID+1
// (0x10 -> 0x11, 0x30 -> 0x31, 0x38 -> 0x39, 0x74 -> 0x75, ...). Report 0xFF is the
// device's NAK and echoes the rejected command in bytes [14],[15].
internal static class KrakenProtocol
{
    public const int VendorId = 0x1E71;

    /// <summary>
    /// Every product id <see cref="KrakenModel.All"/> covers, for the two places that only
    /// need to answer "is this one of ours" - HID enumeration and the WinUSB path match.
    /// </summary>
    public static IReadOnlyList<int> ProductIds { get; } = BuildProductIds();

    private static int[] BuildProductIds()
    {
        var ids = new int[KrakenModel.All.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = KrakenModel.All[i].ProductId;
        }
        return ids;
    }

    public const int ReportLength = 512;

    // The bucket store is flash-backed and holds 16 slots, addressed in 1 KiB pages.
    public const int BucketCount = 16;
    public const int BucketPageBytes = 1024;

    // Pump duty floored so coolant keeps circulating; the firmware itself enforces 20.
    public const int PumpDutyFloor = 20;

    // A curve is one duty per degree from 20 C to 59 C inclusive.
    public const int CurvePointCount = 40;
    public const int CurveFirstTempC = 20;

    private const byte ReportFirmwareRequest = 0x10;
    private const byte ReportFirmwareReply = 0x11;
    private const byte ReportLightingInfoRequest = 0x20;
    private const byte ReportLightingInfoReply = 0x21;
    private const byte ReportSetColor = 0x2A;
    private const byte ReportLcdRequest = 0x30;
    private const byte ReportLcdReply = 0x31;
    private const byte ReportBucketRequest = 0x32;
    private const byte ReportBucketReply = 0x33;
    private const byte ReportTransferRequest = 0x36;
    private const byte ReportTransferReply = 0x37;
    private const byte ReportDisplayModeRequest = 0x38;
    private const byte ReportDisplayModeReply = 0x39;
    private const byte ReportStatusRequest = 0x74;
    private const byte ReportStatusReply = 0x75;
    private const byte ReportSpeedCurve = 0x72;
    private const byte ReportInit = 0x70;
    public const byte ReportNak = 0xFF;

    // Sub-commands of the 0x30 LCD family.
    private const byte LcdSubInfo = 0x01;
    private const byte LcdSubSetBacklight = 0x02;
    private const byte LcdSubReadMode = 0x03;
    private const byte LcdSubQueryBucket = 0x04;

    // Payload offset shared by every reply: 14 bytes of report id + serial + padding.
    private const int ReplyPayloadOffset = 14;

    // Byte [14] of an acknowledged reply. Any other value is a failure code.
    public const byte AckOk = 0x01;

    /// <summary>
    /// Speed channels. The trailing bytes are part of the channel identifier, not padding;
    /// both tuples were confirmed by commanding a duty and reading it back in the status report.
    /// </summary>
    public static ReadOnlySpan<byte> PumpChannel => new byte[] { 0x01, 0x01, 0x00 };

    public static ReadOnlySpan<byte> FanChannel => new byte[] { 0x02, 0x01, 0x01 };

    /// <summary>
    /// The tuples the Kraken and Kraken Elite used before their firmware 2.1.1. Only those
    /// two models ever moved: the Elite V2's own 1.x firmware line ships the newer pair
    /// above, so it must not be version-gated onto these.
    /// </summary>
    public static ReadOnlySpan<byte> LegacyPumpChannel => new byte[] { 0x01, 0x00, 0x00 };

    public static ReadOnlySpan<byte> LegacyFanChannel => new byte[] { 0x02, 0x00, 0x00 };

    /// <summary>Firmware at or past which a Kraken/Kraken Elite wants the newer tuples.</summary>
    public static bool UsesNewSpeedChannels(KrakenFirmware? firmware)
    {
        if (firmware is not { } fw)
        {
            return false;
        }
        return fw.Major > 2
            || (fw.Major == 2 && fw.Minor > 1)
            || (fw.Major == 2 && fw.Minor == 1 && fw.Patch >= 1);
    }

    /// <summary>
    /// Lighting channel ids. The Elite V2 reports two channels: the pump ring and whatever
    /// RGB fans are daisy-chained off it. Ids are a bitmask, so Ring|Fans addresses both.
    /// </summary>
    public const byte ColorChannelRing = 0b001;
    public const byte ColorChannelFans = 0b010;

    // Per-channel constant the firmware uses for animation phase; from liquidctl _STATIC_VALUE.
    private static byte StaticValueFor(byte channelId) => channelId switch
    {
        ColorChannelRing => 40,
        ColorChannelFans => 8,
        _ => 40,
    };

    private static byte[] NewReport(byte reportId, byte subCommand)
    {
        var report = new byte[ReportLength];
        report[0] = reportId;
        report[1] = subCommand;
        return report;
    }

    public static byte[] EncodeFirmwareRequest() => NewReport(ReportFirmwareRequest, 0x01);

    public static byte[] EncodeStatusRequest() => NewReport(ReportStatusRequest, 0x01);

    public static byte[] EncodeLightingInfoRequest() => NewReport(ReportLightingInfoRequest, 0x03);

    // Telemetry-stream setup, sent once per connection. 0xB8 with an interval index of 1
    // is the half-second cadence liquidctl uses; the cooler will not report its accessory
    // table until reporting has been started.
    public static byte[] EncodeSetUpdateInterval()
    {
        var report = NewReport(ReportInit, 0x02);
        report[2] = 0x01;
        report[3] = 0xB8;
        report[4] = 0x01;
        return report;
    }

    public static byte[] EncodeStartReporting() => NewReport(ReportInit, 0x01);

    public static byte[] EncodeLcdInfoRequest() => NewReport(ReportLcdRequest, LcdSubInfo);

    public static byte[] EncodeReadDisplayModeRequest() => NewReport(ReportLcdRequest, LcdSubReadMode);

    public static byte[] EncodeQueryBucketRequest(int bucketIndex)
    {
        var report = NewReport(ReportLcdRequest, LcdSubQueryBucket);
        report[2] = (byte)bucketIndex;
        return report;
    }

    /// <summary>
    /// Backlight level and rotation ride the same command, so a caller changing one must
    /// pass the current value of the other or it will be overwritten.
    /// </summary>
    public static byte[] EncodeSetBacklight(int brightnessPercent, int orientationQuarterTurns)
    {
        var report = NewReport(ReportLcdRequest, LcdSubSetBacklight);
        report[2] = 0x01;
        report[3] = (byte)Math.Clamp(brightnessPercent, 0, 100);
        report[6] = 0x01;
        report[7] = (byte)(orientationQuarterTurns & 0x03);
        return report;
    }

    public static byte[] EncodeSetDisplayMode(KrakenDisplayMode mode, int bucketIndex)
    {
        var report = NewReport(ReportDisplayModeRequest, 0x01);
        report[2] = (byte)mode;
        report[3] = (byte)bucketIndex;
        return report;
    }

    public static byte[] EncodeDeleteBucket(int bucketIndex)
    {
        var report = NewReport(ReportBucketRequest, 0x02);
        report[2] = (byte)bucketIndex;
        return report;
    }

    /// <summary>
    /// Reserves <paramref name="pages"/> KiB pages at <paramref name="startPage"/> for a bucket.
    /// Callers must delete every bucket first: a stale allocation makes this return success while
    /// placing the image where the panel will never render it.
    /// </summary>
    public static byte[] EncodeSetupBucket(int bucketIndex, int startPage, int pages)
    {
        var report = NewReport(ReportBucketRequest, 0x01);
        report[2] = (byte)bucketIndex;
        report[3] = (byte)(bucketIndex + 1);
        report[4] = (byte)(startPage & 0xFF);
        report[5] = (byte)((startPage >> 8) & 0xFF);
        report[6] = (byte)(pages & 0xFF);
        report[7] = (byte)((pages >> 8) & 0xFF);
        report[8] = 0x01;
        return report;
    }

    public static byte[] EncodeStartTransfer(int bucketIndex)
    {
        var report = NewReport(ReportTransferRequest, 0x01);
        report[2] = (byte)bucketIndex;
        return report;
    }

    public static byte[] EncodeEndTransfer() => NewReport(ReportTransferRequest, 0x02);

    /// <summary>Aborts any half-finished transfer; CAM and liquidctl send it before every upload.</summary>
    public static byte[] EncodeCancelTransfers() => NewReport(ReportTransferRequest, 0x03);

    /// <summary>
    /// Starts a live frame straight to the panel, no bucket: <c>36 01 00 01 09</c>, as
    /// captured from NZXT CAM driving the 2023 Kraken Elite. Byte 4 is the bulk format.
    /// </summary>
    public static byte[] EncodeStartDirectTransfer()
    {
        var report = NewReport(ReportTransferRequest, 0x01);
        report[3] = 0x01;
        report[4] = BulkFormatBgr888;
        return report;
    }

    /// <summary>
    /// One duty per degree from 20 C to 59 C. <paramref name="duties"/> must hold
    /// <see cref="CurvePointCount"/> entries.
    /// </summary>
    public static byte[] EncodeSpeedCurve(ReadOnlySpan<byte> channel, ReadOnlySpan<byte> duties)
    {
        if (duties.Length != CurvePointCount)
        {
            throw new ArgumentException($"curve needs {CurvePointCount} points", nameof(duties));
        }
        var report = new byte[ReportLength];
        report[0] = ReportSpeedCurve;
        channel.CopyTo(report.AsSpan(1, channel.Length));
        duties.CopyTo(report.AsSpan(1 + channel.Length, CurvePointCount));
        return report;
    }

    /// <summary>
    /// Per-LED or animated lighting. <paramref name="rgbColors"/> is packed RGB triplets,
    /// at most <see cref="MaxColors"/> of them; this method performs the RGB to GRB swap.
    /// </summary>
    public static byte[] EncodeColors(
        byte channelId,
        KrakenColorMode mode,
        KrakenAnimationSpeed speed,
        ReadOnlySpan<byte> rgbColors,
        bool forward)
    {
        var report = new byte[ReportLength];
        report[0] = ReportSetColor;
        report[1] = 0x04;
        report[2] = channelId;
        report[3] = channelId;
        report[4] = mode.ModeByte;

        var timing = SpeedTiming(mode.SpeedScale, speed);
        report[5] = timing.Lo;
        report[6] = timing.Hi;

        int colorCount = Math.Min(rgbColors.Length / 3, MaxColors);
        for (int i = 0; i < colorCount; i++)
        {
            int src = i * 3;
            int dst = ColorBlockOffset + (i * 3);
            report[dst] = rgbColors[src + 1];     // G
            report[dst + 1] = rgbColors[src];     // R
            report[dst + 2] = rgbColors[src + 2]; // B
        }

        // Direction rides the same byte as the animation's own base value: backward adds 2.
        report[FooterOffset] = (byte)(mode.BackwardBase + (forward ? 0x00 : 0x02));
        report[FooterOffset + 1] = mode.ColorCountOverride ?? (byte)colorCount;
        report[FooterOffset + 2] = mode.ModeRelated;
        report[FooterOffset + 3] = StaticValueFor(channelId);
        report[FooterOffset + 4] = mode.LedSize;
        return report;
    }

    /// <summary>Per-LED addressing accepts up to this many colours per channel.</summary>
    public const int MaxDirectColors = 40;

    private const byte ReportChannelColors = 0x26;

    /// <summary>
    /// One report carrying every LED on a channel, GRB, applied on arrival - no separate
    /// latch or apply command. <paramref name="rgbColors"/> is packed RGB triplets; unused
    /// slots stay zero.
    ///
    /// This replaced a three-report `0x22 10/11/A0` sequence that split the channel across
    /// two colour tables. The split was the source of a dark arc at the top of the ring:
    /// LED 23 sits at 11 o'clock and rode the second table. Taken from NZXT's own wire
    /// format, which this firmware is happiest with; measured at 0.14 ms/frame against
    /// 0.62 ms for the sequence it replaced.
    /// </summary>
    public static byte[] EncodeChannelColors(byte channelId, ReadOnlySpan<byte> rgbColors)
    {
        var report = new byte[ReportLength];
        report[0] = ReportChannelColors;
        report[1] = 0x14;
        report[2] = channelId;
        report[3] = channelId;

        int count = Math.Min(rgbColors.Length / 3, MaxDirectColors);
        for (int i = 0; i < count; i++)
        {
            int src = i * 3;
            int dst = 4 + (i * 3);
            report[dst] = rgbColors[src + 1];     // G
            report[dst + 1] = rgbColors[src];     // R
            report[dst + 2] = rgbColors[src + 2]; // B
        }
        return report;
    }

    private const byte ReportStreamColors = 0x22;

    /// <summary>Bytes of colour one 0x22 table carries; 4 header bytes precede them in a 64-byte report.</summary>
    public const int StreamedColorBytesPerTable = 60;

    /// <summary>The firmware exposes exactly two staging tables per channel.</summary>
    public const int StreamedColorTables = 2;

    /// <summary>Two 60-byte tables of GRB, so 40 LEDs - the same ceiling as the 0x26 path.</summary>
    public const int MaxStreamedColors = StreamedColorTables * StreamedColorBytesPerTable / 3;

    /// <summary>
    /// The pre-Elite-V2 per-LED path: colours are staged into two tables and do nothing
    /// until <see cref="EncodeSubmitColors"/> latches them. Both tables are always written,
    /// including an empty second one - a stale table left behind keeps lighting its LEDs.
    /// <paramref name="rgbColors"/> is packed RGB triplets; this performs the RGB to GRB swap.
    /// </summary>
    public static byte[][] EncodeStreamedColors(byte channelId, ReadOnlySpan<byte> rgbColors)
    {
        int count = Math.Min(rgbColors.Length / 3, MaxStreamedColors);
        var reports = new byte[StreamedColorTables][];
        for (int table = 0; table < StreamedColorTables; table++)
        {
            var report = new byte[ReportLength];
            report[0] = ReportStreamColors;
            report[1] = (byte)(0x10 | table);
            report[2] = channelId;
            report[3] = 0x00;
            reports[table] = report;
        }
        for (int i = 0; i < count; i++)
        {
            int src = i * 3;
            int flat = i * 3;
            int table = flat / StreamedColorBytesPerTable;
            int dst = 4 + (flat % StreamedColorBytesPerTable);
            var report = reports[table];
            report[dst] = rgbColors[src + 1];     // G
            report[dst + 1] = rgbColors[src];     // R
            report[dst + 2] = rgbColors[src + 2]; // B
        }
        return reports;
    }

    /// <summary>
    /// Latches whatever the two staged tables hold onto the channel. The tail is the
    /// documented constant, sent verbatim; byte 7 is the channel's LED
    /// budget (0x28 = 40) rather than the count actually staged.
    /// </summary>
    public static byte[] EncodeSubmitColors(byte channelId)
    {
        var report = new byte[ReportLength];
        report[0] = ReportStreamColors;
        report[1] = 0xA0;
        report[2] = channelId;
        report[3] = 0x00;
        report[4] = 0x01;
        report[7] = 0x28;
        report[10] = 0x80;
        report[12] = 0x32;
        report[15] = 0x01;
        return report;
    }

    // Colour block starts right after the 7-byte header and holds 16 GRB triplets;
    // the 5-byte footer follows it.
    private const int ColorBlockOffset = 7;
    public const int MaxColors = 16;
    private const int FooterOffset = ColorBlockOffset + (MaxColors * 3);

    // Magic prefix every bulk (LCD pixel) transfer starts with.
    public static ReadOnlySpan<byte> BulkMagic => new byte[]
    {
        0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10,
    };

    // Wire pixel format. Only Rgba8888 is accepted by this firmware for the bucket path:
    // Rgb565 is taken without complaint and then silently ignored, the panel falling back
    // to its firmware readout. Values are the ordinals of CAM's own format enum.
    public const byte BulkFormatRgba8888 = 0x02;

    /// <summary>
    /// Q565 - the compressed format the panel also accepts on the bulk path. A real frame
    /// is 7-11 KB against 1,638,400 raw, which is the difference between ~2 fps and the
    /// bucket path's ~90. See <see cref="Q565Encoder"/>.
    /// </summary>
    public const byte BulkFormatQ565 = 0x08;

    /// <summary>Uncompressed RGB565. The 2023 Kraken (0x300E) takes only this.</summary>
    public const byte BulkFormatRgb565 = 0x06;

    /// <summary>Raw B G R, 3 bytes a pixel: CAM's live-frame format on the direct path.</summary>
    public const byte BulkFormatBgr888 = 0x09;

    /// <summary>
    /// The 20-byte preamble that precedes the pixels. It must be written as its own bulk
    /// transfer; concatenating it with the pixel data corrupts the upload silently.
    /// </summary>
    public static byte[] EncodeBulkHeader(byte format, int payloadBytes)
    {
        var header = new byte[BulkMagic.Length + 8];
        BulkMagic.CopyTo(header);
        int i = BulkMagic.Length;
        header[i] = format;
        header[i + 4] = (byte)(payloadBytes & 0xFF);
        header[i + 5] = (byte)((payloadBytes >> 8) & 0xFF);
        header[i + 6] = (byte)((payloadBytes >> 16) & 0xFF);
        header[i + 7] = (byte)((payloadBytes >> 24) & 0xFF);
        return header;
    }

    /// <summary>
    /// Rotates a square RGBA frame by whole quarter turns. The panel does not rotate stored
    /// bucket content: the orientation setting only affects the firmware's own readout, so a
    /// host-pushed image has to be rotated before upload or the setting appears to do nothing.
    /// </summary>
    public static byte[] RotateRgba(ReadOnlySpan<byte> rgba, int width, int height, int quarterTurns)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0)
        {
            return rgba.ToArray();
        }
        if (width != height)
        {
            throw new ArgumentException("rotation assumes a square panel", nameof(width));
        }
        var dst = new byte[rgba.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx, sy;
                switch (turns)
                {
                    case 1: sx = y; sy = height - 1 - x; break;
                    case 2: sx = width - 1 - x; sy = height - 1 - y; break;
                    default: sx = width - 1 - y; sy = x; break;
                }
                int from = ((sy * width) + sx) * 4;
                int to = ((y * width) + x) * 4;
                dst[to] = rgba[from];
                dst[to + 1] = rgba[from + 1];
                dst[to + 2] = rgba[from + 2];
                dst[to + 3] = rgba[from + 3];
            }
        }
        return dst;
    }

    /// <summary>
    /// Rotates and converts a frame into what the raw-RGBA wire format wants: R G B and a
    /// zero alpha byte. Any other alpha value mangles the colours, and the overlay hands
    /// frames back in capture order (BGRA), hence <paramref name="sourceIsBgra"/>.
    /// </summary>
    public static byte[] ToWireRgba(ReadOnlySpan<byte> frame, int width, int height, int quarterTurns, bool sourceIsBgra)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns != 0 && width != height)
        {
            throw new ArgumentException("rotation assumes a square panel", nameof(width));
        }
        var dst = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx, sy;
                switch (turns)
                {
                    case 1: sx = y; sy = height - 1 - x; break;
                    case 2: sx = width - 1 - x; sy = height - 1 - y; break;
                    case 3: sx = width - 1 - y; sy = x; break;
                    default: sx = x; sy = y; break;
                }
                int from = ((sy * width) + sx) * 4;
                int to = ((y * width) + x) * 4;
                dst[to] = frame[from + (sourceIsBgra ? 2 : 0)];
                dst[to + 1] = frame[from + 1];
                dst[to + 2] = frame[from + (sourceIsBgra ? 0 : 2)];
                dst[to + 3] = 0;
            }
        }
        return dst;
    }

    /// <summary>
    /// Rotates a frame into <paramref name="dst"/> as B G R, 3 bytes a pixel, for
    /// <see cref="BulkFormatBgr888"/>. Returns the bytes written.
    /// </summary>
    public static int ToWireBgr(ReadOnlySpan<byte> frame, int width, int height, int quarterTurns, bool sourceIsBgra, Span<byte> dst)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns != 0 && width != height)
        {
            throw new ArgumentException("rotation assumes a square panel", nameof(width));
        }
        int blue = sourceIsBgra ? 0 : 2;
        int red = sourceIsBgra ? 2 : 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx, sy;
                switch (turns)
                {
                    case 1: sx = y; sy = height - 1 - x; break;
                    case 2: sx = width - 1 - x; sy = height - 1 - y; break;
                    case 3: sx = width - 1 - y; sy = x; break;
                    default: sx = x; sy = y; break;
                }
                int from = ((sy * width) + sx) * 4;
                int to = ((y * width) + x) * 3;
                dst[to] = frame[from + blue];
                dst[to + 1] = frame[from + 1];
                dst[to + 2] = frame[from + red];
            }
        }
        return width * height * 3;
    }

    /// <summary>Pages a bucket must reserve to hold header plus payload.</summary>
    public static int PagesFor(int payloadBytes)
    {
        int total = BulkMagic.Length + 8 + payloadBytes;
        return (total + BucketPageBytes - 1) / BucketPageBytes;
    }

    public static bool IsReplyTo(ReadOnlySpan<byte> report, byte requestReportId, byte subCommand)
    {
        return report.Length > 1 && report[0] == requestReportId + 1 && report[1] == subCommand;
    }

    public static bool IsAck(ReadOnlySpan<byte> report) =>
        report.Length > ReplyPayloadOffset && report[ReplyPayloadOffset] == AckOk;

    public static bool IsStatusReply(ReadOnlySpan<byte> report) =>
        report.Length > 1 && report[0] == ReportStatusReply;

    /// <summary>
    /// Decodes the telemetry report. The device also pushes this unsolicited about once a
    /// second with sub-command 0x02, so callers should accept any 0x75 report.
    /// </summary>
    public static KrakenReading? DecodeStatus(ReadOnlySpan<byte> report)
    {
        if (!IsStatusReply(report) || report.Length < 26)
        {
            return null;
        }
        // 0xFF 0xFF in the temperature field is the documented firmware-fault marker.
        if (report[15] == 0xFF && report[16] == 0xFF)
        {
            return null;
        }
        double liquidC = report[15] + (report[16] / 10.0);
        int pumpRpm = (report[18] << 8) | report[17];
        int fanRpm = (report[24] << 8) | report[23];
        return new KrakenReading(liquidC, pumpRpm, report[19], fanRpm, report[25]);
    }

    public static KrakenFirmware? DecodeFirmware(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, ReportFirmwareRequest, 0x01) || report.Length < 0x14)
        {
            return null;
        }
        return new KrakenFirmware(report[0x11], report[0x12], report[0x13]);
    }

    public static KrakenLcdInfo? DecodeLcdInfo(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, ReportLcdRequest, LcdSubInfo) || report.Length < 28)
        {
            return null;
        }
        int width = report[20] | (report[21] << 8);
        int height = report[22] | (report[23] << 8);
        return new KrakenLcdInfo(report[24], report[26], width, height);
    }

    public static KrakenDisplayMode? DecodeDisplayMode(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, ReportLcdRequest, LcdSubReadMode) || report.Length <= ReplyPayloadOffset)
        {
            return null;
        }
        return (KrakenDisplayMode)report[ReplyPayloadOffset];
    }

    /// <summary>
    /// True when a queried bucket holds no asset. An occupied bucket carries its index,
    /// asset index, start page and page count from byte 14 onward.
    /// </summary>
    public static bool IsBucketEmpty(ReadOnlySpan<byte> report)
    {
        if (report.Length < 64)
        {
            return true;
        }
        // Byte 14 is the bucket's own index and is echoed even when unoccupied.
        for (int i = 15; i < 64; i++)
        {
            if (report[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Parses the accessory table. Byte 14 is the channel count; each channel then has
    /// <see cref="AccessorySlotsPerChannel"/> slots from byte 15, holding accessory type ids.
    /// </summary>
    public const int AccessorySlotsPerChannel = 6;

    public static int DecodeChannelCount(ReadOnlySpan<byte> report)
    {
        if (!IsReplyTo(report, ReportLightingInfoRequest, 0x03) || report.Length <= ReplyPayloadOffset)
        {
            return 0;
        }
        return report[ReplyPayloadOffset];
    }

    public static byte DecodeAccessory(ReadOnlySpan<byte> report, int channel, int slot)
    {
        int index = 15 + (channel * AccessorySlotsPerChannel) + slot;
        return index < report.Length ? report[index] : (byte)0;
    }

    /// <summary>
    /// LED count for an accessory type id. Values below are the ones this workspace has
    /// either measured or taken from OpenRGB's Hue 2 table; an unknown accessory reports 0
    /// so the caller can fall back rather than lighting a wrong-length strip.
    /// </summary>
    public static int LedCountForAccessory(byte accessoryId) => accessoryId switch
    {
        0x10 => 8,  // Kraken X3 pump ring
        0x11 => 1,  // Kraken X3 logo
        0x13 => 18, // F120 RGB
        0x14 => 18, // F140 RGB
        0x15 => 20, // F120 RGB Duo
        0x16 => 20, // F140 RGB Duo
        0x17 => 8,  // F120 RGB Core
        0x18 => 8,  // F140 RGB Core
        0x19 => 8,  // F120 RGB Core, case version
        0x1B => 16, // F240 RGB Core: two 8-LED fans
        0x1D => 24, // F360 RGB Core
        0x1E => 24, // Kraken Elite pump ring
        0x1F => 24, // F420 RGB
        _ => 0,
    };

    /// <summary>
    /// How an accessory's LEDs sit in space: how many rings it carries and how many LEDs
    /// go round each one. A multi-fan accessory (the F240/F360/F420 report as ONE id
    /// covering the whole radiator) is one ring per fan, which is what makes the LED map
    /// show two circles for an F240 instead of a 16-LED strip. Rings is 0 for an accessory
    /// whose geometry we have not measured; the caller then lays the LEDs out as one ring.
    /// </summary>
    public static (int Rings, int LedsPerRing) AccessoryRings(byte accessoryId) => accessoryId switch
    {
        0x10 => (1, 8),   // Kraken X3 pump ring
        0x11 => (1, 1),   // Kraken X3 logo
        0x17 => (1, 8),   // F120 RGB Core
        0x18 => (1, 8),   // F140 RGB Core
        0x19 => (1, 8),   // F120 RGB Core, case version
        0x1B => (2, 8),   // F240 RGB Core: two fans on one accessory id
        0x1D => (3, 8),   // F360 RGB Core: three fans
        0x1E => (1, 24),  // Kraken Elite pump ring
        0x1F => (3, 8),   // F420 RGB: three fans
        // F120/F140 RGB and the Duos put their LEDs on more than one ring per fan; the
        // split is unmeasured, so they stay a single ring until a unit is on the bench.
        _ => (0, 0),
    };

    /// <summary>
    /// True for the accessories that are a cooler's own pump ring rather than something
    /// chained off its RGB port. Which channel carries it moves across the line, so callers
    /// identify it by accessory id.
    /// </summary>
    public static bool IsPumpRingAccessory(byte accessoryId) =>
        accessoryId is 0x10 or 0x11 or 0x1E;

    public static string AccessoryName(byte accessoryId) => accessoryId switch
    {
        0x10 => "Kraken Pump Ring",
        0x11 => "Kraken Logo",
        0x13 => "F120 RGB",
        0x14 => "F140 RGB",
        0x15 => "F120 RGB Duo",
        0x16 => "F140 RGB Duo",
        0x17 => "F120 RGB Core",
        0x18 => "F140 RGB Core",
        0x19 => "F120 RGB Core",
        0x1B => "F240 RGB Core",
        0x1D => "F360 RGB Core",
        0x1E => "Kraken Elite Ring",
        0x1F => "F420 RGB",
        _ => "Unknown accessory",
    };

    private readonly record struct Timing(byte Lo, byte Hi);

    // liquidctl _SPEED_VALUE: per animation scale, five timings slowest..fastest.
    private static Timing SpeedTiming(int scale, KrakenAnimationSpeed speed)
    {
        var row = SpeedRows[Math.Clamp(scale, 0, SpeedRows.Length - 1)];
        return row[Math.Clamp((int)speed, 0, row.Length - 1)];
    }

    // liquidctl's _SPEED_VALUE: one row per animation speed scale, five entries per row
    // running slowest to fastest. The scale an animation uses is part of its definition.
    private static readonly Timing[][] SpeedRows =
    {
        new[] { new Timing(0x32, 0x00), new Timing(0x32, 0x00), new Timing(0x32, 0x00), new Timing(0x32, 0x00), new Timing(0x32, 0x00) },
        new[] { new Timing(0x50, 0x00), new Timing(0x3C, 0x00), new Timing(0x28, 0x00), new Timing(0x14, 0x00), new Timing(0x0A, 0x00) },
        new[] { new Timing(0x5E, 0x01), new Timing(0x2C, 0x01), new Timing(0xFA, 0x00), new Timing(0x96, 0x00), new Timing(0x50, 0x00) },
        new[] { new Timing(0x40, 0x06), new Timing(0x14, 0x05), new Timing(0xE8, 0x03), new Timing(0x20, 0x03), new Timing(0x58, 0x02) },
        new[] { new Timing(0x20, 0x03), new Timing(0xBC, 0x02), new Timing(0xF4, 0x01), new Timing(0x90, 0x01), new Timing(0x2C, 0x01) },
        new[] { new Timing(0x19, 0x00), new Timing(0x14, 0x00), new Timing(0x0F, 0x00), new Timing(0x07, 0x00), new Timing(0x04, 0x00) },
        new[] { new Timing(0x28, 0x00), new Timing(0x1E, 0x00), new Timing(0x14, 0x00), new Timing(0x0A, 0x00), new Timing(0x04, 0x00) },
        new[] { new Timing(0x32, 0x00), new Timing(0x28, 0x00), new Timing(0x1E, 0x00), new Timing(0x14, 0x00), new Timing(0x0A, 0x00) },
        new[] { new Timing(0x14, 0x00), new Timing(0x14, 0x00), new Timing(0x14, 0x00), new Timing(0x14, 0x00), new Timing(0x14, 0x00) },
    };
}

/// <summary>What the Kraken's LCD is currently showing.</summary>
public enum KrakenDisplayMode : byte
{
    /// <summary>Backlight on, nothing drawn - the panel reads as black.</summary>
    Blank = 0x01,
    /// <summary>The firmware's own liquid-temperature readout. Needs no host frames.</summary>
    Liquid = 0x02,
    /// <summary>Renders the contents of a stored bucket.</summary>
    Bucket = 0x04,
}

public enum KrakenAnimationSpeed
{
    Slowest = 0,
    Slower = 1,
    Normal = 2,
    Faster = 3,
    Fastest = 4,
}

/// <summary>
/// One firmware animation. <see cref="SpeedScale"/> selects which timing row the speed
/// index reads from; <see cref="ModeRelated"/> and <see cref="LedSize"/> are firmware
/// constants that vary per animation.
/// </summary>
/// <param name="BackwardBase">
/// Base value of the direction byte. Marquee animations carry 0x04 and starry-night 0x01
/// even when running forward; a backward run adds 2 on top.
/// </param>
public readonly record struct KrakenColorMode(
    byte ModeByte,
    int SpeedScale,
    byte ModeRelated,
    byte LedSize,
    byte BackwardBase = 0x00,
    byte? ColorCountOverride = null)
{
    public static readonly KrakenColorMode Fixed = new(0x00, 0, 0x00, 0x03);
    public static readonly KrakenColorMode Fading = new(0x01, 1, 0x08, 0x03);
    public static readonly KrakenColorMode SpectrumWave = new(0x02, 2, 0x00, 0x03);
    public static readonly KrakenColorMode Marquee = new(0x03, 2, 0x00, 0x03, 0x04);
    public static readonly KrakenColorMode CoveringMarquee = new(0x04, 2, 0x00, 0x03, 0x04);
    public static readonly KrakenColorMode Alternating = new(0x05, 3, 0x00, 0x03);
    public static readonly KrakenColorMode Pulse = new(0x06, 5, 0x08, 0x03);
    public static readonly KrakenColorMode Breathing = new(0x07, 6, 0x08, 0x03);
    public static readonly KrakenColorMode Candle = new(0x08, 0, 0x00, 0x03);
    public static readonly KrakenColorMode StarryNight = new(0x09, 5, 0x01, 0x03, 0x01);
    public static readonly KrakenColorMode RainbowFlow = new(0x0B, 2, 0x00, 0x03);
    public static readonly KrakenColorMode SuperRainbow = new(0x0C, 2, 0x00, 0x03);
    public static readonly KrakenColorMode RainbowPulse = new(0x0D, 2, 0x00, 0x03);
    public static readonly KrakenColorMode TaiChi = new(0x0E, 7, 0x05, 0x03);

    // Water cooler is handed two colours but the count byte must still read 1, or the
    // firmware plays it as a two-colour alternation instead of the cooling sweep.
    public static readonly KrakenColorMode WaterCooler = new(0x0F, 6, 0x05, 0x03, 0x00, 0x01);
    public static readonly KrakenColorMode Loading = new(0x10, 8, 0x04, 0x03);
}

/// <summary>
/// One firmware animation as the API names it. <see cref="MinColors"/> and
/// <see cref="MaxColors"/> bound the palette the animation reads; an animation that
/// generates its own colours takes none. <see cref="Directional"/> says whether the
/// animation travels, and so whether reversing it means anything.
/// </summary>
public sealed record KrakenEffect(
    string Id,
    KrakenColorMode Mode,
    int MinColors,
    int MaxColors,
    bool Directional);

/// <summary>
/// The firmware animations the cooler plays on its own. Ids match the web client's
/// effect list; the set mirrors NZXT CAM's own menu for this generation, minus the
/// audio- and temperature-reactive entries, which CAM drives from the host.
/// </summary>
public static class KrakenEffects
{
    public static readonly KrakenEffect[] All =
    {
        new("fixed", KrakenColorMode.Fixed, 1, 1, false),
        new("fading", KrakenColorMode.Fading, 1, 8, false),
        new("spectrumWave", KrakenColorMode.SpectrumWave, 0, 0, true),
        new("marquee", KrakenColorMode.Marquee, 1, 1, true),
        new("coveringMarquee", KrakenColorMode.CoveringMarquee, 1, 8, true),
        new("alternating", KrakenColorMode.Alternating, 1, 2, false),
        new("pulse", KrakenColorMode.Pulse, 1, 8, false),
        new("breathing", KrakenColorMode.Breathing, 1, 8, false),
        new("candle", KrakenColorMode.Candle, 1, 1, false),
        new("starryNight", KrakenColorMode.StarryNight, 1, 1, true),
        new("rainbowFlow", KrakenColorMode.RainbowFlow, 0, 0, true),
        new("superRainbow", KrakenColorMode.SuperRainbow, 0, 0, true),
        new("rainbowPulse", KrakenColorMode.RainbowPulse, 0, 0, true),
        new("taiChi", KrakenColorMode.TaiChi, 1, 2, false),
        new("waterCooler", KrakenColorMode.WaterCooler, 2, 2, false),
        new("loading", KrakenColorMode.Loading, 1, 1, false),
    };

    public static KrakenEffect? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        foreach (var effect in All)
        {
            if (string.Equals(effect.Id, id, StringComparison.Ordinal))
            {
                return effect;
            }
        }
        return null;
    }
}

public readonly record struct KrakenReading(
    double LiquidTempC,
    int PumpRpm,
    int PumpDuty,
    int FanRpm,
    int FanDuty);

public readonly record struct KrakenFirmware(int Major, int Minor, int Patch)
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public readonly record struct KrakenLcdInfo(int BrightnessPercent, int OrientationQuarterTurns, int Width, int Height);
