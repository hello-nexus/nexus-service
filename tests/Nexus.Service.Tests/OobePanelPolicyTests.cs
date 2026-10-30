using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests;

public sealed class OobePanelPolicyTests
{
    [Theory]
    [InlineData("alice", true)]
    [InlineData("alice", false)]
    [InlineData("defaultuser0", true)]
    public void SignedInUser_IsAlwaysTheConsoleUserPath_AndNeverAsksOobe(string user, bool sessionExists)
    {
        var asked = false;
        var plan = OobePanelPolicy.Decide(user, sessionExists, () => { asked = true; return true; });
        Assert.Equal(ConsoleLaunchPlan.ConsoleUser, plan);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoUser_OobeComplete_WaitsForLogon_AsToday(bool sessionExists)
    {
        Assert.Equal(ConsoleLaunchPlan.WaitForLogon, OobePanelPolicy.Decide("", sessionExists, () => false));
    }

    [Fact]
    public void NoUser_InOobe_LaunchesWithoutUser()
    {
        Assert.Equal(ConsoleLaunchPlan.OobeNoUser, OobePanelPolicy.Decide("", true, () => true));
    }

    [Fact]
    public void NoUser_NoConsoleSession_WaitsEvenInOobe_AndNeverAsksOobe()
    {
        var asked = false;
        var plan = OobePanelPolicy.Decide("", false, () => { asked = true; return true; });
        Assert.Equal(ConsoleLaunchPlan.WaitForLogon, plan);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    public void Reap_OnlyWhenSomethingIsTrackedAndOobeIsOver(int tracked, bool inOobe, bool expected)
    {
        Assert.Equal(expected, OobePanelPolicy.ShouldReap(tracked, () => inOobe));
    }

    [Fact]
    public void Reap_WithNothingTracked_NeverAsksOobe()
    {
        var asked = false;
        Assert.False(OobePanelPolicy.ShouldReap(0, () => { asked = true; return false; }));
        Assert.False(asked);
    }
}
