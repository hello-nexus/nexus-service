using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Platform;
using SkiaSharp;
using Xunit;

namespace Nexus.Service.Tests.DeepCool;

public class MystiqueMediaTests
{
    // Headers and layout below are byte-for-byte the uploads of a recorded session.
    private const string PictureId = "2101b6e6277841daa821e8d79a5c528b";
    private const string GifId = "acd9325521f3493c86bfc0b0aa85658d";

    private static readonly int[] GifSizes =
    {
        27198, 27200, 26910, 26967, 26832, 27024, 27041, 27435, 26919, 27759, 28814, 26029, 25918,
        29243, 27179, 27328, 28194, 28084, 28105, 28173, 28036, 28184, 28407, 28510, 28414,
    };

    private static readonly int[] GifSums =
    {
        12701, 43386, 62356, 57868, 49794, 60650, 22167, 63297, 53282, 64816, 2414, 41857, 292,
        24905, 40447, 54700, 53136, 43777, 26591, 61152, 36144, 41992, 936, 28049, 10348,
    };

    private static readonly int[] GifFrameStartPackets =
    {
        0, 426, 852, 1274, 1697, 2118, 2542, 2966, 3396, 3818, 4253, 4705, 5113, 5519, 5977, 6403,
        6831, 7273, 7713, 8154, 8596, 9036, 9478, 9923, 10370,
    };

    private static readonly int[] GifShortPackets = { 62, 30, 23, 16, 16, 33, 43, 39, 47, 14, 45, 62, 59, 43, 34, 52, 9, 13, 4, 24, 55, 30, 62 };

    // The four DHT segments of the recorded uploads: the standard tables of the JPEG specification, Annex K.3.
    private const string StandardHuffmanTables =
        "FFC4001F0000010501010101010100000000000000000102030405060708090A0B"
        + "FFC400B5100002010303020403050504040000017D01020300041105122131410613516107227114328191A1082342B1C11552D1F02433627282090A161718191A25262728292A3435363738393A434445464748494A535455565758595A636465666768696A737475767778797A838485868788898A92939495969798999AA2A3A4A5A6A7A8A9AAB2B3B4B5B6B7B8B9BAC2C3C4C5C6C7C8C9CAD2D3D4D5D6D7D8D9DAE1E2E3E4E5E6E7E8E9EAF1F2F3F4F5F6F7F8F9FA"
        + "FFC4001F0100030101010101010101010000000000000102030405060708090A0B"
        + "FFC400B51100020102040403040705040400010277000102031104052131061241510761711322328108144291A1B1C109233352F0156272D10A162434E125F11718191A262728292A35363738393A434445464748494A535455565758595A636465666768696A737475767778797A82838485868788898A92939495969798999AA2A3A4A5A6A7A8A9AAB2B3B4B5B6B7B8B9BAC2C3C4C5C6C7C8C9CAD2D3D4D5D6D7D8D9DAE2E3E4E5E6E7E8E9EAF2F3F4F5F6F7F8F9FA";

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    /// <summary>A stand-in JPEG of the recorded length and byte sum, the only inputs the header takes from it.</summary>
    private static byte[] Jpeg(int length, int sum)
    {
        var b = new byte[length];
        int i = 0;
        for (; sum >= 255; sum -= 255)
        {
            b[i++] = 255;
        }
        b[i] = (byte)sum;
        return b;
    }

    [Theory]
    [InlineData(145654, 57930, "44434C6401F63802004AE2000000000000000000323130316236653632373738343164616138323165386437396135633532386200000000000000000000F40B")]
    [InlineData(144909, 31971, "44434C64010D360200E37C0000000000000000003231303162366536323737383431646161383231653864373961356335323862000000000000000000003C0B")]
    public void Picture_header_matches_the_recorded_uploads(int length, int sum, string expected)
    {
        var package = MystiqueMedia.BuildPicture(Jpeg(length, sum), PictureId);

        Assert.Equal(expected, Hex(package.Frames.Single()[..MystiqueMedia.HeaderLength]));
    }

