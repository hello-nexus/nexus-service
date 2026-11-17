using System;
using System.Collections.Generic;
using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class ResumeRetakeTrackerTests
{
    private static bool Write(ResumeRetakeTracker t, List<string> log, string id = "a", bool amd = false, Func<bool>? duty = null) =>
        t.Write(id, amd, () => log.Add("default"), () =>
        {
            log.Add("duty");
            return duty?.Invoke() ?? true;
        });

    [Fact]
    public void ArmedChannel_IsHandedBackToDefaultBeforeTheDutyWrite()
    {
        var t = new ResumeRetakeTracker();
        var log = new List<string>();
        t.Arm(new[] { "a" });

        Assert.True(Write(t, log));

        Assert.Equal(new[] { "default", "duty" }, log);
    }

    [Fact]
    public void ArmedChannel_RetakesOnce_ThenWritesPlain()
    {
        var t = new ResumeRetakeTracker();
        var log = new List<string>();
        t.Arm(new[] { "a" });

        Write(t, log);
        Write(t, log);

        Assert.Equal(new[] { "default", "duty", "duty" }, log);
    }

    [Fact]
    public void UnarmedChannel_NeverGetsTheDefaultWrite()
    {
        var t = new ResumeRetakeTracker();
        var log = new List<string>();

        Write(t, log);

        Assert.Equal(new[] { "duty" }, log);
    }

    [Fact]
    public void AdlxChannel_SkipsTheDefaultWrite_ButIsStillDisarmed()
    {
        var t = new ResumeRetakeTracker();
        var log = new List<string>();
        t.Arm(new[] { "a" });

        Write(t, log, amd: true);

        Assert.Equal(new[] { "duty" }, log);
        Assert.False(t.IsArmed("a"));
    }

    [Fact]
    public void ThrowingDutyWrite_KeepsTheChannelArmed()
    {
        var t = new ResumeRetakeTracker();
        var log = new List<string>();
        t.Arm(new[] { "a" });

        Assert.Throws<InvalidOperationException>(() => Write(t, log, duty: () => throw new InvalidOperationException()));
        Assert.True(t.IsArmed("a"));

        Write(t, log);
        Assert.Equal(new[] { "default", "duty", "default", "duty" }, log);
        Assert.False(t.IsArmed("a"));
    }

    [Fact]
    public void RefusedDutyWrite_KeepsTheChannelArmed()
    {
        var t = new ResumeRetakeTracker();
        var log = new List<string>();
        t.Arm(new[] { "a" });

        Assert.False(Write(t, log, duty: () => false));

        Assert.True(t.IsArmed("a"));
    }

    [Fact]
    public void ThrowingDefaultWrite_StillWritesTheDuty_AndKeepsTheChannelArmed()
    {
        var t = new ResumeRetakeTracker();
        var duty = 0;
        t.Arm(new[] { "a" });

        var ok = t.Write("a", false, () => throw new InvalidOperationException(), () => { duty++; return true; });

        Assert.True(ok);
        Assert.Equal(1, duty);
        Assert.True(t.IsArmed("a"));
    }

    [Fact]
    public void PersistentlyThrowingDefaultWrite_GivesUpAfterTheCap_WhileDutyWritesNeverStop()
    {
        var t = new ResumeRetakeTracker();
        var defaults = 0;
        var duty = 0;
        t.Arm(new[] { "a" });

        for (var i = 0; i < ResumeRetakeTracker.MaxDefaultFailures + 3; i++)
        {
            t.Write("a", false, () => { defaults++; throw new InvalidOperationException(); }, () => { duty++; return true; });
        }

        Assert.Equal(ResumeRetakeTracker.MaxDefaultFailures, defaults);
        Assert.Equal(ResumeRetakeTracker.MaxDefaultFailures + 3, duty);
        Assert.False(t.IsArmed("a"));
    }

    [Fact]
    public void ArmingAgainDuringAWrite_IsNotDroppedByThatWritesDisarm()
    {
        var t = new ResumeRetakeTracker();
        var log = new List<string>();
        t.Arm(new[] { "a" });

        t.Write("a", false, () => log.Add("default"), () =>
        {
            t.Arm(new[] { "a" });
            return true;
        });

        Assert.True(t.IsArmed("a"));
        Write(t, log);
        Assert.Equal(new[] { "default", "default", "duty" }, log);
        Assert.False(t.IsArmed("a"));
    }

    [Fact]
    public void ArmingAgainAfterGivingUp_ResetsTheCap()
    {
        var t = new ResumeRetakeTracker();
        var defaults = 0;
        t.Arm(new[] { "a" });
        for (var i = 0; i < ResumeRetakeTracker.MaxDefaultFailures; i++)
        {
            t.Write("a", false, () => { defaults++; throw new InvalidOperationException(); }, () => true);
        }
        Assert.False(t.IsArmed("a"));

        t.Arm(new[] { "a" });
        for (var i = 0; i < ResumeRetakeTracker.MaxDefaultFailures; i++)
        {
            t.Write("a", false, () => { defaults++; throw new InvalidOperationException(); }, () => true);
        }

        Assert.Equal(2 * ResumeRetakeTracker.MaxDefaultFailures, defaults);
    }

    [Fact]
    public void ArmingAgainWhileTheGivingUpWriteIsInFlight_KeepsTheNewArm()
    {
        var t = new ResumeRetakeTracker();
        t.Arm(new[] { "a" });
        for (var i = 0; i < ResumeRetakeTracker.MaxDefaultFailures - 1; i++)
        {
            t.Write("a", false, () => throw new InvalidOperationException(), () => true);
        }

        t.Write("a", false, () =>
        {
            t.Arm(new[] { "a" });
            throw new InvalidOperationException();
        }, () => true);

        Assert.True(t.IsArmed("a"));
    }

    [Fact]
    public void ForgetAndClear_DropArmedChannels()
    {
        var t = new ResumeRetakeTracker();
        t.Arm(new[] { "a", "b", "c" });

        t.Forget("a");
        Assert.False(t.IsArmed("a"));
        Assert.True(t.IsArmed("b"));

        t.Clear();
        Assert.False(t.IsArmed("b"));
        Assert.False(t.IsArmed("c"));
    }
}
