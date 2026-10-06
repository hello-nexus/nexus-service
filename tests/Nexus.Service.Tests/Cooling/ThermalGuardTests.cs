using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class ThermalGuardTests
{
    private const double L = 95;

    [Theory]
    [InlineData(70, 0)]
    [InlineData(80, 0)]
    [InlineData(85, 20)]
    [InlineData(90, 40)]
    [InlineData(95, 60)]
    [InlineData(96.5, 80)]
    [InlineData(98, 100)]
    [InlineData(110, 100)]
    public void FloorRamp_IsZeroToSixtyAtTheLimit_ThenFullThreeAboveIt(double temp, int expected)
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
        var o = g.Step(0, 90, L, 50, 50);
        Assert.Equal(ThermalGuardStates.Floor, o.State);
        Assert.Equal(40, o.FloorDuty);
    }

    [Fact]
    public void SittingAtTheLimit_ForAMinute_NeverTripsAndHoldsSixtyPercent()
    {
        // Ryzen 7000/9000 and recent Intel sit at their limit under load by design.
        var g = new ThermalGuard();
        for (var s = 0; s <= 120; s++)
        {
            var o = g.Step(s * 1000L, 95, L, 100, 80);
            Assert.Equal(ThermalGuardStates.Floor, o.State);
            Assert.Equal(60, o.FloorDuty);
            Assert.False(o.TripStarted);
        }
    }

    [Fact]
    public void JustUnderLimitPlusThree_NeverTrips()
    {
        var g = new ThermalGuard();
        for (var s = 0; s <= 120; s++)
        {
            Assert.False(g.Step(s * 1000L, 97.9, L, 100, 80).TripStarted);
        }
    }

    [Fact]
    public void LimitTrip_NeedsFiveSecondsSustainedAtLimitPlusThree()
    {
        var g = new ThermalGuard();
        Assert.Equal(ThermalGuardStates.Floor, g.Step(0, 98, L, 100, 80).State);
        Assert.Equal(ThermalGuardStates.Floor, g.Step(4000, 98, L, 100, 80).State);
        var o = g.Step(5000, 98, L, 100, 80);
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
        g.Step(0, 99, L, 100, 80);
        g.Step(3000, 95, L, 100, 80);
        var o = g.Step(6000, 99, L, 100, 80);
        Assert.NotEqual(ThermalGuardStates.Tripped, o.State);
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

    private static ThermalGuard Tripped()
    {
        var g = new ThermalGuard();
        g.Step(0, 99, L, 100, 80);
        g.Step(5000, 99, L, 100, 80);
        Assert.Equal(ThermalGuardStates.Tripped, g.State);
        return g;
    }

    [Fact]
    public void Escalation_FansNotRespondingTwentySecondsIntoTheTripAtTheLimit_ReleasesAllOnce()
    {
        var g = Tripped();
        var o = g.Step(25_000, 97, L, 100, 100, writesNotLanding: true);
        Assert.Equal(ThermalGuardStates.Escalated, o.State);
        Assert.True(o.ReleaseAllNow);
        Assert.True(o.StopWriting);

        var again = g.Step(26_000, 98, L, 100, 100, writesNotLanding: true);
        Assert.False(again.ReleaseAllNow);
        Assert.True(again.StopWriting);
    }

    [Fact]
    public void Escalation_NeverOnRisingTemperatureAlone()
    {
        var g = Tripped();
        var o = g.Step(60_000, 110, L, 100, 100, writesNotLanding: false);
        Assert.Equal(ThermalGuardStates.Tripped, o.State);
        Assert.False(o.ReleaseAllNow);
    }

    [Fact]
    public void Escalation_NotBeforeTwentySecondsOrBelowTheLimit()
    {
        var g = Tripped();
        Assert.Equal(ThermalGuardStates.Tripped, g.Step(24_000, 97, L, 100, 100, writesNotLanding: true).State);
        Assert.Equal(ThermalGuardStates.Tripped, g.Step(30_000, 90, L, 100, 100, writesNotLanding: true).State);
    }

    [Fact]
    public void Release_RequiresSixtySecondsBelowLimitMinusTen()
    {
        var g = Tripped();

        // At or above limit-10 (85): stays tripped however long.
        Assert.Equal(ThermalGuardStates.Tripped, g.Step(10_000, 90, L, 100, 100).State);
        Assert.Equal(ThermalGuardStates.Tripped, g.Step(200_000, 90, L, 100, 100).State);

        Assert.Equal(ThermalGuardStates.Tripped, g.Step(201_000, 80, L, 100, 100).State);
        Assert.Equal(ThermalGuardStates.Tripped, g.Step(260_000, 80, L, 100, 100).State);
        var o = g.Step(261_000, 80, L, 100, 100);
        Assert.Equal(ThermalGuardStates.Normal, o.State);
        Assert.True(o.TripEnded);
        Assert.Equal(0, o.FloorDuty);
    }

    [Fact]
    public void Release_ReboundAboveThresholdRestartsTheSustainTimer()
    {
        var g = Tripped();
        g.Step(6000, 80, L, 100, 100);
        g.Step(50_000, 86, L, 100, 100);
        var o = g.Step(70_000, 80, L, 100, 100);
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
        g.Step(0, 99, L, 50, 50);
        g.Step(5000, 99, L, 50, 50);
        var o = g.Step(6000, null, L, 50, 50);
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
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", null, 89.0, "spec")]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", null, 90.0, "spec")]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", null, 95.0, "spec")]
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

    [Fact]
    public void IntelTjMax_IsCoreTempPlusThatCoresDistance_NotTheDistanceItself()
    {
        var temps = new (string, double)[]
        {
            ("CPU Core #1", 35), ("CPU Core #1 Distance to TjMax", 65),
            ("CPU Core #2", 40), ("CPU Core #2 Distance to TjMax", 60),
            ("CPU Package", 42),
        };
        Assert.Equal(100, ThermalLimits.TjMaxFromCoreDistances(temps));
        Assert.Null(ThermalLimits.TjMaxFromCoreDistances(new (string, double)[] { ("CPU Core #1 Distance to TjMax", 65) }));
        Assert.True(ThermalLimits.IsDistanceToTjMax("CPU Core #1 Distance to TjMax"));
        Assert.False(ThermalLimits.IsDistanceToTjMax("CPU Core #1"));
    }

    [Fact]
    public void CoolingLoss_NeverTripsBelowLimitMinus15()
    {
        var g = new ThermalGuard();
        for (var s = 0; s <= 600; s++)
        {
            // 3 C/min, but staying in the 50s: far from the limit.
            var o = g.Step(s * 1000L, 40 + s / 60.0 * 3, L, 2, 0);
            Assert.False(o.TripStarted);
        }
    }

    [Fact]
    public void CoolingLoss_WithNoCpuCoolingFanToJudge_DoesNotTrip()
    {
        var g = new ThermalGuard();
        for (var s = 0; s <= 1000; s++)
        {
            var o = g.Step(s * 1000L, 61 + 53.0 * s / 1020, L, 2, null);
            Assert.False(o.TripStarted && o.TripReason == ThermalTripReasons.CoolingLoss);
        }
    }

    [Fact]
    public void Thresholds_AreOneRecord_AndChangeBehaviour()
    {
        var instant = new ThermalGuard(new ThermalGuardThresholds { LimitSustainMs = 0 });
        Assert.Equal(ThermalGuardStates.Tripped, instant.Step(0, 99, L, 50, 50).State);

        var earlyFloor = new ThermalGuardThresholds { FloorStartBelowLimitC = 30 };
        Assert.True(ThermalGuard.FloorFor(70, L, earlyFloor) > 0);
        Assert.Equal(0, ThermalGuard.FloorFor(70, L));
    }
}
