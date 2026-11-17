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
    public void ThrowingDefaultWrite_KeepsTheChannelArmedAndSkipsTheDuty()
    {
        var t = new ResumeRetakeTracker();
        var duty = 0;
        t.Arm(new[] { "a" });

        Assert.Throws<InvalidOperationException>(() => t.Write("a", false, () => throw new InvalidOperationException(), () => { duty++; return true; }));

        Assert.Equal(0, duty);
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
