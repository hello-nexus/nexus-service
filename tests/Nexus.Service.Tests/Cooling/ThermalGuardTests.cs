using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class ThermalGuardTests
{
    private const double L = 95;

    [Theory]
    [InlineData(70, 0)]
    [InlineData(80, 0)]
    [InlineData(85, 50)]
    [InlineData(90, 100)]
    [InlineData(94, 100)]
    public void FloorRamp_IsLinearFromLimitMinus15ToLimitMinus5(double temp, int expected)
    {
        Assert.Equal(expected, ThermalGuard.FloorFor(temp, L));
    }

    [Fact]
    public void BelowFloorBand_ChangesNothing()
    {
        var g = new ThermalGuard();
        var o = g.Step(0, 60, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Normal, o.State);
        Assert.Equal(0, o.FloorDuty);
        Assert.False(o.ForceMax);
    }

    [Fact]
    public void Floor_StateReportsRampedDuty()
    {
        var g = new ThermalGuard();
        var o = g.Step(0, 85, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Floor, o.State);
        Assert.Equal(50, o.FloorDuty);
    }

    [Fact]
    public void LimitTrip_NeedsThreeSecondsSustainedAtLimitMinus5()
    {
        var g = new ThermalGuard();
        Assert.Equal(ThermalGuardStates.Floor, g.Step(0, 91, L, 50, 50).State);
        Assert.Equal(ThermalGuardStates.Floor, g.Step(2000, 91, L, 50, 50).State);
        var o = g.Step(3000, 91, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Tripped, o.State);
        Assert.True(o.TripStarted);
        Assert.True(o.ForceMax);
        Assert.Equal(100, o.FloorDuty);
        Assert.Equal(ThermalTripReasons.Limit, o.TripReason);
    }

    [Fact]
    public void LimitTrip_DipBelowThresholdResetsSustainTimer()
    {
        var g = new ThermalGuard();
        g.Step(0, 91, L, 50, 50);
        g.Step(2000, 85, L, 50, 50);
        var o = g.Step(4000, 91, L, 50, 50);
        Assert.NotEqual(ThermalGuardStates.Tripped, o.State);
    }

    [Fact]
    public void LimitTrip_AtLimitIsInstant()
    {
        var g = new ThermalGuard();
        var o = g.Step(0, 95, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Tripped, o.State);
        Assert.True(o.TripStarted);
    }

    [Fact]
    public void T1Replay_TempRisingWithFansAtZeroAndIdleLoad_TripsOnCoolingLossBeforeNinetyC()
    {
        // 61 C to 114 C over 17 minutes at 2 percent load, every fan commanded to 0.
        var g = new ThermalGuard();
        ThermalGuardOutput? tripped = null;
        double trippedAt = 0;
        const int totalSeconds = 17 * 60;
        for (var s = 0; s <= totalSeconds; s++)
        {
            var temp = 61 + 53.0 * s / totalSeconds;
            var o = g.Step(s * 1000L, temp, L, 2, 0);
            if (o.TripStarted)
            {
                tripped = o;
                trippedAt = temp;
                break;
            }
        }
        Assert.NotNull(tripped);
        Assert.Equal(ThermalTripReasons.CoolingLoss, tripped!.Value.TripReason);
        Assert.True(trippedAt < 90, $"tripped at {trippedAt:0.0} C");
    }

    [Theory]
    [InlineData(2.0, 60.0)] // fans commanded up: not a cooling loss
    [InlineData(80.0, 0.0)] // genuine load: a rise is expected
    public void CoolingLoss_NotDeclaredWhenFansOrLoadExplainTheRise(double load, double duty)
    {
        var g = new ThermalGuard();
        for (var s = 0; s <= 600; s++)
        {
            var o = g.Step(s * 1000L, 50 + s * 0.05, L, load, duty);
            Assert.False(o.TripStarted);
        }
    }

    [Fact]
    public void CoolingLoss_SlowRiseUnderTwoCPerMinuteDoesNotTrip()
    {
        var g = new ThermalGuard();
        for (var s = 0; s <= 900; s++)
        {
            var o = g.Step(s * 1000L, 50 + s / 60.0 * 1.5, L, 2, 0);
            Assert.False(o.TripStarted);
        }
    }

    [Fact]
    public void Escalation_StillRisingThirtySecondsAfterTrip_ReleasesAllOnce()
    {
        var g = new ThermalGuard();
        g.Step(0, 96, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Tripped, g.State);

        var o = g.Step(30_000, 97.5, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Escalated, o.State);
        Assert.True(o.ReleaseAllNow);
        Assert.True(o.StopWriting);

        var again = g.Step(31_000, 98, L, 50, 50);
        Assert.False(again.ReleaseAllNow);
        Assert.True(again.StopWriting);
    }

    [Fact]
    public void Escalation_NotTriggeredWhenTempHeldFlat()
    {
        var g = new ThermalGuard();
        g.Step(0, 96, L, 50, 50);
        var o = g.Step(30_000, 96.5, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Tripped, o.State);
        Assert.False(o.ReleaseAllNow);
    }

    [Fact]
    public void Release_RequiresSixtySecondsBelowLimitMinus20()
    {
        var g = new ThermalGuard();
        g.Step(0, 96, L, 50, 50);

        // Above limit-20 (75): stays tripped no matter how long.
        Assert.Equal(ThermalGuardStates.Tripped, g.Step(10_000, 80, L, 50, 50).State);
        Assert.Equal(ThermalGuardStates.Tripped, g.Step(200_000, 80, L, 50, 50).State);

        Assert.Equal(ThermalGuardStates.Tripped, g.Step(201_000, 70, L, 50, 50).State);
        Assert.Equal(ThermalGuardStates.Tripped, g.Step(260_000, 70, L, 50, 50).State);
        var o = g.Step(261_000, 70, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Normal, o.State);
        Assert.True(o.TripEnded);
        Assert.Equal(0, o.FloorDuty);
    }

    [Fact]
    public void Release_ReboundAboveThresholdRestartsTheSustainTimer()
    {
        var g = new ThermalGuard();
        g.Step(0, 96, L, 50, 50);
        g.Step(1000, 70, L, 50, 50);
        g.Step(50_000, 76, L, 50, 50);
        var o = g.Step(70_000, 70, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Tripped, o.State);
    }

    [Fact]
    public void ImplausibleOrMissingReading_IsInactiveAndNeverTrips()
    {
        var g = new ThermalGuard();
        Assert.Equal(ThermalGuardStates.Inactive, g.Step(0, null, L, 50, 50).State);
        Assert.Equal(ThermalGuardStates.Inactive, g.Step(1000, double.NaN, L, 50, 50).State);
        Assert.Equal(ThermalGuardStates.Inactive, g.Step(2000, 200, L, 50, 50).State);
        Assert.Equal(ThermalGuardStates.Inactive, g.Step(3000, 0, L, 50, 50).State);
    }

    [Fact]
    public void MissingReadingWhileTripped_HoldsTheTrip()
    {
        var g = new ThermalGuard();
        g.Step(0, 96, L, 50, 50);
        var o = g.Step(1000, null, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Tripped, o.State);
        Assert.Equal(100, o.FloorDuty);
    }

    [Fact]
    public void GpuGuard_HandsBackThenForcesThenResumes()
    {
        var g = new GpuThermalGuard();
        Assert.Equal(GpuGuardStates.Normal, g.Step(0, 70, 90).State);
        var hb = g.Step(1000, 86, 90);
        Assert.True(hb.HandBackNow);
        Assert.Equal(GpuGuardStates.HandedBack, hb.State);

        Assert.Equal(GpuGuardStates.Forced, g.Step(31_000, 88, 90).State);

        Assert.Equal(GpuGuardStates.Forced, g.Step(40_000, 70, 90).State);
        var resumed = g.Step(100_000, 70, 90);
        Assert.True(resumed.Resumed);
        Assert.Equal(GpuGuardStates.Normal, resumed.State);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i9-14900K", 100.0, 100.0, "hardware")]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", null, 95.0, "spec")]
    [InlineData("AMD Ryzen 5 1600", null, 95.0, "default")]
    [InlineData("Some Other CPU", null, 90.0, "default")]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", 0.0, 95.0, "spec")]
    public void LimitResolution_HardwareThenSpecThenDefault(string model, double? tjMax, double expected, string source)
    {
        var limit = ThermalLimits.ResolveCpu(model, tjMax);
        Assert.Equal(expected, limit.LimitC);
        Assert.Equal(source, limit.Source);
    }

    [Fact]
    public void MaxPlausible_IgnoresNonFiniteAndImplausibleReadings()
    {
        Assert.Equal(70, ThermalLimits.MaxPlausible(new[] { 55.0, double.NaN, 70, 0, -5, 199 }));
        Assert.Null(ThermalLimits.MaxPlausible(new[] { double.NaN, 0.0 }));
    }
}
