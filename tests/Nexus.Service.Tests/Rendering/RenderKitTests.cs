using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Nexus.Service.Rendering;
using SkiaSharp;
using Xunit;

namespace Nexus.Service.Tests.Rendering;

public class RenderKitTests
{
    [Theory]
    [InlineData("#ff8000", 0xff, 0x80, 0x00, 0xff)]
    [InlineData("ff8000", 0xff, 0x80, 0x00, 0xff)]
    [InlineData("#f80", 0xff, 0x88, 0x00, 0xff)]
    // nexus-web writes alpha last (CSS order), not Skia's #aarrggbb.
    [InlineData("#ff800040", 0xff, 0x80, 0x00, 0x40)]
    [InlineData("#f804", 0xff, 0x88, 0x00, 0x44)]
    public void ParseColor_reads_css_hex_order(string hex, int r, int g, int b, int a)
    {
        var color = RenderKit.ParseColor(hex, SKColors.Black);

        Assert.Equal(new SKColor((byte)r, (byte)g, (byte)b, (byte)a), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    public void ParseColor_falls_back_on_anything_else(string? hex)
    {
        Assert.Equal(SKColors.Teal, RenderKit.ParseColor(hex, SKColors.Teal));
    }

    [Fact]
    public void Decode_returns_null_for_bytes_that_are_no_image()
    {
        Assert.Null(RenderKit.Decode(Encoding.ASCII.GetBytes("not an image at all")));
    }

    [Fact]
    public void Decode_refuses_a_header_past_the_pixel_ceiling_without_allocating_it()
    {
        // A valid PNG header for 20000x20000 RGBA: 1.6 GB decoded from a few dozen bytes.
        Assert.Null(RenderKit.Decode(PngHeaderOnly(20000, 20000)));
    }

    [Fact]
    public void Jpeg_round_trip_keeps_the_channel_order()
    {
        using var image = RenderKit.NewImage(32, 32, new SKColor(0xe0, 0x20, 0x40));

        using var decoded = TestImages.Decode(RenderKit.EncodeJpeg(image));

        var px = decoded.GetPixel(16, 16);
        Assert.InRange(px.Red, 0xe0 - 12, 0xe0 + 12);
        Assert.InRange(px.Green, 0x20 - 12, 0x20 + 12);
        Assert.InRange(px.Blue, 0x40 - 12, 0x40 + 12);
    }

    [Fact]
    public void Rgba_bytes_round_trip_unpremultiplied()
    {
        var rgba = new byte[] { 200, 100, 50, 128, 0, 0, 0, 0 };

        using var image = RenderKit.FromRgba32Bytes(rgba, 2, 1);
        var back = RenderKit.ToRgba32Bytes(image);

        // Premultiplied storage rounds a half-alpha channel by at most one step.
        for (var i = 0; i < rgba.Length; i++)
        {
            Assert.InRange(back[i], rgba[i] - 1, rgba[i] + 1);
        }
    }

    [Fact]
    public void Crop_outside_the_source_is_transparent_not_uninitialized()
    {
        using var src = RenderKit.NewImage(4, 4, SKColors.Red);

        using var crop = RenderKit.Crop(src, SKRectI.Create(10, 10, 3, 3));

        Assert.Equal(0, crop.GetPixel(1, 1).Alpha);
    }

    private static byte[] PngHeaderOnly(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        stream.Write(word);
        var typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        stream.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(typed));
        stream.Write(word);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }
        return ~crc;
    }
}
