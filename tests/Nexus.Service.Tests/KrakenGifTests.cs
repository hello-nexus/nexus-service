using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class KrakenGifTests
{
    // 2-frame 8x8 gif with a transparent background and a green block on frame 0's left half.
    private const string TwoFrameGifBase64 =
        "R0lGODlhCAAIAIEAAP8A/yjIUAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQJCgAAACwAAAAACAAIAAAIGwADCAwAoCCAgQQNIjR4cCDDhQodRhT4UGLBgAAh+QQJCgAAACwEAAAABAAIAIH/AP8oyFAAAAAAAAAIDAADCBxIsKDBgwQDAgA7";

    [FfmpegFact]
    public async Task Fit_produces_a_panel_sized_gif()
    {
        var source = WriteTemp(Convert.FromBase64String(TwoFrameGifBase64));
        try
        {
            var gif = await KrakenGif.FitAsync(source, 16, 12, CancellationToken.None);

            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(gif, 0, 6));
            Assert.Equal(16, BitConverter.ToUInt16(gif, 6));
            Assert.Equal(12, BitConverter.ToUInt16(gif, 8));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [FfmpegFact]
    public async Task One_quarter_turn_moves_the_top_left_corner_to_the_top_right()
    {
        // The still path's rotation: top-left lands top-right for one clockwise turn.
        var source = WriteTemp(Convert.FromBase64String(TwoFrameGifBase64));
        try
        {
            var fitted = await KrakenGif.FitAsync(source, 8, 8, CancellationToken.None);
            var rotated = await KrakenGif.RotateAsync(fitted, 1, CancellationToken.None);

            var before = await FirstFrameRgbAsync(fitted);
            var after = await FirstFrameRgbAsync(rotated);
            Assert.Equal(Pixel(before, 0, 0), Pixel(after, 7, 0));
            Assert.Equal(Pixel(before, 0, 7), Pixel(after, 0, 0));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [FfmpegFact]
    public async Task Transparent_pixels_come_out_black()
    {
        var source = WriteTemp(Convert.FromBase64String(TwoFrameGifBase64));
        try
        {
            var fitted = await KrakenGif.FitAsync(source, 8, 8, CancellationToken.None);

            var frame = await FirstFrameRgbAsync(fitted);
            Assert.Equal(((byte)0, (byte)0, (byte)0), Pixel(frame, 7, 0));
            Assert.NotEqual(((byte)0, (byte)0, (byte)0), Pixel(frame, 0, 0));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task No_rotation_returns_the_same_bytes()
    {
        var gif = new byte[] { 1, 2, 3 };

        Assert.Same(gif, await KrakenGif.RotateAsync(gif, 0, CancellationToken.None));
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-kraken-gif-test-{Guid.NewGuid():N}.gif");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<byte[]> FirstFrameRgbAsync(byte[] gif)
    {
        var input = WriteTemp(gif);
        var output = Path.ChangeExtension(input, ".rgb");
        try
        {
            await MediaImporter.RunFfmpeg("-y", "-v", "error", "-i", input, "-frames:v", "1",
                "-f", "rawvideo", "-pix_fmt", "rgb24", output);
            return await File.ReadAllBytesAsync(output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static (byte R, byte G, byte B) Pixel(byte[] rgb, int x, int y)
    {
        int i = ((y * 8) + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }
}
