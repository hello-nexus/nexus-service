using System;
using System.Buffers;
using System.Linq;
using System.Runtime.InteropServices;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class RawFrameUpscalerTests
{
    private static StreamedPanelProfile Profile(int width, int height, double renderScale, StreamCodec codec = StreamCodec.RawBgra) => new()
    {
        Kind = "test",
        DisplayName = "Test",
        Surface = "lcd-wide",
        CssWidth = width,
        CssHeight = height,
        Codec = codec,
        RenderScale = renderScale,
    };

    private sealed class CountingPool : ArrayPool<byte>
    {
        public int Returned;
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) => Returned++;
    }

    private static StreamFrame Frame(uint[] pixels, CountingPool pool, byte flags = 0) => new()
    {
        Flags = flags,
        Payload = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray(),
        Pooled = true,
        ReturnTo = pool,
    };

    private static uint[] Pixels(StreamFrame frame) => MemoryMarshal.Cast<byte, uint>(frame.Bytes).ToArray();

    [Theory]
    [InlineData(StreamCodec.RawBgra, 1.0)]
    [InlineData(StreamCodec.H264, 0.5)]
    public void Only_raw_frames_rendered_below_native_get_an_upscaler(StreamCodec codec, double renderScale)
    {
        Assert.Null(RawFrameUpscaler.For("s", Profile(2288, 1080, renderScale, codec)));
    }

    [Fact]
    public void Half_scale_frames_come_back_at_native_size()
    {
        var upscaler = RawFrameUpscaler.For("s", Profile(2288, 1080, 0.5))!;

        Assert.Equal(1144 * 540 * 4, upscaler.SourceBytes);
        Assert.Equal(2288 * 1080 * 4, upscaler.TargetBytes);
    }

    [Theory]
    [InlineData(2288, 1080, 1144, 540)]
    [InlineData(1600, 720, 800, 360)]
    [InlineData(1920, 480, 1280, 320)]
    [InlineData(1120, 540, 746, 360)]
    [InlineData(1920, 462, 1440, 346)]
    public void High_performance_takes_the_lowest_clean_scale_that_keeps_320_px_on_the_short_side(int width, int height, int renderWidth, int renderHeight)
    {
        var native = Profile(width, height, 1.0);
        var upscaler = RawFrameUpscaler.For("s", native with { RenderScale = native.PerformanceRenderScale })!;

        Assert.Equal(renderWidth * renderHeight * 4, upscaler.SourceBytes);
    }

    [Fact]
    public void Half_scale_doubles_every_pixel_and_keeps_the_flags()
    {
        var upscaler = RawFrameUpscaler.For("s", Profile(8, 4, 0.5))!;
        var source = Enumerable.Range(1, 8).Select(i => (uint)i).ToArray();
        var pool = new CountingPool();

        var native = upscaler.Apply(Frame(source, pool, StreamFraming.FlagIdr))!;

        var pixels = Pixels(native);
        Assert.Equal(32, pixels.Length);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                Assert.Equal(source[(y / 2) * 4 + x / 2], pixels[y * 8 + x]);
            }
        }
        Assert.True(native.IsIdr);
        Assert.Equal(1, pool.Returned);
        native.Release();
    }

    [Fact]
    public void An_odd_half_height_maps_every_source_row_in_order()
    {
        // An odd half length rounds down to the overlay's even frame length.
        var upscaler = RawFrameUpscaler.For("s", Profile(4, 462, 0.5))!;
        var source = Enumerable.Range(0, 230).SelectMany(row => new[] { (uint)row, (uint)row }).ToArray();

        var native = upscaler.Apply(Frame(source, new CountingPool()))!;

        var rows = Pixels(native).Where((_, i) => i % 4 == 0).ToArray();
        Assert.Equal(462, rows.Length);
        Assert.Equal(0u, rows[0]);
        Assert.Equal(229u, rows[^1]);
        Assert.Equal(230, rows.Distinct().Count());
        Assert.True(rows.Zip(rows.Skip(1), (a, b) => b >= a).All(ordered => ordered));
        native.Release();
    }

    [Fact]
    public void A_frame_of_the_wrong_size_is_dropped_and_released()
    {
        var upscaler = RawFrameUpscaler.For("s", Profile(8, 4, 0.5))!;
        var pool = new CountingPool();

        Assert.Null(upscaler.Apply(Frame(new uint[7], pool)));
        Assert.Equal(1, pool.Returned);
    }
}
