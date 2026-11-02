using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class SuperIoOpenPolicyTests
{
    private const string Armoury = "armoury-crate";
    private const string Lighting = "asus-lighting-service";

    [Theory]
    [InlineData(true, new string[0], new[] { Armoury, Lighting })]
    [InlineData(true, new[] { Armoury }, new[] { Lighting })]
    [InlineData(true, new[] { "ARMOURY-CRATE", Lighting }, new string[0])]
    [InlineData(false, new string[0], new string[0])]
    public void HoldersToWaitFor_SkipsWhatTheSweepWillNotEnd(bool sweepAllowed, string[] excluded, string[] expected)
    {
        Assert.Equal(expected, SuperIoOpenPolicy.HoldersToWaitFor(sweepAllowed, excluded));
    }

    // Each script entry is what the process scan reports on one poll; the last repeats.
    private static (Task<SuperIoOpenPolicy.WaitResult> Result, Func<int> Polls) Run(bool[] script)
    {
        var now = 0L;
        var polls = 0;
        var result = SuperIoOpenPolicy.WaitAsync(
            new[] { Armoury },
            _ => script[Math.Min(polls++, script.Length - 1)],
            d => { now += (long)d.TotalMilliseconds; return Task.CompletedTask; },
            () => now);
        return (result, () => polls);
    }

    [Fact]
    public async Task Wait_NoHolderRunning_ReturnsAtOnce()
    {
        var (result, polls) = Run(new[] { false });
        Assert.Equal(new SuperIoOpenPolicy.WaitResult(false, false), await result);
        Assert.Equal(1, polls());
    }

    [Fact]
    public async Task Wait_HolderEnds_ReturnsAfterQuietPeriod()
    {
        // Running for three polls, then gone: quiet period of five one-second polls.
        var (result, polls) = Run(new[] { true, true, true, false });
        Assert.Equal(new SuperIoOpenPolicy.WaitResult(true, false), await result);
        Assert.Equal(3 + 6, polls());
    }

    [Fact]
    public async Task Wait_HolderRespawnsInsideQuietPeriod_RestartsTheQuietPeriod()
    {
        var (result, polls) = Run(new[] { true, false, false, true, false });
        Assert.Equal(new SuperIoOpenPolicy.WaitResult(true, false), await result);
        Assert.Equal(4 + 6, polls());
    }

    [Fact]
    public async Task Wait_HolderNeverEnds_TimesOut()
    {
        var (result, polls) = Run(new[] { true });
        Assert.Equal(new SuperIoOpenPolicy.WaitResult(true, true), await result);
        Assert.Equal(46, polls());
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public void ShouldReopen_NeedsSeenHoldersNowGoneAndADeadWindow(bool sawHolders, bool dead, bool holdersNow, bool expected)
    {
        Assert.Equal(expected, SuperIoOpenPolicy.ShouldReopen(new SuperIoOpenPolicy.WaitResult(sawHolders, false), dead, holdersNow));
    }

    [Theory]
    [InlineData(new[] { -1f, -1f }, new[] { 0f, 0f, 0f }, true)]
    [InlineData(new[] { -1f }, new float[0], true)]
    [InlineData(new[] { -1f, 41f }, new[] { 0f, 0f }, false)]
    [InlineData(new[] { -1f, -1f }, new[] { 0f, 912f }, false)]
    [InlineData(new float[0], new[] { 0f }, false)]
    public void IsDeadWindow_MatchesOnlyTheAllOnesSignature(float[] temps, float[] fans, bool expected)
    {
        Assert.Equal(expected, SuperIoOpenPolicy.IsDeadWindow(temps.Select(t => (float?)t).ToList(), fans.Select(f => (float?)f).ToList()));
    }

    [Fact]
    public void IsDeadWindow_UnreadValuesNeverCountAsDead()
    {
        Assert.False(SuperIoOpenPolicy.IsDeadWindow(new float?[] { -1f, null }, new float?[] { 0f }));
        Assert.False(SuperIoOpenPolicy.IsDeadWindow(new float?[] { -1f }, new float?[] { null }));
    }
}
