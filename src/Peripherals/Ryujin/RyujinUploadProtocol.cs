using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>
/// The stored-media upload to the Ryujin III Extreme. Command frames and the 4096-byte bulk chunking are
/// taken from an Armoury Crate capture; the acknowledgement frames are not in it (the recorder drops
/// interrupt-IN) and are taken from public Ryujin III projects, which report an EE 14 per chunk.
/// </summary>
internal static class RyujinUploadProtocol
{
    public const int ChunkBytes = 4096;
    public const byte BulkWritePipe = 0x02;
    public const byte BulkReadPipe = 0x82;

    private const byte Id = RyujinCoolingProtocol.ReportId;
    private const byte AckEvent = 0xEE;
    public const byte AckChunk = 0x14;
    public const byte AckErase = 0x13;

    /// <summary>Armoury Crate's spacing between the capture's upload commands and the first chunk, in ms.</summary>
    public static readonly int[] BeginGapsMs = { 27, 140, 110, 520, 510 };

    /// <summary>Armoury Crate's pause after each chunk and after every fourth one, in ms.</summary>
    public const int PacedChunkGapMs = 80;
    public const int PacedGroupGapMs = 2300;
    public const int PacedGroupChunks = 4;

    /// <summary>The last byte of Armoury Crate's 72; its select plays slot 0 whatever that byte is, so the two numberings may differ.</summary>
    public const int DefaultSlot = 1;

    public static byte[][] EncodeBegin(int length, int slot = DefaultSlot)
    {
        var begin = new[] { Report(0x71), Report(0xF1), Report(0x72), Report(0x73), Report(0x7F) };
        begin[0][2] = 0x01;
        begin[0][3] = 0x01;
        begin[2][2] = 0x01;
        begin[2][3] = 0x00;
        begin[2][4] = (byte)slot;
        begin[3][2] = 0x01;
        begin[4][2] = 0x02;
        BitConverter.GetBytes((uint)length).CopyTo(begin[4], 3);
        return begin;
    }

    public static byte[] EncodeEnd()
    {
        var end = Report(0x73);
        end[2] = 0xFF;
        return end;
    }

    /// <summary>Selects the stored-media layout; Armoury Crate sent it with the clock before the gif upload.</summary>
    public static byte[][] EncodeSelectStoredMedia(int slot = 0)
    {
        var layout = Report(0x5D);
        layout[3] = 0x01;
        layout[4] = 0x14;
        layout[5] = 0x01;
        layout[6] = (byte)slot;
        layout[9] = 0x01;
        var commit = Report(0x51);
        commit[2] = 0x14;
        commit[3] = 0x01;
        commit[4] = (byte)slot;
        return new[] { layout, commit };
    }

    /// <summary>The disk-info reply to F1: EC 71 00 01 then total KB, free KB (u32 LE) and the JPG and GIF slot bitmaps, per a public protocol doc.</summary>
    public static bool TryParseDiskInfo(ReadOnlySpan<byte> report, out uint totalKb, out uint freeKb, out uint jpgSlots, out uint gifSlots)
    {
        totalKb = freeKb = jpgSlots = gifSlots = 0;
        if (report.Length < 27 || report[0] != Id || report[1] != 0x71 || report[2] != 0x00 || report[3] != 0x01)
        {
            return false;
        }
        totalKb = BitConverter.ToUInt32(report[4..8]);
        freeKb = BitConverter.ToUInt32(report[8..12]);
        jpgSlots = BitConverter.ToUInt32(report[18..22]);
        gifSlots = BitConverter.ToUInt32(report[23..27]);
        return true;
    }

    /// <summary>The device's display-state report (EC 5C 00 ...): rotation byte 3 (0x5A for 90 degrees) and brightness byte 5, as in the settings report Nexus sends.</summary>
    public static bool TryParseDisplayState(ReadOnlySpan<byte> report, out int rotationDegrees, out int brightness)
    {
        rotationDegrees = brightness = 0;
        if (report.Length < 6 || report[0] != Id || report[1] != 0x5C || report[2] != 0x00)
        {
            return false;
        }
        rotationDegrees = report[3] == 0x5A ? 90 : 0;
        brightness = report[5];
        return true;
    }

    /// <summary>The query pair Armoury Crate opens an upload with, without the write.</summary>
    public static byte[][] EncodeDiskInfoQuery()
    {
        var begin = EncodeBegin(0);
        return new[] { begin[0], begin[1] };
    }

    public static int ChunkCount(int length) => (length + ChunkBytes - 1) / ChunkBytes;

    /// <summary>The file in 4096-byte chunks, the last padded with zeros as the capture shows.</summary>
    public static IEnumerable<byte[]> Chunks(byte[] file)
    {
        for (int at = 0; at < file.Length; at += ChunkBytes)
        {
            var chunk = new byte[ChunkBytes];
            Array.Copy(file, at, chunk, 0, Math.Min(ChunkBytes, file.Length - at));
            yield return chunk;
        }
    }

    /// <summary>The event kind of an EE report, with or without the leading report id; 0 for anything else.</summary>
    public static byte AckKind(ReadOnlySpan<byte> report)
    {
        if (report.Length >= 3 && report[0] == Id && report[1] == AckEvent)
        {
            return report[2];
        }
        return report.Length >= 2 && report[0] == AckEvent ? report[1] : (byte)0;
    }

    private static byte[] Report(byte header)
    {
        var report = new byte[RyujinCoolingProtocol.ReportLength];
        report[0] = Id;
        report[1] = header;
        return report;
    }
}
