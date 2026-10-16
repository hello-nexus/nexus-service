using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralGSensorParserTests
{
    private static byte[] Block(short x, short y, short z) =>
        new[] { (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, (byte)(z >> 8), (byte)z, (byte)0 };

    [Fact]
    public void Parse_reads_a_block_captured_from_an_astral_rtx_5090()
    {
        // Registers 0x70-0x76 of a ROG Astral RTX 5090 standing in its case:
        // X 0x102A (4138, clamped to 4096), Y 0x0054 (84), Z 0xFFCA (-54).
        var angles = AstralGSensorParser.Parse(new byte[] { 0x10, 0x2A, 0x00, 0x54, 0xFF, 0xCA, 0x00 });

        Assert.NotNull(angles);
        Assert.Equal(88.603f, angles.Value.X, 0.001f);
        Assert.Equal(1.175f, angles.Value.Y, 0.001f);
        Assert.Equal(90.755f, angles.Value.Z, 0.001f);
    }

    [Fact]
    public void Parse_reads_a_card_lying_flat_as_zero_tilt()
    {
        var angles = AstralGSensorParser.Parse(Block(0, 0, 4096));

        Assert.NotNull(angles);
        Assert.Equal(0f, angles.Value.X, 0.0001f);
        Assert.Equal(0f, angles.Value.Y, 0.0001f);
        Assert.Equal(0f, angles.Value.Z, 0.0001f);
    }

    [Fact]
    public void Parse_clamps_each_axis_to_full_scale()
    {
        var angles = AstralGSensorParser.Parse(Block(short.MaxValue, 0, 0));

        Assert.NotNull(angles);
        Assert.Equal(90f, angles.Value.X, 0.0001f);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void Parse_rejects_a_block_of_the_wrong_size(int length)
    {
        Assert.Null(AstralGSensorParser.Parse(new byte[length]));
    }

    [Fact]
    public void Parse_rejects_a_block_that_does_not_read_as_about_one_g()
    {
        Assert.Null(AstralGSensorParser.Parse(new byte[AstralGSensorParser.BlockSize]));
        Assert.Null(AstralGSensorParser.Parse(Block(-1, -1, -1)));
        Assert.Null(AstralGSensorParser.Parse(Block(4096, 4096, 4096)));
    }
}
