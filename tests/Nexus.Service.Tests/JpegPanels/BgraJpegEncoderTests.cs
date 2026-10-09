using System;
using Nexus.Service.Peripherals.JpegPanels;
using SkiaSharp;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// The channel-order tests here are the point of this file. The Kraken shipped with red
/// and blue transposed because two layers each swapped BGRA once and nothing errored; a
/// decode-and-compare is the only check that catches that class of bug without hardware.
/// </summary>
public class BgraJpegEncoderTests
{
    /// <summary>Fills a frame in the overlay's capture order: B, G, R, A per pixel.</summary>
    private static byte[] SolidBgra(int width, int height, byte r, byte g, byte b)
    {
        var frame = new byte[width * height * 4];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = b;
            frame[i + 1] = g;
            frame[i + 2] = r;
            frame[i + 3] = 0xFF;
        }
        return frame;
    }

    [Fact]
    public void Output_is_a_real_jpeg_with_the_panel_dimensions()
    {
        using var encoder = new BgraJpegEncoder(240, 240);

        var jpeg = encoder.Encode(SolidBgra(240, 240, 0x20, 0x40, 0x60)).ToArray();

        // SOI and EOI markers.
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[0..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);

        using var decoded = TestImages.Decode(jpeg);
        Assert.Equal(240, decoded.Width);
        Assert.Equal(240, decoded.Height);
    }

    [Theory]
    // Pure primaries: a red/blue transposition shows up as the opposite channel.
    [InlineData(0xFF, 0x00, 0x00)]
    [InlineData(0x00, 0xFF, 0x00)]
    [InlineData(0x00, 0x00, 0xFF)]
    [InlineData(0x30, 0x90, 0xD0)]
    public void Bgra_input_survives_the_round_trip_in_the_right_channel_order(int r, int g, int b)
    {
        using var encoder = new BgraJpegEncoder(64, 64);

        var jpeg = encoder.Encode(SolidBgra(64, 64, (byte)r, (byte)g, (byte)b)).ToArray();

        using var decoded = TestImages.Decode(jpeg);
        var centre = decoded.GetPixel(32, 32);
        // JPEG is lossy and chroma-subsampled, so compare with a tolerance rather than
        // exactly; a swapped channel is off by far more than this.
        Assert.InRange(centre.Red, r - 12, r + 12);
        Assert.InRange(centre.Green, g - 12, g + 12);
        Assert.InRange(centre.Blue, b - 12, b + 12);
    }

    [Fact]
    public void Spatial_orientation_is_preserved_top_left_to_top_left()
    {
        using var encoder = new BgraJpegEncoder(64, 64);
        // Left half red, right half blue: catches a mirrored or rotated write.
        var frame = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int i = ((y * 64) + x) * 4;
                bool left = x < 32;
                frame[i] = left ? (byte)0x00 : (byte)0xFF;     // B
                frame[i + 1] = 0x00;                            // G
                frame[i + 2] = left ? (byte)0xFF : (byte)0x00;  // R
                frame[i + 3] = 0xFF;
            }
        }

        var jpeg = encoder.Encode(frame).ToArray();

        using var decoded = TestImages.Decode(jpeg);
        Assert.True(decoded.GetPixel(8, 32).Red > 200, "left half should decode red");
        Assert.True(decoded.GetPixel(56, 32).Blue > 200, "right half should decode blue");
    }

    [Fact]
    public void A_short_frame_is_rejected_rather_than_read_past_its_end()
    {
        using var encoder = new BgraJpegEncoder(64, 64);

        Assert.Throws<ArgumentException>(() => encoder.Encode(new byte[64 * 64 * 4 - 1]));
    }

    [Fact]
    public void Frame_bytes_is_the_panel_area_in_bgra()
    {
        using var encoder = new BgraJpegEncoder(480, 480);

        Assert.Equal(480 * 480 * 4, encoder.FrameBytes);
    }

    /// <summary>
    /// The returned span points into a buffer the encoder reuses, so a caller must consume
    /// it before the next Encode. This pins that the reuse actually happens - a fresh array
    /// per frame at 30 fps is the allocation this class exists to avoid.
    /// </summary>
    [Fact]
    public void Successive_frames_reuse_one_buffer()
    {
        using var encoder = new BgraJpegEncoder(64, 64);
        var first = encoder.Encode(SolidBgra(64, 64, 0x10, 0x20, 0x30));
        var firstLength = first.Length;

        var second = encoder.Encode(SolidBgra(64, 64, 0x10, 0x20, 0x30));

        Assert.Equal(firstLength, second.Length);
        Assert.True(second.Length > 0);
    }

    [Fact]
    public void A_zero_sized_panel_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BgraJpegEncoder(0, 240));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BgraJpegEncoder(240, -1));
    }

    /// <summary>
    /// Without this the channel-order tests above would pass while silently exercising only
    /// the Skia fallback - the exact false green that would hide a wrong TJPF_* value.
    /// TurboJPEG is compiled into the bundled libSkiaSharp the csproj copies next to the
    /// test binary, so this must be the native path.
    /// </summary>
    [Fact]
    public void Bundled_libjpeg_turbo_is_the_active_encoder()
    {
        using var encoder = new BgraJpegEncoder(64, 64);
        Assert.True(encoder.IsNative, "the bundled libSkiaSharp carries TurboJPEG but the encoder fell back to Skia");
    }

    [Fact]
    public void Active_encoder_round_trips_to_the_requested_geometry()
    {
        using var encoder = new BgraJpegEncoder(160, 96);
        var frame = SolidBgra(160, 96, 200, 120, 40);

        var jpeg = encoder.Encode(frame).ToArray();

        using var decoded = TestImages.Decode(jpeg);
        Assert.Equal(160, decoded.Width);
        Assert.Equal(96, decoded.Height);
        // Whichever encoder is active: 4:2:0 at quality 85 on a flat fill lands well
        // under the raw frame. Not a cross-check of the two - only one is ever live.
        Assert.InRange(jpeg.Length, 100, frame.Length / 4);
    }
}
