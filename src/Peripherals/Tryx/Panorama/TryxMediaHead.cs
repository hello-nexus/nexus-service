using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Turns the head of a file pulled off the panel into something ffmpeg can thumbnail.
/// Stored media is either the Tryx container (uploads: uint32 LE header length + MediaHeaderPb,
/// then Annex-B H.264) or bare Annex-B (presets, cloud themes). The bundled ffmpeg decodes H.264
/// and demuxes Matroska but has no raw-H.264 demuxer, so the first keyframe is re-wrapped as a
/// one-frame Matroska file.</summary>
public static class TryxMediaHead
{
    public sealed record Keyframe(byte[] Sps, byte[] Pps, List<byte[]> Slices);

    public sealed record Head(Keyframe Keyframe, int Width, int Height, double DurationSec);

    private const int DefaultWidth = 2240;
    private const int DefaultHeight = 1080;

    /// <summary>The first keyframe plus container metadata, or null until <paramref name="file"/>
    /// holds a complete IDR picture with its SPS/PPS.</summary>
    public static Head? Parse(ReadOnlySpan<byte> file)
    {
        var es = file;
        int width = DefaultWidth, height = DefaultHeight;
        double duration = 0;
        if (!IsStartCode(file))
        {
            if (file.Length < 4) return null;
            var headerLen = BinaryPrimitives.ReadUInt32LittleEndian(file);
            if (headerLen > (uint)(file.Length - 4)) return null;
            ReadMediaHeader(file.Slice(4, (int)headerLen), ref width, ref height, out duration);
            es = file[(4 + (int)headerLen)..];
        }
        var kf = FindFirstKeyframe(es);
        return kf is null ? null : new Head(kf, width, height, duration);
    }

    // MediaHeaderPb { frame_rate = 5, width = 6, height = 7, total_frame_count = 8 }, all varints.
    private static void ReadMediaHeader(ReadOnlySpan<byte> hdr, ref int width, ref int height, out double duration)
    {
        ulong fps = 0, frames = 0;
        var pos = 0;
        while (pos < hdr.Length)
        {
            if (!TryReadVarint(hdr, ref pos, out var tag)) break;
            var wt = (int)(tag & 7);
            if (wt == 0)
            {
                if (!TryReadVarint(hdr, ref pos, out var v)) break;
                switch ((int)(tag >> 3))
                {
                    case 5: fps = v; break;
                    case 6 when v is > 0 and < 16384: width = (int)v; break;
                    case 7 when v is > 0 and < 16384: height = (int)v; break;
                    case 8: frames = v; break;
                }
            }
            else if (wt == 2)
            {
                if (!TryReadVarint(hdr, ref pos, out var len) || len > (ulong)(hdr.Length - pos)) break;
                pos += (int)len;
            }
            else
            {
                break;
            }
        }
        duration = fps > 0 && frames > 0 ? (double)frames / fps : 0;
    }

    /// <summary>SPS, PPS and the IDR slices of the first keyframe; null until a NAL outside the
    /// IDR picture has begun (so a truncated head never yields a partial frame).</summary>
    public static Keyframe? FindFirstKeyframe(ReadOnlySpan<byte> es)
    {
        byte[]? sps = null, pps = null;
        var slices = new List<byte[]>();
        var start = NextNalStart(es, 0);
        while (start >= 0 && start < es.Length)
        {
            var type = es[start] & 0x1f;
            // A slice with first_mb_in_slice == 0 (leading ue(v) bit set) begins a new picture.
            var newPicture = start + 1 < es.Length && (es[start + 1] & 0x80) != 0;
            if (slices.Count > 0 && (type != 5 || newPicture))
            {
                return sps is null || pps is null ? null : new Keyframe(sps, pps, slices);
            }
            var next = NextNalStart(es, start);
            if (next < 0) return null;
            var nal = es[start..StartCodeBegin(es, next, start)].ToArray();
            if (type == 7) sps ??= nal;
            else if (type == 8) pps ??= nal;
            else if (type == 5) slices.Add(nal);
            start = next;
        }
        return null;
    }

