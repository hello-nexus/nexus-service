using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralLevelSenseTests
{
    private static AstralGSensorParser.Angles Sample(float x, float y) => new(x, y, 90f);

    private static void AddTimes(AstralLevelSense levelSense, AstralGSensorParser.Angles sample, int count)
    {
        for (var i = 0; i < count; i++)
        {
            levelSense.Add(sample);
        }
    }

    [Fact]
    public void Add_seeds_the_filter_with_the_first_reading_then_averages_each_new_one_in()
    {
        var levelSense = new AstralLevelSense();

        levelSense.Add(Sample(10f, 2f));
        Assert.Equal(Sample(10f, 2f), levelSense.Smoothed);

        levelSense.Add(Sample(20f, 4f));
        Assert.Equal(Sample(15f, 3f), levelSense.Smoothed);
    }

    [Fact]
    public void Calibration_completes_on_the_fifth_reading()
    {
        var levelSense = new AstralLevelSense();
        levelSense.StartCalibration();

        for (var i = 1; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Assert.Null(levelSense.Add(Sample(88.5f, 1.2f)));
            Assert.Null(levelSense.Deviation);
        }

        var calibration = levelSense.Add(Sample(88.5f, 1.2f));

        Assert.Equal(new AstralLevelSense.Calibration(88.5f, 1.2f, 90f), calibration);
        Assert.False(levelSense.IsCalibrating);
        Assert.Equal((0f, 0f), levelSense.Deviation!.Value);
    }

    [Fact]
    public void Deviation_is_null_without_a_calibration()
    {
        var levelSense = new AstralLevelSense();
        levelSense.Add(Sample(88.5f, 1.2f));

        Assert.Null(levelSense.Deviation);
    }

    [Fact]
    public void Deviation_measures_against_a_loaded_calibration()
    {
        var levelSense = new AstralLevelSense();
        levelSense.Load(new AstralLevelSense.Calibration(88.5f, 1.2f, 90f));

        levelSense.Add(Sample(88f, 1.7f));

        var deviation = levelSense.Deviation;
        Assert.NotNull(deviation);
        Assert.Equal(-0.5f, deviation.Value.X, 0.0001f);
        Assert.Equal(0.5f, deviation.Value.Y, 0.0001f);
    }

    [Fact]
    public void Deviation_compares_magnitudes_when_a_reading_crosses_zero()
    {
        // GPU Tweak: same sign -> current - calibrated, opposite signs -> current + calibrated.
        var levelSense = new AstralLevelSense();
        levelSense.Load(new AstralLevelSense.Calibration(1.2f, -0.5f, 90f));

        levelSense.Add(Sample(-1f, -0.8f));

        var deviation = levelSense.Deviation;
        Assert.NotNull(deviation);
        Assert.Equal(0.2f, deviation.Value.X, 0.0001f);
        Assert.Equal(-0.3f, deviation.Value.Y, 0.0001f);
    }

    [Fact]
    public void Recalibration_keeps_measuring_against_the_previous_level_until_it_completes()
    {
        var levelSense = new AstralLevelSense();
        levelSense.Load(new AstralLevelSense.Calibration(0f, 0f, 90f));
        levelSense.Add(Sample(0f, 2f));

        levelSense.StartCalibration();
        AddTimes(levelSense, Sample(0f, 2f), AstralLevelSense.CalibrationSamples - 1);
        Assert.True(levelSense.IsCalibrating);
        Assert.Equal(2f, levelSense.Deviation!.Value.Y, 0.0001f);

        levelSense.Add(Sample(0f, 2f));
        Assert.False(levelSense.IsCalibrating);
        Assert.Equal(0f, levelSense.Deviation!.Value.Y, 0.0001f);
    }

    // The angles AstralGSensorParser gives for a gravity reading (x, y, z), as unit vector parts.
    private static AstralGSensorParser.Angles FromGravity(double x, double y, double z)
    {
        var n = Math.Sqrt(x * x + y * y + z * z);
        return new AstralGSensorParser.Angles(
            (float)(Math.Asin(x / n) * 180 / Math.PI),
            (float)(Math.Asin(y / n) * 180 / Math.PI),
            (float)(Math.Acos(z / n) * 180 / Math.PI));
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180;

    private static (float Sag, float Lean) PostureAfter(AstralGSensorParser.Angles calibrated, AstralGSensorParser.Angles now)
    {
        var levelSense = new AstralLevelSense();
        levelSense.Load(new AstralLevelSense.Calibration(calibrated.X, calibrated.Y, calibrated.Z));
        levelSense.Add(now);
        return levelSense.Posture!.Value;
    }

    [Fact]
    public void Posture_is_null_without_a_calibration()
    {
        var levelSense = new AstralLevelSense();
        levelSense.Add(Sample(88.5f, 1.2f));

        Assert.Null(levelSense.Posture);
    }

    [Fact]
    public void Sag_is_positive_when_the_free_end_drops()
    {
        // Vertical mount, as captured from an Astral RTX 5090: the free end already
        // 1.2 degrees low; 0.3 degrees more droop.
        var calibrated = FromGravity(Math.Cos(Rad(1.2)), Math.Sin(Rad(1.2)), 0);
        var now = FromGravity(Math.Cos(Rad(1.5)), Math.Sin(Rad(1.5)), 0);

        var (sag, lean) = PostureAfter(calibrated, now);

        Assert.Equal(0.3f, sag, 0.001f);
        Assert.Equal(0f, lean, 0.001f);
    }

    [Fact]
    public void Lean_is_positive_when_the_top_edge_turns_toward_the_fans_on_a_vertical_mount()
    {
        // Upright card (gravity on +X); the top edge tips 0.5 degrees toward the fans (+Z),
        // so "up" picks up a -Z part.
        var calibrated = FromGravity(1, 0, 0);
        var now = FromGravity(Math.Cos(Rad(0.5)), 0, -Math.Sin(Rad(0.5)));

        var (sag, lean) = PostureAfter(calibrated, now);

        Assert.Equal(0f, sag, 0.001f);
        Assert.Equal(0.5f, lean, 0.001f);
    }

    [Fact]
    public void Lean_is_positive_when_the_top_edge_turns_toward_the_fans_on_a_horizontal_mount()
    {
        // Fans facing down (gravity on -Z); the top edge drops 0.5 degrees, which is toward the fans.
        var calibrated = FromGravity(0, 0, -1);
        var now = FromGravity(-Math.Sin(Rad(0.5)), 0, -Math.Cos(Rad(0.5)));

        var (sag, lean) = PostureAfter(calibrated, now);

        Assert.Equal(0f, sag, 0.001f);
        Assert.Equal(0.5f, lean, 0.001f);
    }

    [Fact]
    public void Lean_stays_small_where_the_roll_angle_wraps_around()
    {
        // Upside-down vertical card: "up" sits at 180 degrees around the length, and a small turn
        // toward the backplate crosses to -180.
        var calibrated = FromGravity(-1, 0, 0);
        var now = FromGravity(-Math.Cos(Rad(0.5)), 0, -Math.Sin(Rad(0.5)));

        var (_, lean) = PostureAfter(calibrated, now);

        Assert.Equal(-0.5f, lean, 0.001f);
    }

    [Fact]
    public void Mode_picks_the_most_frequent_value()
    {
        Assert.Equal(2f, AstralLevelSense.Mode(new[] { 1f, 2f, 2f, 3f, 2f }));
    }

    [Fact]
    public void Mode_breaks_a_tie_toward_the_smaller_value()
    {
        Assert.Equal(1f, AstralLevelSense.Mode(new[] { 3f, 3f, 1f, 1f, 5f }));
    }

    [Fact]
    public void Mode_of_all_distinct_values_is_the_smallest()
    {
        Assert.Equal(3f, AstralLevelSense.Mode(new[] { 5f, 4f, 3f, 6f, 7f }));
    }

    [Fact]
    public void Mode_counts_values_within_the_tolerance_as_equal()
    {
        Assert.Equal(1f, AstralLevelSense.Mode(new[] { 1f, 1.000005f, 2f }));
    }
}
