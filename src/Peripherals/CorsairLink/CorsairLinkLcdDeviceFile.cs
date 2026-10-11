using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nexus.Service.Monitoring.History.Binary;
using Nexus.Service.Rendering;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// The image or animation an iCUE LINK pump LCD keeps in its own flash and plays whenever
/// no host drives the glass. Layout and upload framing are read off iCUE 5.52 saving a GIF
/// and a still in Device Memory Mode (TITAN RX LCD, LCD fw 0.1.0.21):
///
///   file    = u16be width, u16be height, u8 1, u16le frame count, u16le frame ms,
///             u32le size per frame, then the baseline JPEG frames back to back
///   upload  = report 02 06 per 1008-byte chunk, grouped into 128 KiB blocks (1-based);
///             block 0 is a single 4-byte zero chunk that opens the transfer
///   commit  = feature 03 1c: CRC-32 of the JPEG bytes, block count, 01 00 01 01,
///             last chunk's length, last chunk's index in its block
/// </summary>
public static class CorsairLinkLcdDeviceFile
{
    internal const int Width = CorsairLinkLcd.PanelWidth;
    internal const int Height = CorsairLinkLcd.PanelHeight;
    internal const int HeaderBytes = 9;
    internal const int BlockBytes = 128 * 1024;
    internal const int ChunkPayload = 1008;
    internal const int ChunkHeaderBytes = 16;

    /// <summary>
    /// The largest file this firmware is known to accept (iCUE's own GIF upload). The flash
    /// size itself is unknown, so a longer animation is thinned to fit rather than sent
    /// and hoped for.
    /// </summary>
    internal const int MaxFileBytes = 2_905_253;

    /// <summary>A still holds its one frame; the firmware still reads a frame time.</summary>
    internal const int StillFrameMs = 1000;

    /// <summary>Frame time for an animation that carries no usable delays.</summary>
    internal const int DefaultFrameMs = 100;

    /// <summary>
    /// Builds the stored file from 480x480 baseline JPEG frames, keeping every n-th frame for
    /// the smallest n that fits (frame time scaled by n to keep the speed). Null when even one
    /// frame does not fit.
    /// </summary>
    public static byte[]? Build(IReadOnlyList<byte[]> frames, int frameMs)
    {
        if (frames.Count == 0) return null;
        List<byte[]> kept;
        var stride = 1;
        while (true)
        {
            kept = new List<byte[]>((frames.Count + stride - 1) / stride);
            for (var i = 0; i < frames.Count; i += stride) kept.Add(frames[i]);
            if (Size(kept) <= MaxFileBytes) break;
            if (kept.Count == 1) return null;
            stride++;
        }
        frameMs *= stride;

        var file = new byte[Size(kept)];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, Width);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], Height);
        span[4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(span[5..], (ushort)kept.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(span[7..], (ushort)Math.Clamp(frameMs, 1, ushort.MaxValue));
        var offset = HeaderBytes;
        foreach (var f in kept)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)f.Length);
            offset += 4;
        }
        foreach (var f in kept)
        {
            f.CopyTo(span[offset..]);
            offset += f.Length;
        }
        return file;
    }

    /// <summary>One frame time for the whole animation: the median of the per-frame delays.</summary>
    public static int FrameMs(IReadOnlyList<int> delaysMs, int frameCount)
    {
        if (frameCount <= 1) return StillFrameMs;
        var positive = new List<int>();
        foreach (var d in delaysMs) if (d > 0) positive.Add(d);
        if (positive.Count == 0) return DefaultFrameMs;
        positive.Sort();
        return positive[positive.Count / 2];
    }

    /// <summary>
    /// The frame as a baseline 4:2:0 JPEG, re-encoded when it is anything else. The firmware
    /// decodes only that layout (iCUE's frames): a 4:4:4 frame is stored and checksummed but
    /// never drawn, and the glass keeps its previous picture. Null when it does not decode.
    /// </summary>
    public static byte[]? ForDevice(byte[] jpeg)
    {
        if (IsBaseline420(jpeg)) return jpeg;
        using var image = RenderKit.Decode(jpeg);
        var encoded = image is null ? null : RenderKit.EncodeJpeg(image);
        return encoded is { Length: > 0 } ? encoded : null;
    }

    internal static bool IsBaseline420(ReadOnlySpan<byte> jpeg)
    {
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker == 0xC0)
            {
                // SOF0: precision, height, width, component count, then (id, HhVv, table) per component.
                var c = i + 9;
                return c < jpeg.Length && jpeg[c] == 3 && c + 9 < jpeg.Length
                    && jpeg[c + 2] == 0x22 && jpeg[c + 5] == 0x11 && jpeg[c + 8] == 0x11;
            }
            if (marker is >= 0xC1 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return false;
            if (marker == 0xDA) return false;
            i += 2 + length;
        }
        return false;
    }

    private static int Size(List<byte[]> frames)
    {
        var size = HeaderBytes + 4 * frames.Count;
        foreach (var f in frames) size += f.Length;
        return size;
    }

    /// <summary>
    /// The upload as 1024-byte output reports, in send order: block 0's opener, then every
    /// chunk of every 128 KiB block.
    /// </summary>
    public static IEnumerable<byte[]> UploadReports(byte[] file)
    {
        yield return Report(file.Length, block: 0, lastInBlock: true, lastBlock: false, chunk: 0, new byte[4]);

        var blocks = BlockCount(file.Length);
        for (var b = 0; b < blocks; b++)
        {
            var blockStart = b * BlockBytes;
            var blockLen = Math.Min(BlockBytes, file.Length - blockStart);
            var chunks = (blockLen + ChunkPayload - 1) / ChunkPayload;
            for (var c = 0; c < chunks; c++)
            {
                var start = blockStart + c * ChunkPayload;
                var len = Math.Min(ChunkPayload, blockStart + blockLen - start);
                yield return Report(file.Length, b + 1, c == chunks - 1, b == blocks - 1, c, file.AsSpan(start, len));
            }
        }
    }

    /// <summary>The 03 1c feature report that makes the uploaded file the stored screen.</summary>
    public static byte[] CommitReport(byte[] file)
    {
        var frameCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(5));
        var jpegStart = HeaderBytes + 4 * frameCount;
        var blocks = BlockCount(file.Length);
        var lastBlockLen = file.Length - (blocks - 1) * BlockBytes;
        var lastChunk = (lastBlockLen - 1) / ChunkPayload;
        var lastChunkLen = lastBlockLen - lastChunk * ChunkPayload;

        var r = new byte[16];
        r[0] = 0x03;
        r[1] = 0x1c;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(2), Crc32.Compute(file.AsSpan(jpegStart)));
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), (ushort)blocks);
        r[8] = 0x01;
        r[10] = 0x01;
        r[11] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(12), (ushort)lastChunkLen);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(14), (ushort)lastChunk);
        return r;
    }

    private static int BlockCount(int fileBytes) => (fileBytes + BlockBytes - 1) / BlockBytes;

    private static byte[] Report(int fileBytes, int block, bool lastInBlock, bool lastBlock, int chunk, ReadOnlySpan<byte> payload)
    {
        var r = new byte[CorsairLinkLcd.LcdBufferSize];
        r[0] = 0x02;
        r[1] = 0x06;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(2), (uint)fileBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), (ushort)block);
        r[8] = 0x01;
        r[10] = lastInBlock ? (byte)1 : (byte)0;
        r[11] = lastBlock ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(12), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(14), (ushort)chunk);
        payload.CopyTo(r.AsSpan(ChunkHeaderBytes));
        return r;
    }
}
