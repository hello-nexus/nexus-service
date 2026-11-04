using System;
using System.Security.Cryptography;
using Nexus.Service.Peripherals.SteelSeries;
using Xunit;

namespace Nexus.Service.Tests.SteelSeries;

public class ApexOledProtocolTests
{
    private const int W = ApexOledProtocol.Width;
    private const int H = ApexOledProtocol.Height;

    private static bool[] Lit(params (int X, int Y)[] points)
    {
        var px = new bool[W * H];
        foreach (var (x, y) in points)
        {
            px[y * W + x] = true;
        }
        return px;
    }

    [Fact]
    public void Rows_put_sixteen_bytes_per_row_with_the_leftmost_pixel_in_the_high_bit()
    {
        var packed = ApexOledProtocol.Pack(Lit((0, 0), (7, 0), (8, 0), (0, 1), (127, 39)), ApexOledLayout.Rows);

        Assert.Equal(ApexOledProtocol.FrameBytes, packed.Length);
        Assert.Equal(0x81, packed[0]);
        Assert.Equal(0x80, packed[1]);
        Assert.Equal(0x80, packed[16]);
        Assert.Equal(0x01, packed[639]);
        Assert.Equal(5, CountBits(packed));
    }

    [Fact]
    public void Columns_put_eight_vertical_pixels_per_byte_with_the_top_one_in_bit_zero()
    {
        var packed = ApexOledProtocol.Pack(Lit((0, 0), (0, 7), (0, 8), (5, 0), (127, 39)), ApexOledLayout.Columns);

        Assert.Equal(0x81, packed[0]);
        Assert.Equal(0x01, packed[128]);
        Assert.Equal(0x01, packed[5]);
        Assert.Equal(0x80, packed[639]);
        Assert.Equal(5, CountBits(packed));
    }

    // Hashes carried over from the patch's tests: they pin both packings against regression.
    // Only the row packing has been seen working on a keyboard.
    [Theory]
    [InlineData(ApexOledLayout.Rows, "94a7cc8686264a0784ed78ae53fc507e789107c2e12c4743ced61aa542911f5e")]
    [InlineData(ApexOledLayout.Columns, "d91f2b16c9c93bdf90495ea3322951995c13eab93e252aaff2ae1504e4c0c351")]
    public void Packs_the_test_pattern_to_the_known_bytes(ApexOledLayout layout, string sha256)
    {
        var px = new bool[W * H];
        for (var x = 0; x < W; x++)
        {
            px[x] = px[(H - 1) * W + x] = true;
        }
        for (var y = 0; y < H; y++)
        {
            px[y * W] = px[y * W + W - 1] = true;
        }
        for (var x = 0; x < W; x++)
        {
            px[Math.Min(H - 1, x * (H - 1) / (W - 1)) * W + x] = true;
        }
        for (var y = 2; y < 12; y++)
        {
            for (var x = 2; x < 12; x++)
            {
                px[y * W + x] = true;
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(ApexOledProtocol.Pack(px, layout))).ToLowerInvariant();

        Assert.Equal(sha256, hash);
    }

    [Fact]
    public void Pack_refuses_a_picture_of_the_wrong_size()
    {
        Assert.Throws<ArgumentException>(() => ApexOledProtocol.Pack(new bool[W * H - 1], ApexOledLayout.Rows));
    }

    [Fact]
    public void A_frame_is_a_644_byte_feature_report_with_the_live_frame_command()
    {
        var packed = new byte[ApexOledProtocol.FrameBytes];
        packed[0] = 0xAA;
        packed[639] = 0x55;

        var report = ApexOledProtocol.FrameReport(packed);

        Assert.Equal(644, report.Length);
        Assert.Equal(new byte[] { 0x00, 0x1F, 0x81, 0xAA }, report[..4]);
        Assert.Equal(0x55, report[642]);
        Assert.Equal(0x00, report[643]);
    }

    [Fact]
    public void A_frame_report_refuses_a_wrong_size_bitmap()
    {
        Assert.Throws<ArgumentException>(() => ApexOledProtocol.FrameReport(new byte[ApexOledProtocol.FrameBytes + 1]));
    }

    [Fact]
    public void The_hand_back_is_a_65_byte_output_report()
    {
        var report = ApexOledProtocol.HandBackReport();

        Assert.Equal(65, report.Length);
        Assert.Equal(new byte[] { 0x00, 0x1F, 0x82 }, report[..3]);
        Assert.All(report[3..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Each_model_has_its_layout()
    {
        Assert.Equal(ApexOledLayout.Rows, ApexOledProtocol.ModelFor(0x1628)!.Layout);
        Assert.Equal(ApexOledLayout.Columns, ApexOledProtocol.ModelFor(0x1642)!.Layout);
        Assert.Null(ApexOledProtocol.ModelFor(0x1614));
        Assert.Equal(new[] { 0x1628, 0x1642 }, ApexOledProtocol.ProductIds);
    }

    private static int CountBits(byte[] bytes)
    {
        var n = 0;
        foreach (var b in bytes)
        {
            n += System.Numerics.BitOperations.PopCount(b);
        }
        return n;
    }
}
