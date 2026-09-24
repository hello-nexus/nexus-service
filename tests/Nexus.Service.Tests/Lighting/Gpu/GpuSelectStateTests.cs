using System;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GpuSelectStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("integrated", "integrated")]
    [InlineData("discrete", "discrete")]
    [InlineData("  DISCRETE  ", "discrete")]
    [InlineData("unprobed", "unprobed")]
    [InlineData("", "unprobed")]
    [InlineData("nonsense", "unprobed")]
    public void ParsesTheTokensEarlierBuildsWrote(string text, string expected)
    {
        Assert.Equal(expected, GpuSelectState.Parse(text).Card);
    }

    [Fact]
    public void BareOffParsesAsAnUndatedLatch()
    {
        var state = GpuSelectState.Parse("off");

        Assert.True(state.IsOff);
        Assert.Null(state.OffSince);
        Assert.Equal(1, state.OffStreak);
    }

    [Fact]
    public void LatchRoundTrips()
    {
        var latched = GpuSelectState.Fresh.LatchedOff(Now);

        var parsed = GpuSelectState.Parse(latched.Format());

        Assert.True(parsed.IsOff);
        Assert.Equal(Now, parsed.OffSince);
        Assert.Equal(1, parsed.OffStreak);
    }

    [Fact]
    public void ConsecutiveLatchesGrowTheStreakAndTheWait()
    {
        var first = GpuSelectState.Fresh.LatchedOff(Now);
        var second = first.LatchedOff(Now);
        var third = second.LatchedOff(Now);

        Assert.Equal(1, first.OffStreak);
        Assert.Equal(3, third.OffStreak);
        Assert.True(GpuSelectState.ReprobeDelay(second.OffStreak) > GpuSelectState.ReprobeDelay(first.OffStreak));
        Assert.True(GpuSelectState.ReprobeDelay(third.OffStreak) > GpuSelectState.ReprobeDelay(second.OffStreak));
    }

    [Fact]
    public void AWorkingCardResetsTheStreak()
    {
        var latched = GpuSelectState.Fresh.LatchedOff(Now).LatchedOff(Now);

        Assert.Equal(0, GpuSelectState.Working(GpuSelectState.Integrated).OffStreak);
        Assert.Equal(1, GpuSelectState.Working(GpuSelectState.Integrated).LatchedOff(Now).OffStreak);
        Assert.Equal(2, latched.OffStreak);
    }

    [Fact]
    public void AnUndatedLatchHoldsUntilItIsStamped()
    {
        var bare = GpuSelectState.Parse("off");

        Assert.False(bare.ReprobeDue(Now));

        var stamped = bare.Restamped(Now);

        Assert.Equal(1, stamped.OffStreak);
        Assert.False(stamped.ReprobeDue(Now));
        Assert.True(stamped.ReprobeDue(Now + GpuSelectState.ReprobeDelay(1)));
    }

    [Theory]
    [InlineData("off since=99999999999999 streak=1")]
    [InlineData("off since=-99999999999999 streak=1")]
    [InlineData("off since=9223372036854775807 streak=1")]
    [InlineData("off since=notanumber streak=1")]
    [InlineData("off since= streak=1")]
    public void AnUnusableTimestampParsesAsAnUndatedLatch(string text)
    {
        // FromUnixTimeSeconds throws outside DateTimeOffset's range; a throw here
        // escapes into the warmup's blanket handler and leaves the context
        // neither started nor failed, which reads as "initializing" forever.
        var state = GpuSelectState.Parse(text);

        Assert.True(state.IsOff);
        Assert.Null(state.OffSince);
        Assert.False(state.ReprobeDue(Now));
    }

    [Fact]
    public void ACrashGuardLatchDoesNotLengthenTheWait()
    {
        // The guard is left by ANY death inside the init window, so escalating on
        // it would put a healthy card on the longest wait after three reboots.
        var once = GpuSelectState.Fresh.LatchedOff(Now, escalate: false);
        var twice = once.LatchedOff(Now, escalate: false);
        var thrice = twice.LatchedOff(Now, escalate: false);

        Assert.Equal(1, thrice.OffStreak);
        Assert.Equal(GpuSelectState.ReprobeDelay(1), GpuSelectState.ReprobeDelay(thrice.OffStreak));
    }

    [Fact]
    public void ANonEscalatingLatchKeepsAStreakAProbeAlreadyEarned()
    {
        var probed = GpuSelectState.Fresh.LatchedOff(Now).LatchedOff(Now);

        Assert.Equal(2, probed.LatchedOff(Now, escalate: false).OffStreak);
    }

    [Fact]
    public void ClockMovingBackwardsDoesNotStrandTheLatch()
    {
        var latched = GpuSelectState.Fresh.LatchedOff(Now);

        Assert.True(latched.ReprobeDue(Now.AddHours(-3)));
    }

    [Fact]
    public void ANonOffStateIsAlwaysDue()
    {
        Assert.True(GpuSelectState.Working(GpuSelectState.Integrated).ReprobeDue(Now));
        Assert.True(GpuSelectState.Fresh.ReprobeDue(Now));
    }

    [Fact]
    public void ReprobeInCountsDownAndFloorsAtZero()
    {
        var latched = GpuSelectState.Fresh.LatchedOff(Now);

        Assert.Equal(GpuSelectState.ReprobeDelay(1), latched.ReprobeIn(Now));
        Assert.Equal(TimeSpan.Zero, latched.ReprobeIn(Now.AddDays(1)));
    }

    [Fact]
    public void AForcedDelayOverridesTheSchedule()
    {
        var forced = TimeSpan.FromMinutes(2);

        Assert.Equal(forced, GpuSelectState.ReprobeDelay(3, forced));
        Assert.Equal(GpuSelectState.ReprobeDelay(3), GpuSelectState.ReprobeDelay(3, TimeSpan.Zero));
    }

    [Fact]
    public void LatchRoundTripsItsEnvironment()
    {
        var env = new GpuEnvironment("0badf00d1234abcd", "17");
        var latched = GpuSelectState.Fresh.LatchedOff(Now, env: env);

        var parsed = GpuSelectState.Parse(latched.Format());

        Assert.Equal("off since=1788868800 streak=1 fp=0badf00d1234abcd boot=17", latched.Format());
        Assert.Equal(env.Fingerprint, parsed.Fingerprint);
        Assert.Equal(env.Boot, parsed.Boot);
        Assert.False(parsed.LatchStale(env));
        Assert.True(parsed.LatchStale(new GpuEnvironment("ffff", "17")));
        Assert.True(parsed.LatchStale(new GpuEnvironment("0badf00d1234abcd", "18")));
    }

    [Fact]
    public void LatchWithoutAnEnvironmentFormatsAsBefore()
    {
        var latched = GpuSelectState.Fresh.LatchedOff(Now);

        Assert.Equal("off since=1788868800 streak=1", latched.Format());
        // Nothing to compare against: not stale, and never stale to a caller
        // that supplies no environment.
        Assert.False(latched.LatchStale(null));
        Assert.True(latched.LatchStale(new GpuEnvironment("ffff", "17")));
    }

    [Fact]
    public void UndatedLatchIsNeverStale()
    {
        Assert.False(GpuSelectState.Parse("off").LatchStale(new GpuEnvironment("ffff", "17")));
    }

    [Fact]
    public void RestampBindsTheEnvironment()
    {
        var env = new GpuEnvironment("cafe", "3");

        var stamped = GpuSelectState.Parse("off").Restamped(Now, env);

        Assert.Equal(env.Fingerprint, stamped.Fingerprint);
        Assert.Equal(env.Boot, stamped.Boot);
        Assert.Equal(1, stamped.OffStreak);
    }
}
