using System;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GlReadbackConvertTests
{
    // The per-byte loop the vectorized pass replaced.
    private static byte[] Reference(byte[] src, int w, int h, bool rgba)
    {
        var stride = w * (rgba ? 4 : 3);
        var dst = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            var srcRow = (h - 1 - y) * stride;
            var dstRow = y * w * 3;
            for (var x = 0; x < w; x++)
            {
                var step = rgba ? 4 : 3;
                dst[dstRow + (x * 3)] = src[srcRow + (x * step)];
                dst[dstRow + (x * 3) + 1] = src[srcRow + (x * step) + 1];
                dst[dstRow + (x * 3) + 2] = src[srcRow + (x * step) + 2];
            }
        }
        return dst;
    }

    private static byte[] Noise(int length)
    {
        var rng = new Random(7);
        var b = new byte[length];
        rng.NextBytes(b);
        return b;
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(4, 4, true)]
    [InlineData(5, 3, true)]
    [InlineData(7, 9, true)]
    [InlineData(160, 90, true)]
    [InlineData(161, 91, true)]
    [InlineData(1, 1, false)]
    [InlineData(5, 3, false)]
    [InlineData(160, 90, false)]
    public void FlipRows_matches_the_per_byte_reference(int w, int h, bool rgba)
    {
        var src = Noise(w * h * (rgba ? 4 : 3));
        var dst = new byte[w * h * 3];
        GlReadbackConvert.FlipRows(src, dst, w, h, rgba);
        Assert.Equal(Reference(src, w, h, rgba), dst);
    }

    [Fact]
    public void FlipRows_puts_the_last_source_row_first_and_drops_alpha()
    {
        // Two rows of one pixel: bottom row (first in memory) is red, top row green.
        byte[] src = { 255, 0, 0, 9, 0, 255, 0, 9 };
        var dst = new byte[6];
        GlReadbackConvert.FlipRows(src, dst, 1, 2, rgba: true);
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 0 }, dst);
    }

    [Fact]
    public void WriteFromGl_fills_the_canvas_like_the_flip_then_WriteFromRgb_path()
    {
        const int w = 13, h = 6;
        var src = Noise(w * h * 4);
        var canvas = new CanvasBuffer(w, h);
        canvas.WriteFromGl(src, rgba: true);
        var viaOld = new CanvasBuffer(w, h);
        viaOld.WriteFromRgb(Reference(src, w, h, true));
        Assert.True(canvas.Pixels.SequenceEqual(viaOld.Pixels));
    }

    [Fact]
    public void WriteFromGl_ignores_a_block_too_short_for_the_canvas()
    {
        var canvas = new CanvasBuffer(4, 4);
        canvas.Fill(5, 5, 5);
        canvas.WriteFromGl(new byte[10], rgba: true);
        Assert.Equal((5, 5, 5), canvas.GetPixel(0, 0));
    }
}