    [Theory]
    [InlineData(0, "44434C64023E6A00009D31000019003F04000000616364393332353532316633343933633836626663306230616138353635386400000000000000000000CB0B")]
    [InlineData(1, "44434C6402406A00007AA9000019003F04010000616364393332353532316633343933633836626663306230616138353635386400000000000000000000230C")]
    [InlineData(2, "44434C64021E69000094F3000019003F04020000616364393332353532316633343933633836626663306230616138353635386400000000000000000000650C")]
    public void Gif_headers_match_the_recorded_upload(int index, string expected)
    {
        var package = MystiqueMedia.BuildGif(GifSizes.Select((s, i) => Jpeg(s, GifSums[i])).ToList(), GifId);

        Assert.Equal(expected, Hex(package.Frames[index][..MystiqueMedia.HeaderLength]));
    }

    [Fact]
    public void Gif_packets_follow_the_recorded_layout_with_no_padding_after_a_whole_packet()
    {
        var package = MystiqueMedia.BuildGif(GifSizes.Select((s, i) => Jpeg(s, GifSums[i])).ToList(), GifId);
        var starts = new List<int>();
        var packets = new List<byte[]>();
        foreach (var frame in package.Frames)
        {
            starts.Add(packets.Count);
            packets.AddRange(MystiqueMedia.Packets(frame));
        }

        Assert.Equal(10815, packets.Count);
        Assert.Equal(GifFrameStartPackets, starts);
        Assert.Equal(GifShortPackets, packets.Where(p => p.Length != MystiqueMedia.PacketLength).Select(p => p.Length));
        Assert.Equal(package.Frames.SelectMany(f => f), packets.SelectMany(p => p));
    }

    [Theory]
    [InlineData(145654, 2277, 54)]
    [InlineData(144909, 2266, 13)]
    public void Picture_packets_follow_the_recorded_layout(int length, int packetCount, int lastLength)
    {
        var packets = MystiqueMedia.Packets(MystiqueMedia.BuildPicture(Jpeg(length, 0), PictureId).Frames.Single()).ToList();

        Assert.Equal(packetCount, packets.Count);
        Assert.Equal(lastLength, packets[^1].Length);
        Assert.All(packets.SkipLast(1), p => Assert.Equal(MystiqueMedia.PacketLength, p.Length));
    }

    [Fact]
    public void Finish_packet_matches_the_recorded_one()
    {
        Assert.Equal("64636C6466696E697368" + new string('0', 90), Hex(MystiqueMedia.FinishPacket()));
    }

    [Fact]
    public void Sizes_and_counts_outside_the_recording_are_refused()
    {
        Assert.Throws<ArgumentException>(() => MystiqueMedia.BuildPicture(new byte[MystiqueMedia.MaxPictureJpegBytes + 1], PictureId));
        Assert.Throws<ArgumentException>(() => MystiqueMedia.BuildGif(Enumerable.Repeat(new byte[100], 24).ToList(), GifId));
        Assert.Throws<ArgumentException>(() => MystiqueMedia.BuildGif(Enumerable.Repeat(new byte[28_000], 25).ToList(), GifId));
        Assert.Throws<ArgumentException>(() => MystiqueMedia.BuildPicture(new byte[100], "2101B6E6277841DAA821E8D79A5C528B"));
    }

    [Fact]
    public void Ids_are_per_source_and_kind()
    {
        var a = new byte[] { 1, 2, 3 };
        Assert.True(MystiqueMedia.IsId(MystiqueMedia.IdOf(MystiqueMedia.KindPicture, a)));
        Assert.Equal(MystiqueMedia.IdOf(MystiqueMedia.KindPicture, a), MystiqueMedia.IdOf(MystiqueMedia.KindPicture, new byte[] { 1, 2, 3 }));
        Assert.NotEqual(MystiqueMedia.IdOf(MystiqueMedia.KindPicture, a), MystiqueMedia.IdOf(MystiqueMedia.KindGif, a));
    }

    [Fact]
    public void Encoded_jpeg_has_the_segment_layout_of_the_recorded_uploads()
    {
        var rgbx = new byte[MystiqueMedia.Width * MystiqueMedia.Height * 4];
        new Random(7).NextBytes(rgbx);

        AssertRecordedLayout(MystiqueMediaEncoder.EncodeJpeg(rgbx, 95));
    }

