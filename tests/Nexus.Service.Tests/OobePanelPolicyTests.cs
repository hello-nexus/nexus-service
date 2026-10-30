using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests;

public sealed class OobePanelPolicyTests
{
    [Theory]
    [InlineData("alice", true)]
    [InlineData("alice", false)]
    [InlineData("defaultuserX", true)]
    [InlineData("defaultuser", true)]
    [InlineData("defaultuser0x", true)]
    public void RealUser_IsAlwaysTheConsoleUserPath_AndNeverAsksOobe(string user, bool sessionExists)
    {
        var asked = false;
        var plan = OobePanelPolicy.Decide(user, sessionExists, () => { asked = true; return true; }, () => { asked = true; return true; });
        Assert.Equal(ConsoleLaunchPlan.ConsoleUser, plan);
        Assert.False(asked);
    }

    [Theory]
    [InlineData("defaultuser0")]
    [InlineData("DefaultUser1")]
    [InlineData("defaultuser12")]
    public void TempAccount_InOobe_LaunchesDirectly(string user)
    {
        Assert.Equal(ConsoleLaunchPlan.OobeTempUser, OobePanelPolicy.Decide(user, true, () => true, () => true));
    }

    [Fact]
    public void TempAccount_OobeComplete_IsTheNormalPath()
    {
        Assert.Equal(ConsoleLaunchPlan.ConsoleUser, OobePanelPolicy.Decide("defaultuser0", true, () => false, () => true));
    }

    [Fact]
    public void TempAccountName_OnANonLocalAccount_IsTheNormalPath()
    {
        Assert.Equal(ConsoleLaunchPlan.ConsoleUser, OobePanelPolicy.Decide("defaultuser0", true, () => true, () => false));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("alice", true)]
    [InlineData("defaultuser0", false)]
    [InlineData("DEFAULTUSER3", false)]
    public void IsRealUser_ExcludesNoUserAndTheSetupAccount(string user, bool expected)
    {
        Assert.Equal(expected, OobePanelPolicy.IsRealUser(user));
    }

    [Theory]
    [InlineData("defaultuser0", false)]
    [InlineData("", false)]
    [InlineData("alice", true)]
    public void Reap_KeepsRunningWhileOnlyTheSetupAccountIsOnTheConsole(string consoleUser, bool expectedReap)
    {
        Assert.Equal(expectedReap, OobePanelPolicy.ShouldReap(2, () => true, () => OobePanelPolicy.IsRealUser(consoleUser)));
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
