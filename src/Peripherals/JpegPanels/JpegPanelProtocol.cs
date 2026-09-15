using System;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// How one vendor frames a JPEG frame across a sequence of HID output reports. Every
/// style here chunks the same way - a whole JPEG split over consecutive reports - and
/// differs only in what precedes the bytes.
///
/// Reconstructed from third-party protocol documentation for each device. Only
/// <see cref="LianLiSequenced"/> has been run against hardware.
/// </summary>
public enum JpegPanelHeaderStyle
{
    /// <summary>
    /// Lian Li HydroShift LCD and Galahad II LCD. 11 bytes: report id, command, big-endian
    /// 32-bit total length, 24-bit sequence, 16-bit chunk length. The B report uses id 0x02
    /// and carries 1013 bytes in a 1024-byte report.
    /// </summary>
    LianLiSequenced,

    /// <summary>
    /// Corsair's LCD pumps. 8 bytes: report id 0x02, 0x05, a per-model selector, an
    /// is-last flag, an 8-bit chunk index, a pad, then the chunk length little-endian.
    /// 1016 payload bytes in a 1024-byte report.
    /// </summary>
    CorsairChunked,

    /// <summary>
    /// ID-Cooling FX-LCD. The first report carries a 33-byte ASCII-tagged header
    /// ("CRT" + "DRA") and 992 bytes; every report after it is a bare 1025-byte
    /// report id plus 1024 payload bytes, with no header at all.
    /// </summary>
    IdCoolingTagged,

    /// <summary>
    /// ASRock LCD. 25 bytes: a 0x5C frame header carrying the framed length, then a
    /// 21-byte image descriptor holding the total chunk count and this chunk's index.
    /// Unlike this panel's control channel, image frames are neither escaped nor
    /// checksummed. 1000 payload bytes per report, which is what fills its 1025-byte report.
    /// </summary>
    AsRockFramed,
}

/// <summary>
/// Builds the HID output reports that carry one JPEG frame. Pure and allocation-light:
/// the caller owns one report-sized buffer and this fills it in place, so a 30 fps
/// stream does not churn the heap.
/// </summary>
public static class JpegPanelProtocol
{
    /// <summary>ASCII "CRT", the tag every ID-Cooling report opens with.</summary>
    private static ReadOnlySpan<byte> IdCoolingTag => new byte[] { 0x43, 0x52, 0x54 };

    /// <summary>Header length for a chunk, given its style and position in the frame.</summary>
    public static int HeaderLength(JpegPanelHeaderStyle style, bool isFirstChunk) => style switch
    {
        JpegPanelHeaderStyle.LianLiSequenced => 11,
        JpegPanelHeaderStyle.CorsairChunked => 8,
        // Only ID-Cooling's opening report is framed; the rest is raw payload behind the
        // report id, which is why this is the one style whose header length varies.
        JpegPanelHeaderStyle.IdCoolingTagged => isFirstChunk ? 33 : 1,
        JpegPanelHeaderStyle.AsRockFramed => 25,
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };

    /// <summary>Payload bytes one report can carry at this position in the frame.</summary>
    public static int PayloadCapacity(JpegPanelHeaderStyle style, int reportLength, bool isFirstChunk) =>
        reportLength - HeaderLength(style, isFirstChunk);

    /// <summary>
    /// Writes one chunk's header plus its slice of <paramref name="jpeg"/> into
    /// <paramref name="report"/>, which must be exactly the device's output report length.
    /// The buffer is cleared first, so trailing padding is always zero. Returns the number
    /// of payload bytes consumed.
    /// </summary>
    public static int FillChunk(
        Span<byte> report,
        JpegPanelHeaderStyle style,
        byte selector,
        ReadOnlySpan<byte> jpeg,
        int offset,
        int chunkIndex)
    {
        if (offset < 0 || offset > jpeg.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        bool isFirst = chunkIndex == 0;
        int header = HeaderLength(style, isFirst);
        if (report.Length <= header)
        {
            throw new ArgumentException("report is too short to carry a header", nameof(report));
        }

        report.Clear();
        int capacity = report.Length - header;
        int take = Math.Min(capacity, jpeg.Length - offset);
        bool isLast = offset + take >= jpeg.Length;

        switch (style)
        {
            case JpegPanelHeaderStyle.LianLiSequenced:
                report[0] = 0x02;
                report[1] = selector;
                WriteUInt32BigEndian(report[2..], jpeg.Length);
                report[6] = (byte)((chunkIndex >> 16) & 0xFF);
                report[7] = (byte)((chunkIndex >> 8) & 0xFF);
                report[8] = (byte)(chunkIndex & 0xFF);
                report[9] = (byte)((take >> 8) & 0xFF);
                report[10] = (byte)(take & 0xFF);
                break;

            case JpegPanelHeaderStyle.CorsairChunked:
                report[0] = 0x02;
                report[1] = 0x05;
                report[2] = selector;
                report[3] = isLast ? (byte)0x01 : (byte)0x00;
                report[4] = (byte)(chunkIndex & 0xFF);
                report[5] = 0x00;
                report[6] = (byte)(take & 0xFF);
                report[7] = (byte)((take >> 8) & 0xFF);
                break;

            case JpegPanelHeaderStyle.AsRockFramed:
            {
                // Every chunk repeats the total count, so it is derived rather than passed:
                // a caller that got it wrong would desynchronise the panel's reassembly.
                int chunks = ChunkCount(style, report.Length, jpeg.Length);
                int framed = 21 + take;
                report[0] = 0x00;
                report[1] = 0x5C;
                report[2] = (byte)((framed >> 8) & 0xFF);
                report[3] = (byte)(framed & 0xFF);
                // Image descriptor: id, pad, chunk count, pad, chunk index, format (1 = JPEG),
                // then 15 reserved zeros the buffer clear already supplies.
                report[4] = 0x00;
                report[6] = (byte)chunks;
                report[8] = (byte)chunkIndex;
                report[9] = 0x01;
                break;
            }

            case JpegPanelHeaderStyle.IdCoolingTagged:
                report[0] = 0x00;
                if (isFirst)
                {
                    IdCoolingTag.CopyTo(report[1..]);
                    report[6] = 0x44; // 'D'
                    report[7] = 0x52; // 'R'
                    report[8] = 0x41; // 'A'
                    // The length field counts the 32 framing bytes that follow the report
                    // id, not just the JPEG: the documented length is the payload plus 32.
                    int framed = jpeg.Length + 32;
                    report[11] = (byte)((framed >> 8) & 0xFF);
                    report[12] = (byte)(framed & 0xFF);
                    report[13] = 0xB1;
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(style));
        }

        jpeg.Slice(offset, take).CopyTo(report[header..]);
        return take;
    }

    /// <summary>Reports one frame needs at this report length. Always at least one.</summary>
    public static int ChunkCount(JpegPanelHeaderStyle style, int reportLength, int jpegLength)
    {
        if (jpegLength <= 0)
        {
            return 0;
        }
        int first = PayloadCapacity(style, reportLength, isFirstChunk: true);
        if (jpegLength <= first)
        {
            return 1;
        }
        int rest = PayloadCapacity(style, reportLength, isFirstChunk: false);
        return 1 + (((jpegLength - first) + rest - 1) / rest);
    }

    private static void WriteUInt32BigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 24) & 0xFF);
        destination[1] = (byte)((value >> 16) & 0xFF);
        destination[2] = (byte)((value >> 8) & 0xFF);
        destination[3] = (byte)(value & 0xFF);
    }
}
