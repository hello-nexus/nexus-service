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

    public static byte[][] EncodeBegin(int length)
    {
        var begin = new[] { Report(0x71), Report(0xF1), Report(0x72), Report(0x73), Report(0x7F) };
        begin[0][2] = 0x01;
        begin[0][3] = 0x01;
        begin[2][2] = 0x01;
        begin[2][3] = 0x00;
        begin[2][4] = 0x01;
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
    public static byte[][] EncodeSelectStoredMedia()
    {
        var layout = Report(0x5D);
        layout[3] = 0x01;
        layout[4] = 0x14;
        layout[5] = 0x01;
        layout[9] = 0x01;
        var commit = Report(0x51);
        commit[2] = 0x14;
        commit[3] = 0x01;
        return new[] { layout, commit };
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
