using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Extracts the H.264 elementary stream from an MP4 as Annex-B (start-code framed, SPS/PPS
/// in-band). The bundled ffmpeg is stripped to the mp4 muxer only (no raw h264 muxer, no
/// h264_mp4toannexb bitstream filter), and the Tryx panel's media container wraps a raw
/// Annex-B stream, so the conversion is done here. A stream already carrying SPS/PPS in-band
/// (x264 repeat-headers) is copied as is; otherwise the avcC SPS/PPS are emitted before every IDR.
/// </summary>
public static class Mp4AnnexB
{
    private static readonly byte[] StartCode = { 0x00, 0x00, 0x00, 0x01 };

    public static byte[] Convert(ReadOnlySpan<byte> mp4)
    {
        var (spsList, ppsList, nalLenSize) = ParseAvcC(mp4);
        if (spsList.Count == 0 || ppsList.Count == 0)
        {
            throw new InvalidOperationException("avcC (SPS/PPS) not found in MP4");
        }
        var mdat = FindBoxPayload(mp4, "mdat");
        if (mdat.IsEmpty)
        {
            throw new InvalidOperationException("mdat not found in MP4");
        }

        var outp = new List<byte>(mdat.Length + 256);
        if (HasInBandSps(mdat, nalLenSize))
        {
            CopyInBand(outp, mdat, nalLenSize);
            return outp.ToArray();
        }
        EmitParameterSets(outp, spsList, ppsList);

        var i = 0;
        while (i + nalLenSize <= mdat.Length)
        {
            var nalLen = ReadBigEndian(mdat.Slice(i, nalLenSize));
            i += nalLenSize;
            if (nalLen == 0 || i + (int)nalLen > mdat.Length) break;
            var nal = mdat.Slice(i, (int)nalLen);
            i += (int)nalLen;

            var nalType = nal[0] & 0x1f;
            if (nalType == 5)
            {
                EmitParameterSets(outp, spsList, ppsList);
            }
            // Skip SPS/PPS already carried in-band from avcC; re-emit ours instead.
            if (nalType is 7 or 8) continue;
            outp.AddRange(StartCode);
            for (var k = 0; k < nal.Length; k++) outp.Add(nal[k]);
        }
        return outp.ToArray();
    }

    private static bool HasInBandSps(ReadOnlySpan<byte> mdat, int nalLenSize)
    {
        var i = 0;
        while (i + nalLenSize <= mdat.Length)
        {
            var nalLen = (int)ReadBigEndian(mdat.Slice(i, nalLenSize));
            i += nalLenSize;
            if (nalLen == 0 || i + nalLen > mdat.Length) return false;
            var nalType = mdat[i] & 0x1f;
            if (nalType == 7) return true;
            // x264 with repeat-headers puts SPS in the first access unit, ahead of its first slice.
            if (nalType is 1 or 5) return false;
            i += nalLen;
        }
        return false;
    }

    // Copies the stream verbatim (x264 repeat-headers + aud), minus the extradata SEI ffmpeg
    // prepends to the first sample, so the stream opens the way x264 writes it raw.
    private static void CopyInBand(List<byte> outp, ReadOnlySpan<byte> mdat, int nalLenSize)
    {
        var leading = true;
        var i = 0;
        while (i + nalLenSize <= mdat.Length)
        {
            var nalLen = (int)ReadBigEndian(mdat.Slice(i, nalLenSize));
            i += nalLenSize;
            if (nalLen == 0 || i + nalLen > mdat.Length) break;
            var nal = mdat.Slice(i, nalLen);
            i += nalLen;
            var nalType = nal[0] & 0x1f;
            if (leading && nalType == 6) continue;
            leading = false;
            outp.AddRange(StartCode);
            for (var k = 0; k < nal.Length; k++) outp.Add(nal[k]);
        }
    }

    private static void EmitParameterSets(List<byte> outp, List<byte[]> sps, List<byte[]> pps)
    {
        foreach (var s in sps) { outp.AddRange(StartCode); outp.AddRange(s); }
        foreach (var p in pps) { outp.AddRange(StartCode); outp.AddRange(p); }
    }

    private static (List<byte[]> Sps, List<byte[]> Pps, int NalLenSize) ParseAvcC(ReadOnlySpan<byte> mp4)
    {
        var payload = FindBoxPayload(mp4, "avcC");
        var sps = new List<byte[]>();
        var pps = new List<byte[]>();
        // Need at least the 5 fixed bytes + the numSPS byte before indexing them.
        if (payload.Length < 6) return (sps, pps, 4);

        // AVCDecoderConfigurationRecord: [0]=version [1]=profile [2]=compat [3]=level
        // [4]=xxxxxx + lengthSizeMinusOne(2) [5]=xxx + numSPS(5), then per SPS [len:2][data].
        var nalLenSize = (payload[4] & 0x03) + 1;
        var idx = 5;
        var numSps = payload[idx++] & 0x1f;
        for (var n = 0; n < numSps && idx + 2 <= payload.Length; n++)
        {
            var len = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(idx, 2)); idx += 2;
            if (idx + len > payload.Length) break;
            sps.Add(payload.Slice(idx, len).ToArray()); idx += len;
        }
        if (idx >= payload.Length) return (sps, pps, nalLenSize);
        var numPps = payload[idx++];
        for (var n = 0; n < numPps && idx + 2 <= payload.Length; n++)
        {
            var len = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(idx, 2)); idx += 2;
            if (idx + len > payload.Length) break;
            pps.Add(payload.Slice(idx, len).ToArray()); idx += len;
        }
        return (sps, pps, nalLenSize);
    }

    // Locates a box by its 4-char type and returns its payload span. Scans the raw bytes
    // for the type tag rather than walking the box tree, since the sought boxes (mdat at
    // top level, avcC deep in stsd) are uniquely typed in a single-video-track MP4.
    private static ReadOnlySpan<byte> FindBoxPayload(ReadOnlySpan<byte> mp4, string type)
    {
        Span<byte> tag = stackalloc byte[4];
        for (var i = 0; i < 4; i++) tag[i] = (byte)type[i];
        for (var i = 4; i + 4 <= mp4.Length; i++)
        {
            if (!mp4.Slice(i, 4).SequenceEqual(tag)) continue;
            var sizeField = BinaryPrimitives.ReadUInt32BigEndian(mp4.Slice(i - 4, 4));
            var payloadStart = i + 4;
            long payloadLen;
            if (sizeField == 1)
            {
                if (payloadStart + 8 > mp4.Length) return default;
                var large = BinaryPrimitives.ReadUInt64BigEndian(mp4.Slice(payloadStart, 8));
                payloadStart += 8;
                payloadLen = (long)large - 16;
            }
            else if (sizeField == 0)
            {
                payloadLen = mp4.Length - payloadStart;
            }
            else
            {
                payloadLen = (long)sizeField - 8;
            }
            if (payloadLen <= 0 || payloadStart + payloadLen > mp4.Length) return default;
            return mp4.Slice(payloadStart, (int)payloadLen);
        }
        return default;
    }

    private static uint ReadBigEndian(ReadOnlySpan<byte> b)
    {
        uint v = 0;
        foreach (var x in b) v = (v << 8) | x;
        return v;
    }
}