    private static bool IsStartCode(ReadOnlySpan<byte> b)
        => b.StartsWith(new byte[] { 0, 0, 1 }) || b.StartsWith(new byte[] { 0, 0, 0, 1 });

    // Index just past the next 00 00 01 at or after from, or -1.
    private static int NextNalStart(ReadOnlySpan<byte> es, int from)
    {
        var i = es[from..].IndexOf(new byte[] { 0, 0, 1 });
        return i < 0 ? -1 : from + i + 3;
    }

    // Start of the start code ending at nalStart, taking in the 4-byte form's leading zero and
    // any trailing_zero_8bits, but never before floor (the previous NAL's first byte).
    private static int StartCodeBegin(ReadOnlySpan<byte> es, int nalStart, int floor)
    {
        var begin = nalStart - 3;
        while (begin > floor && es[begin - 1] == 0) begin--;
        return begin;
    }

    /// <summary>A single-frame Matroska file carrying <paramref name="kf"/> as an AVC track.</summary>
    public static byte[] BuildMatroska(Keyframe kf, int width, int height)
    {
        var avcC = new MemoryStream();
        avcC.Write([1, kf.Sps[1], kf.Sps[2], kf.Sps[3], 0xFF, 0xE1]);
        WriteU16(avcC, kf.Sps.Length);
        avcC.Write(kf.Sps);
        avcC.WriteByte(1);
        WriteU16(avcC, kf.Pps.Length);
        avcC.Write(kf.Pps);

        var frame = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        foreach (var slice in kf.Slices)
        {
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)slice.Length);
            frame.Write(len);
            frame.Write(slice);
        }

        var ebmlHeader = Element(0x1A45DFA3,
            Element(0x4282, Encoding.ASCII.GetBytes("matroska")), Element(0x4287, [2]), Element(0x4285, [2]));
        var info = Element(0x1549A966, Element(0x2AD7B1, UInt(1_000_000)));
        var track = Element(0xAE,
            Element(0xD7, [1]), Element(0x73C5, [1]), Element(0x83, [1]),
            Element(0x86, Encoding.ASCII.GetBytes("V_MPEG4/ISO/AVC")),
            Element(0x63A2, avcC.ToArray()),
            Element(0xE0, Element(0xB0, UInt((ulong)width)), Element(0xBA, UInt((ulong)height))));
        // SimpleBlock: track 1 (vint 0x81), relative timecode 0, keyframe flag.
        var block = new MemoryStream();
        block.Write([0x81, 0x00, 0x00, 0x80]);
        frame.WriteTo(block);
        var cluster = Element(0x1F43B675, Element(0xE7, [0]), Element(0xA3, block.ToArray()));
        var segment = Element(0x18538067, info, Element(0x1654AE6B, track), cluster);

        var file = new MemoryStream();
        file.Write(ebmlHeader);
        file.Write(segment);
        return file.ToArray();
    }

    // EBML element: big-endian id (its marker bits are part of the id), then an 8-byte size vint.
    private static byte[] Element(uint id, params byte[][] children)
    {
        var ms = new MemoryStream();
        var idLen = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        for (var i = idLen - 1; i >= 0; i--) ms.WriteByte((byte)(id >> (8 * i)));
        long size = 0;
        foreach (var c in children) size += c.Length;
        ms.WriteByte(0x01);
        for (var i = 6; i >= 0; i--) ms.WriteByte((byte)(size >> (8 * i)));
        foreach (var c in children) ms.Write(c);
        return ms.ToArray();
    }

    private static byte[] UInt(ulong v)
    {
        var len = 1;
        while (len < 8 && v >> (8 * len) != 0) len++;
        var b = new byte[len];
        for (var i = 0; i < len; i++) b[len - 1 - i] = (byte)(v >> (8 * i));
        return b;
    }

    private static void WriteU16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> b, ref int pos, out ulong val)
    {
        val = 0;
        var shift = 0;
        while (pos < b.Length && shift < 64)
        {
            var x = b[pos++];
            val |= (ulong)(x & 0x7f) << shift;
            if ((x & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
