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

    // Feeds one reading per second from startSec up to endSec, returning the last output.
    private static ThermalGuardOutput Feed(ThermalGuard g, int startSec, int endSec, double temp)
    {
        ThermalGuardOutput last = default;
        for (var sec = startSec; sec <= endSec; sec++)
        {
            last = g.Step(sec * 1000L, temp, L, 100, 100);
        }
        return last;
    }

    [Fact]
    public void Escalation_NotBeforeTwentySecondsOrBelowTheLimit()
    {
        var g = Tripped();
        // Settled below the limit by the time the twenty seconds are up: no escalation.
        Feed(g, 6, 20, 90);
        for (var sec = 21; sec <= 40; sec++)
        {
            Assert.Equal(ThermalGuardStates.Tripped, g.Step(sec * 1000L, 90, L, 100, 100, writesNotLanding: true).State);
        }
    }

    [Fact]
    public void Release_RequiresSixtySecondsBelowLimitMinusTen()
    {
        var g = Tripped();

        // At or above limit-10 (85): stays tripped however long.
        Assert.Equal(ThermalGuardStates.Tripped, Feed(g, 10, 200, 90).State);

        // Settled below it: still tripped until the sustain has run.
        Assert.Equal(ThermalGuardStates.Tripped, Feed(g, 201, 262, 80).State);
        var o = Feed(g, 263, 270, 80);
        Assert.Equal(ThermalGuardStates.Normal, o.State);
        Assert.Equal(0, o.FloorDuty);
        Assert.Equal(ThermalGuardStates.Normal, g.State);
    }

    [Fact]
    public void Release_ReboundAboveThresholdRestartsTheSustainTimer()
    {
        var g = Tripped();
        Feed(g, 6, 40, 80);
        Feed(g, 41, 70, 90);
        Assert.Equal(ThermalGuardStates.Tripped, Feed(g, 71, 110, 80).State);
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
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", null, 90.0, "spec")]
    [InlineData("AMD Ryzen 7 5700X 8-Core Processor", null, 90.0, "spec")]
    [InlineData("AMD Ryzen 9 5900XT 16-Core Processor", null, 95.0, "default")]
    [InlineData("AMD Ryzen 5 5600 6-Core Processor", null, 95.0, "default")]
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

    // A single-second spike on otherwise idle readings.
    [Fact]
    public void ASingleSampleSpike_WithFansAtZeroAndIdleLoad_DoesNotTripCoolingLoss()
    {
        var g = new ThermalGuard();
        for (var s = 0; s <= 600; s++)
        {
            var temp = s == 300 ? 90.1 : (s == 301 ? 73.3 : 64.8);
            var o = g.Step(s * 1000L, temp, L, 3, 0);
            Assert.False(o.TripStarted, $"tripped at {s} s");
        }
    }

    [Fact]
    public void ASingleSampleSpike_DoesNotMoveTheFloor()
    {
        var g = new ThermalGuard();
        for (var s = 0; s < 10; s++)
        {
            Assert.Equal(0, g.Step(s * 1000L, 64.8, L, 3, 0).FloorDuty);
        }
        // On the raw reading the floor would jump; smoothed it stays at zero.
        var spike = g.Step(10_000, 90, L, 3, 0);
        Assert.Equal(0, spike.FloorDuty);
        Assert.Equal(ThermalGuardStates.Normal, spike.State);
        Assert.Equal(0, g.Step(11_000, 65, L, 3, 0).FloorDuty);
    }

    [Fact]
    public void ASustainedRise_StillRaisesTheFloor_AndTripsCoolingLossNearTheGate()
    {
        var g = new ThermalGuard();
        var floorSeen = false;
        double? trippedAt = null;
        for (var s = 0; s <= 1020; s++)
        {
            var temp = 61 + 53.0 * s / 1020;
            var o = g.Step(s * 1000L, temp, L, 2, 0);
            floorSeen |= o.FloorDuty > 0;
            if (o.TripStarted)
            {
                trippedAt = temp;
                break;
            }
        }
        Assert.True(floorSeen || trippedAt is not null);
        Assert.NotNull(trippedAt);
        // The median lags the rise by a couple of samples.
        Assert.InRange(trippedAt!.Value, L - 15, L - 15 + 1.5);
    }

    [Fact]
    public void TheLimitTrip_StillReadsTheRawValue()
    {
        var g = new ThermalGuard();
        Assert.Equal(ThermalGuardStates.Floor, g.Step(0, 98, L, 100, 80).State);
        var tripped = g.Step(5000, 98, L, 100, 80);
        Assert.Equal(ThermalGuardStates.Tripped, tripped.State);
    }

    [Fact]
    public void SmoothingWindow_IsANamedThreshold()
    {
        var wide = new ThermalGuard(new ThermalGuardThresholds { SmoothingSamples = 1 });
        for (var s = 0; s < 5; s++)
        {
            wide.Step(s * 1000L, 64.8, L, 3, 0);
        }
        // A one-sample window is the raw reading again.
        Assert.Equal(40, wide.Step(5000, 90, L, 3, 0).FloorDuty);
    }

    [Fact]
    public void ADropoutClearsTheSmoothingWindow_SoOldSamplesDoNotBiasTheFirstReadingsAfterIt()
    {
        var g = new ThermalGuard();
        for (var s = 0; s < 6; s++)
        {
            g.Step(s * 1000L, 90, L, 3, 0);
        }
        Assert.Equal(40, g.Step(6000, 90, L, 3, 0).FloorDuty);

        Assert.Equal(ThermalGuardStates.Inactive, g.Step(7000, null, L, 3, 0).State);

        // Without the clear, the stale 90 C samples would dominate the median and keep the floor up.
        Assert.Equal(0, g.Step(8000, 65, L, 3, 0).FloorDuty);
    }

    [Fact]
    public void TwoHotRawSamples_CannotBeOutvotedByTheMedian_AndSoKeepTheTrip()
    {
        var g = Tripped();
        // Cool long enough that the release timer is almost due.
        Assert.Equal(ThermalGuardStates.Tripped, Feed(g, 6, 66, 80).State);

        // The median stays cool for two more samples, but the raw readings are hot.
        g.Step(67_000, 96, L, 100, 100);
        var o = g.Step(68_000, 96, L, 100, 100);

        Assert.Equal(ThermalGuardStates.Tripped, o.State);
        Assert.False(o.TripEnded);
    }

    [Fact]
    public void AfterALongPauseInTheReadings_OldCoolSamplesDoNotReleaseTheTrip()
    {
        var g = Tripped();
        Feed(g, 6, 40, 80); // the release timer is running

        // Nothing is observed for a long while, then a hot reading arrives.
        var o = g.Step(71_000, 96, L, 100, 100);

        Assert.Equal(ThermalGuardStates.Tripped, o.State);
        Assert.False(o.TripEnded);
    }

    [Fact]
    public void TicksJustUnderASecondApart_DoNotMergeInPairs()
    {
        var g = new ThermalGuard();
        for (var i = 0; i < 10; i++)
        {
            g.Step(i * 990L, 65, L, 3, 0);
        }
        ThermalGuardOutput last = default;
        for (var i = 10; i < 13; i++)
        {
            last = g.Step(i * 990L, 90, L, 3, 0);
        }

        // Three hot samples in a window of five move the median.
        Assert.Equal(40, last.FloorDuty);
    }
}
