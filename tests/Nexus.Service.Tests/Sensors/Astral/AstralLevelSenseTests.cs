using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralLevelSenseTests
{
    private static byte[] Block(short x, short y, short z)
    {
        var b = new byte[AstralGSensorParser.BlockSize];
        (b[0], b[1]) = ((byte)(x >> 8), (byte)x);
        (b[2], b[3]) = ((byte)(y >> 8), (byte)y);
        (b[4], b[5]) = ((byte)(z >> 8), (byte)z);
        return b;
    }

    [Fact]
    public void Parse_returns_angles_of_the_normalized_vector()
    {
        var a = AstralGSensorParser.Parse(Block(4096, 0, 0))!.Value;
        Assert.Equal(90f, a.X, 3);
        Assert.Equal(0f, a.Y, 3);
        Assert.Equal(90f, a.Z, 3);

        var level = AstralGSensorParser.Parse(Block(0, 0, 4096))!.Value;
        Assert.Equal(0f, level.Z, 3);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(100, 100, 100)]
    [InlineData(4096, 4096, 4096)]
    public void Parse_rejects_blocks_that_do_not_read_as_one_g(short x, short y, short z)
    {
        Assert.Null(AstralGSensorParser.Parse(Block(x, y, z)));
    }

    [Fact]
    public void Parse_rejects_a_wrong_length()
    {
        Assert.Null(AstralGSensorParser.Parse(new byte[3]));
    }

    [Fact]
    public void Mode_picks_the_most_frequent_value_and_the_smallest_on_a_tie()
    {
        Assert.Equal(2f, AstralLevelSense.Mode(new[] { 1f, 2f, 2f, 3f, 3f }.AsSpan()));
        Assert.Equal(1f, AstralLevelSense.Mode(new[] { 3f, 1f, 2f, 5f, 4f }.AsSpan()));
        Assert.Equal(7f, AstralLevelSense.Mode(new[] { 7f, 7f, 1f, 9f, 8f }.AsSpan()));
    }

    [Fact]
    public void Calibration_completes_on_the_fifth_reading_and_deviation_follows()
    {
        var level = new AstralLevelSense();
        level.StartCalibration();
        var rest = new AstralAngles(90f, 1.2f, 90f);
        for (var i = 0; i < AstralLevelSense.CalibrationSamples - 1; i++)
        {
            Assert.Null(level.Add(rest));
            Assert.True(level.IsCalibrating);
        }
        Assert.NotNull(level.Add(rest));
        Assert.False(level.IsCalibrating);
        Assert.Equal(rest, level.Calibration);

        Assert.Equal(0f, level.Posture!.Value.Sag, 3);
        level.Add(rest with { Y = 4.2f });
        Assert.True(level.Posture!.Value.Sag > 0);
    }

    [Fact]
    public void Sag_and_lean_carry_direction_across_zero()
    {
        var level = new AstralLevelSense();
        level.Load(new AstralAngles(90f, 1.2f, 90f));
        level.Add(new AstralAngles(90f, -3f, 90f));
        Assert.Equal(-4.2f, level.Posture!.Value.Sag, 3);

        var leaning = new AstralLevelSense();
        leaning.Load(new AstralAngles(90f, 1.2f, 90f));
        leaning.Add(new AstralAngles(85f, 1.2f, 95f));
        Assert.True(leaning.Posture!.Value.Lean > 0);
    }

    [Fact]
    public void A_recalibration_keeps_the_old_one_until_it_completes()
    {
        var level = new AstralLevelSense();
        var old = new AstralAngles(90f, 1f, 90f);
        level.Load(old);
        level.StartCalibration();
        level.Add(new AstralAngles(90f, 5f, 90f));
        Assert.Equal(old, level.Calibration);
    }
}
