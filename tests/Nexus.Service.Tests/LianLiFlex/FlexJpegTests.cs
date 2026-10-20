using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexJpegTests
{
    private static readonly byte[] Bare = { 0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x04, 0x41, 0x42, 0xFF, 0xD9 };
    private static readonly byte[] WithHeader = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9 };

    [Fact]
    public void A_jpeg_without_a_jfif_header_gets_one_right_after_the_start_marker()
    {
        var result = FlexJpeg.WithJfif(Bare);
        Assert.Equal(Bare.Length + 18, result.Length);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00 }, result.Take(11).ToArray());
        Assert.Equal(Bare.Skip(2).ToArray(), result.Skip(20).ToArray());
    }

    [Fact]
    public void A_jpeg_that_has_the_header_or_is_not_a_jpeg_is_returned_as_is()
    {
        Assert.Same(WithHeader, FlexJpeg.WithJfif(WithHeader));
        var other = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Same(other, FlexJpeg.WithJfif(other));
        var tiny = new byte[] { 0xFF, 0xD8 };
        Assert.Same(tiny, FlexJpeg.WithJfif(tiny));
    }

    [Fact]
    public void The_added_header_is_idempotent()
    {
        var once = FlexJpeg.WithJfif(Bare);
        Assert.Same(once, FlexJpeg.WithJfif(once));
    }

    private static byte[] Encode(SkiaSharp.SKJpegEncoderDownsample downsample)
    {
        using var bitmap = Nexus.Service.Rendering.RenderKit.NewImage(64, 64, SkiaSharp.SKColors.Red);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SkiaSharp.SKJpegEncoderOptions(90, downsample, SkiaSharp.SKJpegEncoderAlphaOption.Ignore));
        return data!.ToArray();
    }

    private static byte Sampling(byte[] jpeg)
    {
        var i = 2;
        while (i + 4 < jpeg.Length)
        {
            var marker = jpeg[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                return jpeg[i + 11];
            }
            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }
        return 0;
    }

    [Fact]
    public void A_non_420_still_is_re_encoded_as_420_and_a_420_one_is_left_alone()
    {
        var full = Encode(SkiaSharp.SKJpegEncoderDownsample.Downsample444);
        Assert.NotEqual(0x22, Sampling(full));
        var prepared = FlexJpeg.Prepare(full);
        Assert.Equal(0x22, Sampling(prepared));
        Assert.Equal(0xE0, prepared[3]);

        var half = Encode(SkiaSharp.SKJpegEncoderDownsample.Downsample420);
        Assert.Equal(0x22, Sampling(half));
        Assert.Same(half, FlexJpeg.To420(half));
    }
}