    [FfmpegFact]
    public async Task A_picture_encodes_to_one_screen_sized_frame_within_the_recorded_size()
    {
        var result = await MystiqueMediaEncoder.EncodePictureAsync(Png(1200, 900), ".png", null, PictureId, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        var frame = result.Package!.Frames.Single();
        Assert.Equal(MystiqueMedia.KindPicture, frame[4]);
        Assert.InRange(frame.Length - MystiqueMedia.HeaderLength, 1, MystiqueMedia.MaxPictureJpegBytes);
        AssertRecordedLayout(frame[MystiqueMedia.HeaderLength..]);
    }

    [FfmpegFact]
    public async Task A_clip_encodes_to_the_recorded_frame_count_and_a_still_repeats()
    {
        // Built from PNG frames rather than a lavfi source, which the bundled ffmpeg lacks.
        var dir = Path.Combine(Path.GetTempPath(), $"mystique-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 30; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, $"f{i:000}.png"), Png(320, 240, i * 4));
            }
            var clip = Path.Combine(dir, "clip.gif");
            Run(FfmpegResolver.Path!, "-v", "error", "-y", "-framerate", "30", "-i", Path.Combine(dir, "f%03d.png"), "-f", "gif", clip);
            var moving = await MystiqueMediaEncoder.EncodeGifAsync(await File.ReadAllBytesAsync(clip), ".gif", null, GifId, CancellationToken.None);
            var still = await MystiqueMediaEncoder.EncodeGifAsync(Png(300, 300), ".png", null, GifId, CancellationToken.None);

            Assert.True(moving.Ok, moving.Error);
            Assert.Equal(MystiqueMedia.GifFrames, moving.Package!.Frames.Count);
            Assert.True(moving.Package.Frames.Select(f => Hex(f[MystiqueMedia.HeaderLength..])).Distinct().Count() > 1);
            Assert.True(still.Ok, still.Error);
            Assert.Single(still.Package!.Frames.Select(f => Hex(f[MystiqueMedia.HeaderLength..])).Distinct());
            Assert.All(still.Package.Frames, f => Assert.Equal(MystiqueMedia.GifDurationMs, f[15] | (f[16] << 8)));
            AssertRecordedLayout(moving.Package.Frames[0][MystiqueMedia.HeaderLength..]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23)]
    [InlineData(25)]
    public void Fewer_frames_than_slots_repeat_evenly_in_order(int frameCount)
    {
        var picks = Enumerable.Range(0, MystiqueMedia.GifFrames).Select(i => MystiqueMediaEncoder.SlotSource(i, frameCount)).ToList();

        Assert.Equal(Enumerable.Range(0, frameCount), picks.Distinct());
        Assert.Equal(picks.OrderBy(k => k), picks);
        Assert.All(picks.GroupBy(k => k), g => Assert.InRange(g.Count(), MystiqueMedia.GifFrames / frameCount, (MystiqueMedia.GifFrames + frameCount - 1) / frameCount));
    }

    [FfmpegFact]
    public async Task A_longer_clip_is_spread_across_its_whole_length_and_carries_the_recorded_duration()
    {
        // Still for its first second, moving for its second: frames from the first second alone would barely differ.
        var dir = Path.Combine(Path.GetTempPath(), $"mystique-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 60; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, $"f{i:000}.png"), Png(320, 240, Math.Max(0, i - 30) * 4));
            }
            var clip = Path.Combine(dir, "clip.gif");
            Run(FfmpegResolver.Path!, "-v", "error", "-y", "-framerate", "30", "-i", Path.Combine(dir, "f%03d.png"), "-f", "gif", clip);

            var result = await MystiqueMediaEncoder.EncodeGifAsync(await File.ReadAllBytesAsync(clip), ".gif", null, GifId, CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(MystiqueMedia.GifFrames, result.Package!.Frames.Count);
            Assert.All(result.Package.Frames, f => Assert.Equal(MystiqueMedia.GifDurationMs, f[15] | (f[16] << 8)));
            Assert.InRange(result.Package.Frames.Select(f => Hex(f[MystiqueMedia.HeaderLength..])).Distinct().Count(), 10, MystiqueMedia.GifFrames);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task An_oversized_source_is_kept_as_a_smaller_copy_the_encoder_still_takes()
    {
        var picture = await MystiqueMediaEncoder.ReduceForKeepingAsync(Png(4000, 3000), ".png", clip: false, maxBytes: long.MaxValue);

        Assert.NotNull(picture);
        Assert.Equal(".jpg", picture.Value.Ext);
        using (var kept = SKBitmap.Decode(picture.Value.Bytes))
        {
            Assert.Equal((MystiqueMediaEncoder.KeepLongSide, MystiqueMediaEncoder.KeepLongSide * 3 / 4), (kept.Width, kept.Height));
        }
        var encoded = await MystiqueMediaEncoder.EncodePictureAsync(picture.Value.Bytes, picture.Value.Ext, null, PictureId, CancellationToken.None);
        Assert.True(encoded.Ok, encoded.Error);

        var dir = Path.Combine(Path.GetTempPath(), $"mystique-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 30; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, $"f{i:000}.png"), Png(320, 240, i * 4));
            }
            var gif = Path.Combine(dir, "clip.gif");
            Run(FfmpegResolver.Path!, "-v", "error", "-y", "-framerate", "30", "-i", Path.Combine(dir, "f%03d.png"), "-f", "gif", gif);

            var clip = await MystiqueMediaEncoder.ReduceForKeepingAsync(await File.ReadAllBytesAsync(gif), ".gif", clip: true, maxBytes: long.MaxValue);

            Assert.NotNull(clip);
            Assert.Equal(".mp4", clip.Value.Ext);
            Assert.Null(await MystiqueMediaEncoder.ReduceForKeepingAsync(await File.ReadAllBytesAsync(gif), ".gif", clip: true, maxBytes: 1));
            var moving = await MystiqueMediaEncoder.EncodeGifAsync(clip.Value.Bytes, clip.Value.Ext, null, GifId, CancellationToken.None);
            Assert.True(moving.Ok, moving.Error);
            Assert.True(moving.Package!.Frames.Select(f => Hex(f[MystiqueMedia.HeaderLength..])).Distinct().Count() > 1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>SOI, JFIF 1.01 APP0, two DQT, SOF0 480x640 with 2x2 luma and 1x1 chroma, four DHT, SOS: as recorded.</summary>
    private static void AssertRecordedLayout(byte[] jpeg)
    {
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        var markers = new List<byte>();
        var huffman = new List<byte>();
        int i = 2;
        while (true)
        {
            Assert.Equal(0xFF, jpeg[i]);
            byte marker = jpeg[i + 1];
            int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            markers.Add(marker);
            if (marker == 0xE0)
            {
                Assert.Equal("4A4649460001010000010001", Hex(jpeg[(i + 4)..(i + 16)]));
            }
            if (marker == 0xC0)
            {
                Assert.Equal("08028001E003012200021101031101", Hex(jpeg[(i + 4)..(i + 19)]));
            }
            if (marker == 0xC4)
            {
                huffman.AddRange(jpeg[i..(i + 2 + length)]);
            }
            if (marker == 0xDA)
            {
                break;
            }
            i += 2 + length;
        }
        Assert.Equal(new byte[] { 0xE0, 0xDB, 0xDB, 0xC0, 0xC4, 0xC4, 0xC4, 0xC4, 0xDA }, markers);
        Assert.Equal(StandardHuffmanTables, Hex(huffman.ToArray()));
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);
    }

    private static byte[] Png(int width, int height, int shift = 0)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bool inner = Math.Abs(x - width / 2 - shift) < width / 4 && Math.Abs(y - height / 2) < height / 4;
                pixels[o] = inner ? (byte)255 : (byte)(x * 255 / width);
                pixels[o + 1] = inner ? (byte)255 : (byte)(y * 255 / height);
                pixels[o + 2] = inner ? (byte)255 : (byte)64;
                pixels[o + 3] = 255;
            }
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi)!;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        Assert.Equal(0, proc.ExitCode);
    }
}
