using Nexus.Service.Lighting.Capture;

namespace Nexus.Service.Tests.Lighting;

public class DesktopRotationTests
{
    // Each surface cell holds its own pixel index, so a mapped value identifies its source pixel.
    private const int SurfW = 4;
    private const int SurfH = 2;

    private static int[,] Render(int dstW, int dstH, int rotation)
    {
        var grid = new int[dstH, dstW];
        for (var y = 0; y < dstH; y++)
        {
            for (var x = 0; x < dstW; x++)
            {
                var (sx, sy) = DesktopRotation.MapToSurface(x, y, dstW, dstH, SurfW, SurfH, rotation);
                Assert.InRange(sx, 0, SurfW - 1);
                Assert.InRange(sy, 0, SurfH - 1);
                grid[y, x] = sy * SurfW + sx;
            }
        }
        return grid;
    }

    [Fact]
    public void Identity_IsOneToOne()
    {
        var g = Render(4, 2, DesktopRotation.Identity);
        Assert.Equal(new[] { 0, 1, 2, 3 }, new[] { g[0, 0], g[0, 1], g[0, 2], g[0, 3] });
        Assert.Equal(new[] { 4, 5, 6, 7 }, new[] { g[1, 0], g[1, 1], g[1, 2], g[1, 3] });
    }

    [Fact]
    public void Rotate90_RotatesSurfaceClockwise()
    {
        var g = Render(2, 4, DesktopRotation.Rotate90);
        Assert.Equal(new[] { 4, 0 }, new[] { g[0, 0], g[0, 1] });
        Assert.Equal(new[] { 5, 1 }, new[] { g[1, 0], g[1, 1] });
        Assert.Equal(new[] { 7, 3 }, new[] { g[3, 0], g[3, 1] });
    }

    [Fact]
    public void Rotate270_RotatesSurfaceCounterClockwise()
    {
        var g = Render(2, 4, DesktopRotation.Rotate270);
        Assert.Equal(new[] { 3, 7 }, new[] { g[0, 0], g[0, 1] });
        Assert.Equal(new[] { 0, 4 }, new[] { g[3, 0], g[3, 1] });
    }

    [Fact]
    public void Rotate180_ReversesBothAxes()
    {
        var g = Render(4, 2, DesktopRotation.Rotate180);
        Assert.Equal(new[] { 7, 6, 5, 4 }, new[] { g[0, 0], g[0, 1], g[0, 2], g[0, 3] });
        Assert.Equal(new[] { 3, 2, 1, 0 }, new[] { g[1, 0], g[1, 1], g[1, 2], g[1, 3] });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Downscaled_StaysInsideSurface(int rotation)
    {
        var swap = DesktopRotation.SwapsAxes(rotation);
        var dw = swap ? 3 : 7;
        var dh = swap ? 7 : 3;
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                var (sx, sy) = DesktopRotation.MapToSurface(x, y, dw, dh, SurfW, SurfH, rotation);
                Assert.InRange(sx, 0, SurfW - 1);
                Assert.InRange(sy, 0, SurfH - 1);
            }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void SwapsAxes_OnlyFor90And270(int rotation, bool expected)
    {
        Assert.Equal(expected, DesktopRotation.SwapsAxes(rotation));
    }

    // Transliteration of the PS in Shaders/MirrorDownsample.hlsl, which uses unsigned 32-bit math.
    private static (uint X, uint Y) ShaderMap(uint x, uint y, uint dstW, uint dstH, uint surfW, uint surfH, int rotation) => rotation switch
    {
        DesktopRotation.Rotate90 => (y * surfW / dstH, (dstW - 1 - x) * surfH / dstW),
        DesktopRotation.Rotate180 => ((dstW - 1 - x) * surfW / dstW, (dstH - 1 - y) * surfH / dstH),
        DesktopRotation.Rotate270 => ((dstH - 1 - y) * surfW / dstH, x * surfH / dstW),
        _ => (x * surfW / dstW, y * surfH / dstH),
    };

    [Theory]
    [InlineData(1920, 1200, 1)]
    [InlineData(3840, 2160, 1)]
    [InlineData(7680, 4320, 1)]
    [InlineData(1200, 1920, 2)]
    [InlineData(1920, 1080, 3)]
    [InlineData(2160, 3840, 4)]
    public void Shader_unsigned_mapping_matches_cpu_mapping_on_the_mirror_grid(int surfW, int surfH, int rotation)
    {
        const int w = 160;
        const int h = 90;
        const int dstW = w;
        const int dstH = h;
        for (var y = 0; y < dstH; y++)
        {
            for (var x = 0; x < dstW; x++)
            {
                var cpu = DesktopRotation.MapToSurface(x, y, dstW, dstH, surfW, surfH, rotation);
                var gpu = ShaderMap((uint)x, (uint)y, (uint)dstW, (uint)dstH, (uint)surfW, (uint)surfH, rotation);
                Assert.Equal(((uint)cpu.X, (uint)cpu.Y), gpu);
            }
        }
    }
}
