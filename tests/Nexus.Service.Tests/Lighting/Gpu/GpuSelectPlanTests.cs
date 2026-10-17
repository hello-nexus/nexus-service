using System;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// The boot decision on a single-GPU box. The row that matters is a persisted
/// "off" with no crash guard: the branch it belongs to used to return before it
/// ever read the persisted state, so an off latch could not hold and the box
/// re-probed, re-crashed and restarted every few seconds.
/// </summary>
public class GpuSelectPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static GpuSelectDecision Single(string? state, string? guard) =>
        GpuSelectPlan.NextAction(state, guard, usableAdapters: 1, Now);

    [Fact]
    public void SoleGpu_Unprobed_Probes()
    {
        Assert.Equal(GpuSelectAction.Probe, Single("unprobed", null).Action);
    }

    [Fact]
    public void SoleGpu_CrashGuard_LatchesOffWithoutProbing()
    {
        Assert.Equal(GpuSelectAction.DeclineAndLatch, Single("unprobed", "integrated").Action);
    }

    [Fact]
    public void SoleGpu_PersistedOff_DoesNotProbe()
    {
        var decision = Single("off", null);

        Assert.Equal(GpuSelectAction.DeclineNoProbe, decision.Action);
        // A bare token carries no timestamp, so the caller stamps one and the
        // re-probe clock starts.
        Assert.True(decision.RestampLatch);
    }

    [Fact]
    public void SoleGpu_FreshLatch_HoldsUntilItExpires()
    {
        var latched = new GpuSelectState(GpuSelectState.Off, Now.AddMinutes(-5), 1);

        Assert.Equal(GpuSelectAction.DeclineNoProbe,
            GpuSelectPlan.NextAction(latched.Format(), null, 1, Now).Action);
    }

    [Fact]
    public void SoleGpu_ExpiredLatch_ProbesAgain()
    {
        var latched = new GpuSelectState(GpuSelectState.Off, Now.AddHours(-2), 1);

        var decision = GpuSelectPlan.NextAction(latched.Format(), null, 1, Now);

        Assert.Equal(GpuSelectAction.Probe, decision.Action);
        Assert.Contains("expired", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SoleGpu_LatchWindowIsCompressible()
    {
        var latched = new GpuSelectState(GpuSelectState.Off, Now.AddMinutes(-2), 1);

        Assert.Equal(GpuSelectAction.Probe,
            GpuSelectPlan.NextAction(latched.Format(), null, 1, Now, TimeSpan.FromMinutes(1)).Action);
    }

    [Fact]
    public void MultiGpu_RememberedCard_WarmsWithoutProbing()
    {
        var decision = GpuSelectPlan.NextAction("integrated", null, 2, Now);

        Assert.Equal(GpuSelectAction.WarmRemembered, decision.Action);
        Assert.Equal(GpuSelectState.Integrated, decision.Card);
    }

    [Fact]
    public void MultiGpu_CrashGuardOnTheRememberedCard_Demotes()
    {
        var decision = GpuSelectPlan.NextAction("integrated", "integrated", 2, Now);

        Assert.Equal(GpuSelectAction.Probe, decision.Action);
        Assert.Equal(GpuSelectState.Unprobed, decision.Card);
    }

    [Fact]
    public void MultiGpu_CrashGuardOnTheOtherCard_KeepsTheRememberedOne()
    {
        Assert.Equal(GpuSelectAction.WarmRemembered,
            GpuSelectPlan.NextAction("integrated", "discrete", 2, Now).Action);
    }

    [Fact]
    public void MultiGpu_FreshLatch_HoldsAndExpiredLatchProbes()
    {
        var held = new GpuSelectState(GpuSelectState.Off, Now.AddMinutes(-5), 1);
        var stale = new GpuSelectState(GpuSelectState.Off, Now.AddHours(-2), 1);

        Assert.Equal(GpuSelectAction.DeclineNoProbe, GpuSelectPlan.NextAction(held.Format(), null, 2, Now).Action);
        Assert.Equal(GpuSelectAction.Probe, GpuSelectPlan.NextAction(stale.Format(), null, 2, Now).Action);
    }

    private static readonly GpuEnvironment EnvA = new("aaaa", "41");
    private static readonly GpuEnvironment EnvB = new("bbbb", "41");
    private static readonly GpuEnvironment EnvARebooted = new("aaaa", "42");

    private static string FreshLatch(GpuEnvironment env) =>
        new GpuSelectState(GpuSelectState.Off, Now.AddMinutes(-5), 2, env.Fingerprint, env.Boot).Format();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FreshLatch_SameEnvironment_Holds(int adapters)
    {
        Assert.Equal(GpuSelectAction.DeclineNoProbe,
            GpuSelectPlan.NextAction(FreshLatch(EnvA), null, adapters, Now, env: EnvA).Action);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FreshLatch_DriverOrCardChanged_ProbesWithAFreshStreak(int adapters)
    {
        var decision = GpuSelectPlan.NextAction(FreshLatch(EnvA), null, adapters, Now, env: EnvB);

        Assert.Equal(GpuSelectAction.Probe, decision.Action);
        Assert.True(decision.FreshStreak);
        Assert.Contains("changed", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshLatch_Rebooted_Probes()
    {
        var decision = GpuSelectPlan.NextAction(FreshLatch(EnvA), null, 1, Now, env: EnvARebooted);

        Assert.Equal(GpuSelectAction.Probe, decision.Action);
        Assert.Contains("rebooted", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshLatch_FromABuildThatRecordedNoEnvironment_Probes()
    {
        var latched = new GpuSelectState(GpuSelectState.Off, Now.AddMinutes(-5), 1);

        var decision = GpuSelectPlan.NextAction(latched.Format(), null, 1, Now, env: EnvA);

        Assert.Equal(GpuSelectAction.Probe, decision.Action);
        Assert.True(decision.FreshStreak);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FreshLatch_CrashGuardOutranksAStaleEnvironment(int adapters)
    {
        Assert.Equal(GpuSelectAction.DeclineAndLatch,
            GpuSelectPlan.NextAction(FreshLatch(EnvA), "integrated", adapters, Now, env: EnvB).Action);
    }

    [Fact]
    public void MultiGpu_RememberedCard_WarmsWhateverTheEnvironment()
    {
        Assert.Equal(GpuSelectAction.WarmRemembered,
            GpuSelectPlan.NextAction("integrated", null, 2, Now, env: EnvB).Action);
    }

    [Theory]
    [InlineData("unprobed", null)]
    [InlineData("off", null)]
    [InlineData("integrated", "integrated")]
    public void NoAdapter_WaitsWithoutProbingOrLatching(string state, string? guard)
    {
        var decision = GpuSelectPlan.NextAction(state, guard, 0, Now, env: EnvA);

        Assert.Equal(GpuSelectAction.WaitForAdapter, decision.Action);
        Assert.False(decision.RestampLatch);
    }

    [Fact]
    public void NoAdapter_FreshLatchStillWaitsRatherThanHolding()
    {
        Assert.Equal(GpuSelectAction.WaitForAdapter,
            GpuSelectPlan.NextAction(FreshLatch(EnvA), null, 0, Now, env: EnvA).Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void UnreadableState_Probes(string? state)
    {
        Assert.Equal(GpuSelectAction.Probe, Single(state, null).Action);
    }

    [Fact]
    public void EmptyCrashGuardFile_IsNotACrash()
    {
        Assert.Equal(GpuSelectAction.Probe, Single("unprobed", "   ").Action);
    }
}
