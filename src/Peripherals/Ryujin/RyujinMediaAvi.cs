using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>
/// The MJPEG AVI the Ryujin III Extreme stores and plays, laid out byte for byte like the one Armoury Crate
/// uploaded in the capture (an ffmpeg AVI muxer file): a header list with padded placeholder chunks, an INFO
/// list, padding up to the movi list, one 00dc chunk per frame, then the idx1 index. Only the frame count,
/// the largest frame and the sizes and offsets that follow from the frames vary.
/// </summary>
internal static class RyujinMediaAvi
{
    public const int Width = 640;
    public const int Height = 480;
    public const int Fps = 15;

    /// <summary>Offset of the first frame chunk in every file; the capture's header is exactly this long.</summary>
    public const int MoviDataOffset = 5678;

    private const int MoviListOffset = 5666;
    private const int HdrlListSize = 4588;
    private const int StrlListSize = 4244;
    private const int IndxJunkSize = 4120;
    private const int OdmlJunkSize = 260;
    private const int PadJunkSize = 1016;
    private const uint MaxBytesPerSec = 0x34BC00;
    private const uint AviFlags = 0x910;
    private const uint SuggestedBuffer = 0x100000;
    private const string Software = "Lavf58.76.100";

    /// <summary>Armoury Crate's frames all open with this comment segment after the SOI.</summary>
    private static readonly byte[] JpegComment = BuildComment();

    public static byte[] Build(IReadOnlyList<byte[]> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("An AVI needs at least one frame.", nameof(frames));
        }
        long moviSize = 4;
        uint maxFrame = 0;
        foreach (var f in frames)
        {
            moviSize += 8 + f.Length + (f.Length & 1);
            maxFrame = Math.Max(maxFrame, (uint)f.Length);
        }
        int idxSize = 16 * frames.Count;
        // Everything before the movi list header, the movi list, then the idx1 chunk.
        long total = MoviListOffset + 8 + moviSize + 8 + idxSize;
        using var ms = new MemoryStream((int)total);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)(total - 8));
        w.Write(Encoding.ASCII.GetBytes("AVI "));

        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write((uint)HdrlListSize);
        w.Write(Encoding.ASCII.GetBytes("hdrl"));
        w.Write(Encoding.ASCII.GetBytes("avih"));
        w.Write(56u);
        w.Write(1_000_000u / Fps);
        w.Write(MaxBytesPerSec);
        w.Write(0u);
        w.Write(AviFlags);
        w.Write((uint)frames.Count);
        w.Write(0u);
        w.Write(1u);
        w.Write(SuggestedBuffer);
        w.Write((uint)Width);
        w.Write((uint)Height);
        w.Write(new byte[16]);

        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write((uint)StrlListSize);
        w.Write(Encoding.ASCII.GetBytes("strl"));
        w.Write(Encoding.ASCII.GetBytes("strh"));
        w.Write(56u);
        w.Write(Encoding.ASCII.GetBytes("vids"));
        w.Write(Encoding.ASCII.GetBytes("MJPG"));
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);
        w.Write(1u);
        w.Write((uint)Fps);
        w.Write(0u);
        w.Write((uint)frames.Count);
        w.Write(maxFrame);
        w.Write(0xFFFFFFFFu);
        w.Write(0u);
        w.Write(0u);
        w.Write((ushort)Width);
        w.Write((ushort)Height);
        w.Write(Encoding.ASCII.GetBytes("strf"));
        w.Write(40u);
        w.Write(40u);
        w.Write(Width);
        w.Write(Height);
        w.Write((ushort)1);
        w.Write((ushort)24);
        w.Write(Encoding.ASCII.GetBytes("MJPG"));
        w.Write((uint)(Width * Height * 3));
        w.Write(new byte[16]);
        // The OpenDML super index placeholder: entry size 4, no entries, chunk id 00dc.
        w.Write(Encoding.ASCII.GetBytes("JUNK"));
        w.Write((uint)IndxJunkSize);
        var indx = new byte[IndxJunkSize];
        indx[0] = 4;
        Encoding.ASCII.GetBytes("00dc").CopyTo(indx, 8);
        w.Write(indx);
        w.Write(Encoding.ASCII.GetBytes("JUNK"));
        w.Write((uint)OdmlJunkSize);
        var odml = new byte[OdmlJunkSize];
        Encoding.ASCII.GetBytes("odmldmlh").CopyTo(odml, 0);
        odml[8] = 0xF8;
        w.Write(odml);

        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write(26u);
        w.Write(Encoding.ASCII.GetBytes("INFO"));
        w.Write(Encoding.ASCII.GetBytes("ISFT"));
        w.Write((uint)(Software.Length + 1));
        w.Write(Encoding.ASCII.GetBytes(Software));
        w.Write((byte)0);
        w.Write(Encoding.ASCII.GetBytes("JUNK"));
        w.Write((uint)PadJunkSize);
        w.Write(new byte[PadJunkSize]);

        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write((uint)moviSize);
        w.Write(Encoding.ASCII.GetBytes("movi"));
        var index = new List<(uint Offset, uint Size)>(frames.Count);
        uint at = 4;
        foreach (var f in frames)
        {
            w.Write(Encoding.ASCII.GetBytes("00dc"));
            w.Write((uint)f.Length);
            w.Write(f);
            if ((f.Length & 1) != 0)
            {
                w.Write((byte)0);
            }
            index.Add((at, (uint)f.Length));
            at += (uint)(8 + f.Length + (f.Length & 1));
        }
        w.Write(Encoding.ASCII.GetBytes("idx1"));
        w.Write((uint)idxSize);
        foreach (var (offset, size) in index)
        {
            w.Write(Encoding.ASCII.GetBytes("00dc"));
            w.Write(0x10u);
            w.Write(offset);
            w.Write(size);
        }
        return ms.ToArray();
    }

    /// <summary>Inserts the comment segment Armoury Crate's frames carry straight after the SOI of a baseline JPEG.</summary>
    public static byte[] WithComment(byte[] jpeg)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return jpeg;
        }
        var result = new byte[jpeg.Length + JpegComment.Length];
        result[0] = 0xFF;
        result[1] = 0xD8;
        JpegComment.CopyTo(result, 2);
        Array.Copy(jpeg, 2, result, 2 + JpegComment.Length, jpeg.Length - 2);
        return result;
    }

    private static byte[] BuildComment()
    {
        var text = Encoding.ASCII.GetBytes("Lavc58.134.100\0");
        var segment = new byte[4 + text.Length];
        segment[0] = 0xFF;
        segment[1] = 0xFE;
        segment[2] = (byte)((text.Length + 2) >> 8);
        segment[3] = (byte)((text.Length + 2) & 0xFF);
        text.CopyTo(segment, 4);
        return segment;
    }
}
