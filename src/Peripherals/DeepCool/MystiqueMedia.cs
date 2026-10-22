using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>A picture or GIF ready to stream: one header-plus-JPEG frame per entry.</summary>
public sealed record MystiqueMediaPackage(byte Kind, string Id, IReadOnlyList<byte[]> Frames);

/// <summary>
/// The screen's media upload. After a 0x0F command the host streams each frame (a 64-byte header then
/// its baseline JPEG) in 64-byte writes on the settings pipe, a frame's last write being the remainder
/// with no zero-length write after a whole packet, then the 55-byte finish packet. Header: "DCLd", kind,
/// JPEG size (LE32), JPEG byte sum low 16 bits (LE32), frame count (LE16), duration in ms (LE16), frame
/// index (LE16), a zero, the 32-character id, zeros, then the sum of bytes 0 to 61 (LE16). A picture
/// leaves count, duration and index zero. The screen's own software re-sends an edited item under its id.
/// </summary>
public static class MystiqueMedia
{
    public const int Width = 480;
    public const int Height = 640;
    public const int HeaderLength = 64;
    public const int PacketLength = 64;
    public const int IdLength = 32;

    public const byte KindPicture = 1;
    public const byte KindGif = 2;

    // A still GIF sends the count and duration of the first recorded GIFs. A moving one sends a frame per source
    // frame, never fewer than a still's or more than the largest recorded, and the span they cover as its duration.
    public const int GifFrames = 25;
    public const int GifDurationMs = 1087;
    public const int MaxGifFrames = 149;

    // The largest recorded uploads; nothing larger is sent.
    public const int MaxPictureJpegBytes = 145_654;
    public const int MaxGifStreamBytes = 10_982_436;

    // The screen's own software offers this many slots of each kind.
    public const int MaxPictures = 9;
    public const int MaxGifs = 9;

    private static readonly byte[] FinishText = Encoding.ASCII.GetBytes("dcldfinish");
    public const int FinishLength = 55;

