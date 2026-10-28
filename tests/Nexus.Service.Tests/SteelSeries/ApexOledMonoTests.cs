using System;
using System.IO;
using System.Text;
using Nexus.Service.Peripherals.SteelSeries;
using Xunit;

namespace Nexus.Service.Tests.SteelSeries;

public class ApexOledMonoTests
{
    private const int W = ApexOledProtocol.Width;
    private const int H = ApexOledProtocol.Height;

    internal static byte[] Solid(byte b, byte g, byte r)
    {
        var f = new byte[ApexOledMono.FrameLength];
        for (var i = 0; i < f.Length; i += 4)
        {
            f[i] = b;
            f[i + 1] = g;
            f[i + 2] = r;
            f[i + 3] = 255;
        }
        return f;
    }

    private static void Set(byte[] frame, int x, int y, byte v)
    {
        var p = (y * W + x) * 4;
        frame[p] = frame[p + 1] = frame[p + 2] = v;
    }

    [Fact]
    public void A_black_frame_lights_nothing_and_a_white_frame_lights_everything()
    {
        var lit = new bool[W * H];

        Assert.True(ApexOledMono.Convert(Solid(0, 0, 0), lit));
        Assert.DoesNotContain(true, lit);
        Assert.True(ApexOledMono.Convert(Solid(255, 255, 255), lit));
        Assert.DoesNotContain(false, lit);
    }

    [Theory]
    [InlineData(95, false)]
    [InlineData(96, true)]
    public void The_threshold_sits_at_the_documented_luma(byte gray, bool expected)
    {
        var lit = new bool[W * H];

        ApexOledMono.Convert(Solid(gray, gray, gray), lit);

        Assert.Equal(expected, lit[0]);
    }

    [Fact]
    public void Luma_weights_green_over_red_over_blue()
    {
        var lit = new bool[W * H];

        // BGR order. Pure red and pure blue sit under the threshold; a red with some green in it lights.
        ApexOledMono.Convert(Solid(0, 0, 255), lit);
        Assert.False(lit[0]);
        ApexOledMono.Convert(Solid(255, 0, 0), lit);
        Assert.False(lit[0]);
        ApexOledMono.Convert(Solid(0, 255, 0), lit);
        Assert.True(lit[0]);
        ApexOledMono.Convert(Solid(60, 60, 255), lit);
        Assert.True(lit[0]);
    }

    [Fact]
    public void A_frame_of_the_wrong_size_is_refused()
    {
        Assert.False(ApexOledMono.Convert(new byte[ApexOledMono.FrameLength - 4], new bool[W * H]));
        Assert.False(ApexOledMono.Convert(new byte[ApexOledMono.FrameLength], new bool[W * H - 1]));
    }

    // 3x5 digit glyphs, one string per row.
    private static readonly string[][] Glyphs =
    {
        new[] { "###", "# #", "# #", "# #", "###" },
        new[] { " # ", "## ", " # ", " # ", "###" },
        new[] { "###", "  #", "###", "#  ", "###" },
        new[] { "###", "  #", "###", "  #", "###" },
        new[] { "# #", "# #", "###", "  #", "  #" },
        new[] { "###", "#  ", "###", "  #", "###" },
        new[] { "###", "#  ", "###", "# #", "###" },
        new[] { "###", "  #", "  #", "  #", "  #" },
        new[] { "###", "# #", "###", "# #", "###" },
        new[] { "###", "# #", "###", "  #", "###" },
    };

    // Draws digits at an integer scale, then softens the edges with a 3x3 box blur the way a
    // browser's anti-aliasing leaves partial coverage on glyph borders.
    private static byte[] DigitsFrame(string digits, int scale, int x0, int y0, byte ink, byte background)
    {
        var gray = new int[W * H];
        Array.Fill(gray, background);
        var x = x0;
        foreach (var ch in digits)
        {
            var glyph = Glyphs[ch - '0'];
            for (var gy = 0; gy < 5; gy++)
            {
                for (var gx = 0; gx < 3; gx++)
                {
                    if (glyph[gy][gx] != '#')
                    {
                        continue;
                    }
                    for (var dy = 0; dy < scale; dy++)
                    {
                        for (var dx = 0; dx < scale; dx++)
                        {
                            gray[(y0 + gy * scale + dy) * W + x + gx * scale + dx] = ink;
                        }
                    }
                }
            }
            x += 4 * scale;
        }
        var frame = new byte[ApexOledMono.FrameLength];
        for (var y = 0; y < H; y++)
        {
            for (var xx = 0; xx < W; xx++)
            {
                var sum = 0;
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int sx = xx + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= W || sy >= H)
                        {
                            continue;
                        }
                        sum += gray[sy * W + sx];
                        n++;
                    }
                }
                Set(frame, xx, y, (byte)(sum / n));
            }
        }
        return frame;
    }

    private static string Ascii(bool[] lit)
    {
        var sb = new StringBuilder();
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                sb.Append(lit[y * W + x] ? '#' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("72", 6, 255)]
    [InlineData("1234567890", 2, 255)]
    [InlineData("1234567890", 2, 140)]
    public void Anti_aliased_digits_keep_their_shape_through_the_threshold(string digits, int scale, byte ink)
    {
        var frame = DigitsFrame(digits, scale, 8, 5, ink, 0);
        var lit = new bool[W * H];

        Assert.True(ApexOledMono.Convert(frame, lit));

        // Every glyph cell keeps its core: the centre of each filled glyph square is lit.
        var x = 8;
        foreach (var ch in digits)
        {
            var glyph = Glyphs[ch - '0'];
            for (var gy = 0; gy < 5; gy++)
            {
                for (var gx = 0; gx < 3; gx++)
                {
                    var cx = x + gx * scale + scale / 2;
                    var cy = 5 + gy * scale + scale / 2;
                    if (glyph[gy][gx] == '#' && scale > 2)
                    {
                        Assert.True(lit[cy * W + cx], $"{ch} cell {gx},{gy}");
                    }
                    else if (glyph[gy][gx] == ' ' && scale > 2)
                    {
                        Assert.False(lit[cy * W + cx], $"{ch} gap {gx},{gy}");
                    }
                }
            }
            x += 4 * scale;
        }

        var dump = Environment.GetEnvironmentVariable("APEX_OLED_PREVIEW_DIR");
        if (!string.IsNullOrEmpty(dump))
        {
            File.WriteAllText(Path.Combine(dump, $"digits-{digits}-s{scale}-ink{ink}.txt"), Ascii(lit));
        }
    }
}
