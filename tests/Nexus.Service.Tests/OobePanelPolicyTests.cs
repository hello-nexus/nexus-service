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
    [InlineData(0, true, false, false)]
    [InlineData(0, false, true, false)]
    [InlineData(2, true, false, false)]
    [InlineData(2, false, false, true)]
    [InlineData(2, true, true, true)]
    public void Reap_WhenTrackedAndOobeOverOrAUserIsOnTheConsole(int tracked, bool inOobe, bool userPresent, bool expected)
    {
        Assert.Equal(expected, OobePanelPolicy.ShouldReap(tracked, () => inOobe, () => userPresent));
    }

    [Fact]
    public void Reap_WithNothingTracked_NeverAsksAnything()
    {
        var asked = false;
        Assert.False(OobePanelPolicy.ShouldReap(0, () => { asked = true; return false; }, () => { asked = true; return true; }));
        Assert.False(asked);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Nexus\Nexus.exe", true)]
    [InlineData(@"c:\program files\nexus\overlay\NEXUS-OVERLAY.EXE", true)]
    [InlineData(@"C:\Program Files\Nexus\overlay\other.exe", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"C:\Users\x\Nexus.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOwnImage_AcceptsOnlyTheServiceBinaries(string? path, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, OobePanelPolicy.IsOwnImage(path, @"C:\Program Files\Nexus\"));
    }
}