    /// <summary>The id an upload goes under: per source and kind, so a picture and a GIF of one file never share it.</summary>
    public static string IdOf(byte kind, ReadOnlySpan<byte> source)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(new[] { kind });
        md5.AppendData(source);
        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool IsId(string? id)
    {
        if (id is null || id.Length != IdLength)
        {
            return false;
        }
        foreach (char c in id)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }
        return true;
    }

    public static byte[] BuildHeader(byte kind, ReadOnlySpan<byte> jpeg, string id, int frameCount, int durationMs, int frameIndex)
    {
        if (!IsId(id))
        {
            throw new ArgumentException("id must be 32 lowercase hex characters", nameof(id));
        }
        var h = new byte[HeaderLength];
        "DCLd"u8.CopyTo(h);
        h[4] = kind;
        WriteU32(h, 5, jpeg.Length);
        int sum = 0;
        foreach (byte b in jpeg)
        {
            sum += b;
        }
        WriteU32(h, 9, sum & 0xFFFF);
        WriteU16(h, 13, frameCount);
        WriteU16(h, 15, durationMs);
        WriteU16(h, 17, frameIndex);
        Encoding.ASCII.GetBytes(id, h.AsSpan(20, IdLength));
        int headerSum = 0;
        for (int i = 0; i < HeaderLength - 2; i++)
        {
            headerSum += h[i];
        }
        WriteU16(h, 62, headerSum);
        return h;
    }

    public static byte[] BuildFrame(byte kind, ReadOnlySpan<byte> jpeg, string id, int frameCount, int durationMs, int frameIndex)
    {
        var frame = new byte[HeaderLength + jpeg.Length];
        BuildHeader(kind, jpeg, id, frameCount, durationMs, frameIndex).CopyTo(frame, 0);
        jpeg.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static MystiqueMediaPackage BuildPicture(ReadOnlySpan<byte> jpeg, string id)
    {
        if (jpeg.Length == 0 || jpeg.Length > MaxPictureJpegBytes)
        {
            throw new ArgumentException("picture outside the recorded size", nameof(jpeg));
        }
        return new(KindPicture, id, new[] { BuildFrame(KindPicture, jpeg, id, 0, 0, 0) });
    }

    public static MystiqueMediaPackage BuildGif(IReadOnlyList<byte[]> jpegs, string id, int durationMs = GifDurationMs)
    {
        if (jpegs.Count is < GifFrames or > MaxGifFrames)
        {
            throw new ArgumentException("GIF frame count outside the recorded range", nameof(jpegs));
        }
        if (durationMs is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }
        var frames = new List<byte[]>(jpegs.Count);
        long total = 0;
        for (int i = 0; i < jpegs.Count; i++)
        {
            frames.Add(BuildFrame(KindGif, jpegs[i], id, jpegs.Count, durationMs, i));
            total += frames[^1].Length;
        }
        if (total > MaxGifStreamBytes)
        {
            throw new ArgumentException("GIF outside the recorded size", nameof(jpegs));
        }
        return new MystiqueMediaPackage(KindGif, id, frames);
    }

    /// <summary>A GIF's frames as kept on disk: its duration (LE16), then each JPEG as its length (LE32) and bytes.</summary>
    public static byte[] SaveGif(MystiqueMediaPackage gif)
    {
        int length = 2;
        foreach (var frame in gif.Frames)
        {
            length += 4 + frame.Length - HeaderLength;
        }
        var saved = new byte[length];
        saved[0] = gif.Frames[0][15];
        saved[1] = gif.Frames[0][16];
        int at = 2;
        foreach (var frame in gif.Frames)
        {
            BinaryPrimitives.WriteInt32LittleEndian(saved.AsSpan(at), frame.Length - HeaderLength);
            frame.AsSpan(HeaderLength).CopyTo(saved.AsSpan(at + 4));
            at += 4 + frame.Length - HeaderLength;
        }
        return saved;
    }

    /// <summary>A GIF kept by <see cref="SaveGif"/>, rebuilt under <paramref name="id"/>; null when the bytes do not hold one the screen takes.</summary>
    public static MystiqueMediaPackage? LoadGif(ReadOnlySpan<byte> saved, string id)
    {
        if (saved.Length < 2 || !IsId(id))
        {
            return null;
        }
        int durationMs = BinaryPrimitives.ReadUInt16LittleEndian(saved);
        var jpegs = new List<byte[]>();
        int at = 2;
        while (at < saved.Length)
        {
            if (saved.Length - at < 4)
            {
                return null;
            }
            int length = BinaryPrimitives.ReadInt32LittleEndian(saved[at..]);
            if (length <= 0 || length > saved.Length - at - 4 || jpegs.Count == MaxGifFrames)
            {
                return null;
            }
            jpegs.Add(saved.Slice(at + 4, length).ToArray());
            at += 4 + length;
        }
        if (jpegs.Count < GifFrames || durationMs < 1 || StreamLength(jpegs) > MaxGifStreamBytes)
        {
            return null;
        }
        return BuildGif(jpegs, id, durationMs);
    }

    public static long StreamLength(IReadOnlyList<byte[]> jpegs)
    {
        long total = 0;
        foreach (var j in jpegs)
        {
            total += HeaderLength + j.Length;
        }
        return total;
    }

    public static IEnumerable<byte[]> Packets(byte[] frame)
    {
        for (int offset = 0; offset < frame.Length; offset += PacketLength)
        {
            yield return frame.AsSpan(offset, Math.Min(PacketLength, frame.Length - offset)).ToArray();
        }
    }

    public static byte[] FinishPacket()
    {
        var p = new byte[FinishLength];
        FinishText.CopyTo(p, 0);
        return p;
    }

    private static void WriteU16(byte[] b, int offset, int v)
    {
        b[offset] = (byte)v;
        b[offset + 1] = (byte)(v >> 8);
    }

    private static void WriteU32(byte[] b, int offset, int v)
    {
        WriteU16(b, offset, v);
        WriteU16(b, offset + 2, v >> 16);
    }
}
