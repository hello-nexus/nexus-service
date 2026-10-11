using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.History.Binary;
using Nexus.Service.Peripherals.CorsairLink;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkLcdDeviceFileTests
{
    private static byte[] Jpeg(int length, byte fill)
    {
        var b = new byte[length];
        Array.Fill(b, fill);
        b[0] = 0xFF;
        b[1] = 0xD8;
        return b;
    }

    [Fact]
    public void Build_lays_out_header_size_table_and_frames()
    {
        var frames = new[] { Jpeg(100, 1), Jpeg(250, 2) };

        var file = CorsairLinkLcdDeviceFile.Build(frames, 32)!;

        Assert.Equal(480, BinaryPrimitives.ReadUInt16BigEndian(file));
        Assert.Equal(480, BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(2)));
        Assert.Equal(1, file[4]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(5)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(7)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(9)));
        Assert.Equal(250u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(13)));
        Assert.Equal(frames[0].Concat(frames[1]).ToArray(), file.AsSpan(17).ToArray());
    }

    // The two commits iCUE 5.52 sent: a 46-frame GIF (2,905,253 bytes) and a still (75,367 bytes).
    [Theory]
    [InlineData(46, 2_905_253, 23, 501, 21)]
    [InlineData(1, 75_367, 1, 775, 74)]
    public void CommitReport_matches_icue(int frameCount, int fileBytes, int blocks, int lastLen, int lastIdx)
    {
        var jpegTotal = fileBytes - 9 - 4 * frameCount;
        var frames = new List<byte[]>();
        for (var i = 0; i < frameCount; i++)
        {
            var len = jpegTotal / frameCount + (i < jpegTotal % frameCount ? 1 : 0);
            frames.Add(Jpeg(len, (byte)i));
        }
        var file = CorsairLinkLcdDeviceFile.Build(frames, 32)!;
        Assert.Equal(fileBytes, file.Length);

        var commit = CorsairLinkLcdDeviceFile.CommitReport(file);

        Assert.Equal(new byte[] { 0x03, 0x1c }, commit[..2]);
        Assert.Equal(Crc32.Compute(file.AsSpan(9 + 4 * frameCount)), BinaryPrimitives.ReadUInt32LittleEndian(commit.AsSpan(2)));
        Assert.Equal(blocks, BinaryPrimitives.ReadUInt16LittleEndian(commit.AsSpan(6)));
        Assert.Equal(new byte[] { 0x01, 0x00, 0x01, 0x01 }, commit[8..12]);
        Assert.Equal(lastLen, BinaryPrimitives.ReadUInt16LittleEndian(commit.AsSpan(12)));
        Assert.Equal(lastIdx, BinaryPrimitives.ReadUInt16LittleEndian(commit.AsSpan(14)));
    }

    [Fact]
    public void UploadReports_open_with_block_zero_and_carry_the_file_in_order()
    {
        var file = CorsairLinkLcdDeviceFile.Build(new[] { Jpeg(300_000, 7) }, 1000)!;

        var reports = CorsairLinkLcdDeviceFile.UploadReports(file).ToList();

        // iCUE's opener: size, block 0, 01 00, last-in-block, 4 zero bytes.
        var opener = reports[0];
        Assert.Equal(1024, opener.Length);
        Assert.Equal(new byte[] { 0x02, 0x06 }, opener[..2]);
        Assert.Equal((uint)file.Length, BinaryPrimitives.ReadUInt32LittleEndian(opener.AsSpan(2)));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, opener[6..20]);

        var data = new List<byte>();
        var blocks = new HashSet<int>();
        foreach (var r in reports.Skip(1))
        {
            var block = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(6));
            var len = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(12));
            blocks.Add(block);
            Assert.Equal(block == 3 ? 1 : 0, r[11]);
            data.AddRange(r.AsSpan(16, len).ToArray());
        }
        Assert.Equal(new[] { 1, 2, 3 }, blocks.OrderBy(b => b));
        Assert.Equal(file, data.ToArray());

        // Each block ends on a flagged chunk and restarts its chunk index.
        var block1 = reports.Skip(1).Where(r => r[6] == 1).ToList();
        Assert.Equal(131, block1.Count);
        Assert.Equal(1, block1[^1][10]);
        Assert.All(block1.SkipLast(1), r => Assert.Equal(0, r[10]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(reports.Skip(1).First(r => r[6] == 2).AsSpan(14)));
    }

    [Fact]
    public void Build_thins_an_oversized_animation_and_keeps_its_speed()
    {
        var frames = Enumerable.Range(0, 40).Select(i => Jpeg(100_000, (byte)i)).ToList();

        var file = CorsairLinkLcdDeviceFile.Build(frames, 40)!;

        Assert.True(file.Length <= CorsairLinkLcdDeviceFile.MaxFileBytes);
        // 40 frames of 100 KB need every 2nd frame to fit; a third of them would not be needed.
        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(5)));
        Assert.Equal(80, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(7)));
    }

    [Fact]
    public void Build_takes_the_smallest_stride_that_fits()
    {
        // Every second frame is still too large; every third fits.
        var frames = Enumerable.Range(0, 90).Select(i => Jpeg(90_000, (byte)i)).ToList();

        var file = CorsairLinkLcdDeviceFile.Build(frames, 50)!;

        Assert.Equal(30, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(5)));
        Assert.Equal(150, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(7)));
    }

    [Fact]
    public void Build_refuses_a_single_frame_that_cannot_fit()
    {
        Assert.Null(CorsairLinkLcdDeviceFile.Build(new[] { Jpeg(CorsairLinkLcdDeviceFile.MaxFileBytes, 1) }, 1000));
    }

    private static byte[] SkiaJpeg(SkiaSharp.SKJpegEncoderDownsample downsample)
    {
        using var bitmap = new SkiaSharp.SKBitmap(480, 480);
        bitmap.Erase(new SkiaSharp.SKColor(16, 24, 48));
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SkiaSharp.SKJpegEncoderOptions(85, downsample, SkiaSharp.SKJpegEncoderAlphaOption.Ignore))!;
        return data.ToArray();
    }

    [Fact]
    public void ForDevice_keeps_420_and_reencodes_444()
    {
        var yuv420 = SkiaJpeg(SkiaSharp.SKJpegEncoderDownsample.Downsample420);
        var yuv444 = SkiaJpeg(SkiaSharp.SKJpegEncoderDownsample.Downsample444);

        Assert.True(CorsairLinkLcdDeviceFile.IsBaseline420(yuv420));
        Assert.False(CorsairLinkLcdDeviceFile.IsBaseline420(yuv444));
        Assert.Same(yuv420, CorsairLinkLcdDeviceFile.ForDevice(yuv420));
        Assert.True(CorsairLinkLcdDeviceFile.IsBaseline420(CorsairLinkLcdDeviceFile.ForDevice(yuv444)!));
        Assert.Null(CorsairLinkLcdDeviceFile.ForDevice(new byte[] { 0xFF, 0xD8, 0x00 }));
    }

    [Fact]
    public void FrameMs_takes_the_median_delay_and_a_still_holds()
    {
        Assert.Equal(CorsairLinkLcdDeviceFile.StillFrameMs, CorsairLinkLcdDeviceFile.FrameMs(Array.Empty<int>(), 1));
        Assert.Equal(40, CorsairLinkLcdDeviceFile.FrameMs(new[] { 30, 40, 40, 500 }, 4));
        Assert.Equal(100, CorsairLinkLcdDeviceFile.FrameMs(new[] { 0, 0 }, 2));
    }
}

public class CorsairLinkLcdImportFilterTests
{
    [Fact]
    public void An_uncropped_source_is_centre_cropped_to_a_square_not_stretched()
    {
        Assert.Equal("crop='min(iw,ih)':'min(iw,ih)',scale=480:480", CorsairLinkLcdMediaLibrary.ImportFilter(null, video: false));
    }

    [Fact]
    public void A_cropper_rect_is_applied_and_video_is_sampled()
    {
        var crop = new Nexus.Service.Media.CropRect(0.25, 0, 0.5, 1);

        Assert.Equal(
            $"crop=iw*0.5:ih*1:iw*0.25:ih*0,fps={CorsairLinkLcdMediaLibrary.VideoFps},scale=480:480",
            CorsairLinkLcdMediaLibrary.ImportFilter(crop, video: true));
    }
}
