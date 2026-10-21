using Nexus.Service.Lighting.Capture;

namespace Nexus.Service.Tests.Lighting;

public class DesktopRotationTests
{
    // Native surface 4x2, value at (x, y) = y * 4 + x.
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
}
