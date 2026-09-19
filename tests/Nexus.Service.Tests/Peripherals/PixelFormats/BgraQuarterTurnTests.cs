using System;
using Nexus.Service.Peripherals.PixelFormats;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.PixelFormats;

public class BgraQuarterTurnTests
{
    // One distinct value per pixel, packed little-endian into the BGRA word, so a
    // transposed or mirrored result shows up as a specific wrong coordinate.
    private static byte[] Ramp(int width, int height)
    {
        var buf = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            buf[i * 4] = (byte)(i & 0xFF);
            buf[i * 4 + 1] = (byte)((i >> 8) & 0xFF);
            buf[i * 4 + 2] = (byte)((i >> 16) & 0xFF);
            buf[i * 4 + 3] = 0xFF;
        }
        return buf;
    }

    private static int PixelAt(ReadOnlySpan<byte> buf, int stride, int x, int y)
    {
        var o = (y * stride + x) * 4;
        return buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16);
    }

    [Theory]
    // Exercise sizes below, on, and across the 64-pixel tile boundary in both axes.
    [InlineData(4, 3)]
    [InlineData(64, 64)]
    [InlineData(65, 64)]
    [InlineData(70, 130)]
    public void RotateCcw_sends_each_pixel_to_its_turned_position(int width, int height)
    {
        var src = Ramp(width, height);
        var dest = new byte[width * height * 4];

        BgraQuarterTurn.RotateCcw(src, width, height, dest);

        // Counter-clockwise: source (x, y) lands at destination (y, width - 1 - x) in a
        // frame whose stride is now the source height.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.Equal(
                    PixelAt(src, width, x, y),
                    PixelAt(dest, height, y, width - 1 - x));
            }
        }
    }

    [Fact]
    public void RotateCcw_rejects_a_short_buffer()
    {
        Assert.Throws<ArgumentException>(() =>
            BgraQuarterTurn.RotateCcw(new byte[4 * 4 * 4 - 1], 4, 4, new byte[4 * 4 * 4]));
        Assert.Throws<ArgumentException>(() =>
            BgraQuarterTurn.RotateCcw(new byte[4 * 4 * 4], 4, 4, new byte[4 * 4 * 4 - 1]));
    }
}
