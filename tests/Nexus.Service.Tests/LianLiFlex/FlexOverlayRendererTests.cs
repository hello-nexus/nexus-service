using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using SkiaSharp;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexOverlayRendererTests
{
    private static LianLiFlexOverlaySettings Settings(string type, Action<LianLiFlexOverlaySettings>? tweak = null)
    {
        var s = new LianLiFlexOverlaySettings { Type = type, Speed = 5 };
        tweak?.Invoke(s);
        return s;
    }

    private static FlexOverlayRenderer Make(LianLiFlexOverlaySettings s, SKBitmap? background = null, int size = 40, int seed = 1)
    {
        var sprite = TestImages.Solid(size, size, SKColors.Red);
        return new FlexOverlayRenderer(s, background, new[] { sprite }, new[] { 100 }, seed);
    }

    // Center of the red pixels in the decoded frame, null when none.
    private static (double X, double Y)? FindSprite(byte[] jpeg)
    {
        using var bmp = TestImages.Decode(jpeg);
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 180 && c.Green < 90 && c.Blue < 90)
                {
                    sx += x;
                    sy += y;
                    n++;
                }
            }
        }

        return n == 0 ? null : (sx / n, sy / n);
    }

    private static int RedCount(byte[] jpeg)
    {
        using var bmp = TestImages.Decode(jpeg);
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 180 && c.Green < 90 && c.Blue < 90)
                {
                    n++;
                }
            }
        }

        return n;
    }

    [Theory]
    [InlineData("circular")]
    [InlineData("rotate")]
    [InlineData("bounce")]
    [InlineData("collision")]
    public void Output_is_a_400x400_jpeg_and_deterministic(string type)
    {
        using var r = Make(Settings(type));
        var a = r.RenderJpeg(0.7);
        var b = r.RenderJpeg(0.7);
        var info = TestImages.Identify(a);
        Assert.Equal(new ImageSize(400, 400), info);
        Assert.Equal(a, b);
        using var other = Make(Settings(type));
        Assert.Equal(a, other.RenderJpeg(0.7));
    }

    [Fact]
    public void Circular_starts_right_of_center_and_moves_a_quarter_turn_clockwise()
    {
        // Speed 5 = 1 revolution per second.
        using var r = Make(Settings("circular"));
        var start = FindSprite(r.RenderJpeg(0))!.Value;
        Assert.InRange(start.X, 300 - 3, 300 + 3);
        Assert.InRange(start.Y, 200 - 3, 200 + 3);
        var quarter = FindSprite(r.RenderJpeg(0.25))!.Value;
        Assert.InRange(quarter.X, 200 - 3, 200 + 3);
        Assert.InRange(quarter.Y, 300 - 3, 300 + 3);
    }

    [Fact]
    public void Circular_counter_clockwise_goes_up_first()
    {
        using var r = Make(Settings("circular", s => s.Direction = 1));
        var quarter = FindSprite(r.RenderJpeg(0.25))!.Value;
        Assert.InRange(quarter.Y, 100 - 3, 100 + 3);
    }

    [Fact]
    public void Rotate_returns_near_the_start_after_a_full_cycle()
    {
        // Non-square sprite so orientation changes the pixels.
        var sprite = TestImages.Solid(60, 20, SKColors.Red);
        using var r = new FlexOverlayRenderer(Settings("rotate"), null, new[] { sprite }, new[] { 100 });
        var start = r.RenderJpeg(0);
        var half = r.RenderJpeg(0.125);
        var cycle = r.RenderJpeg(0.5);
        Assert.NotEqual(start, half);
        Assert.True(Math.Abs(RedCount(start) - RedCount(cycle)) < 60);
        var c0 = FindSprite(start)!.Value;
        var c1 = FindSprite(half)!.Value;
        Assert.InRange(c1.X, c0.X - 2, c0.X + 2);
    }

    [Fact]
    public void Bounce_stays_in_area_and_settles()
    {
        using var r = Make(Settings("bounce", s => s.Loop = false));
        for (var t = 0.0; t < 6.0; t += 0.05)
        {
            var p = FindSprite(r.RenderJpeg(t));
            Assert.NotNull(p);
            Assert.InRange(p!.Value.X, 20, 380);
            Assert.InRange(p.Value.Y, 20, 380);
        }

        var rest = FindSprite(r.RenderJpeg(30))!.Value;
        Assert.True(Math.Abs(rest.Y - 200) < 3, $"y={rest.Y}");
        Assert.True(r.Finished);
        var start = FindSprite(r.RenderJpeg(0))!.Value;
        Assert.InRange(start.Y, 120 - 3, 120 + 3);
    }

    [Fact]
    public void Collision_stays_inside_the_circle_and_is_seed_deterministic()
    {
        var radius = 180 - 40 / 2;
        using var r = Make(Settings("collision"), seed: 7);
        for (var t = 0.0; t < 40.0; t += 0.5)
        {
            var p = FindSprite(r.RenderJpeg(t));
            Assert.NotNull(p);
            var dx = p!.Value.X - 200;
            var dy = p.Value.Y - 200;
            Assert.True(Math.Sqrt(dx * dx + dy * dy) <= radius + 4, $"t={t}");
        }

        Assert.False(r.Finished);
        using var same = Make(Settings("collision"), seed: 7);
        using var diff = Make(Settings("collision"), seed: 8);
        Assert.Equal(r.RenderJpeg(12), same.RenderJpeg(12));
        Assert.NotEqual(r.RenderJpeg(12), diff.RenderJpeg(12));
        // Going back in time replays the same frames.
        var later = r.RenderJpeg(20);
        Assert.Equal(same.RenderJpeg(12), r.RenderJpeg(12));
        Assert.Equal(later, r.RenderJpeg(20));
    }

    [Fact]
    public void Non_loop_circular_finishes_after_one_orbit_and_holds()
    {
        using var r = Make(Settings("circular", s => s.Loop = false));
        r.RenderJpeg(0.5);
        Assert.False(r.Finished);
        var end = r.RenderJpeg(1.0);
        Assert.True(r.Finished);
        Assert.Equal(end, r.RenderJpeg(3.7));
    }

    [Fact]
    public void Looping_never_finishes()
    {
        foreach (var type in new[] { "circular", "rotate", "bounce", "collision" })
        {
            using var r = Make(Settings(type));
            r.RenderJpeg(100);
            Assert.False(r.Finished);
        }
    }

    [Fact]
    public void Non_loop_rotate_finishes()
    {
        using var r = Make(Settings("rotate", s => s.Loop = false));
        r.RenderJpeg(0.1);
        Assert.False(r.Finished);
        r.RenderJpeg(0.6);
        Assert.True(r.Finished);
    }

    [Fact]
    public void Zoom_scales_the_sprite()
    {
        using var small = Make(Settings("rotate", s => s.Zoom = 50));
        using var big = Make(Settings("rotate", s => s.Zoom = 200));
        var a = RedCount(small.RenderJpeg(0));
        var b = RedCount(big.RenderJpeg(0));
        Assert.InRange(a, 15 * 15, 25 * 25);
        Assert.InRange(b, 75 * 75, 85 * 85);
    }

    [Fact]
    public void Background_null_is_black_and_bitmap_is_drawn()
    {
        using var black = Make(Settings("circular"));
        using var bmp = TestImages.Decode(black.RenderJpeg(0));
        var corner = bmp.GetPixel(2, 2);
        Assert.True(corner.Red < 10 && corner.Green < 10 && corner.Blue < 10);

        using var withBg = Make(Settings("circular"), TestImages.Solid(400, 400, SKColors.Blue));
        using var bmp2 = TestImages.Decode(withBg.RenderJpeg(0));
        Assert.True(bmp2.GetPixel(2, 2).Blue > 200);
    }

    [Fact]
    public void Frame_intervals_follow_the_type_rates()
    {
        Assert.Equal(50, Make(Settings("rotate")).FrameIntervalMs);
        Assert.Equal(40, Make(Settings("collision")).FrameIntervalMs);
        Assert.Equal(50, Make(Settings("bounce")).FrameIntervalMs);
        // 1000 / (20 + 4 * 5)
        Assert.Equal(25, Make(Settings("circular", s => s.Speed = 5)).FrameIntervalMs);
    }

    [Fact]
    public void Gif_sprite_advances_its_frames_by_delay()
    {
        var red = TestImages.Solid(40, 40, SKColors.Red);
        var green = TestImages.Solid(40, 40, SKColors.Lime);
        using var r = new FlexOverlayRenderer(Settings("rotate"), null, new List<SKBitmap> { red, green }, new[] { 100, 100 });
        using var first = TestImages.Decode(r.RenderJpeg(0.0));
        using var second = TestImages.Decode(r.RenderJpeg(0.15));
        Assert.True(first.GetPixel(200, 200).Red > 180);
        Assert.True(second.GetPixel(200, 200).Green > 180);
    }
}
