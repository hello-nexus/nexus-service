using System;
using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class WindowSetPassStatsTests
{
    [Fact]
    public void Note_KeepsTheLargestCall()
    {
        var s = new WindowSetPassStats();
        s.Note("title", 0x10, 5);
        s.Note("dwm", 0x20, 9);
        s.Note("cheap", 0x30, 7);
        Assert.Equal(9, s.MaxTicks);
        Assert.Equal("dwm", s.MaxCall);
        Assert.Equal(0x20, s.MaxHwnd);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var s = new WindowSetPassStats { EnumTicks = 4, DwmCount = 2, FallbackCount = 1 };
        s.Note("dwm", 1, 3);
        s.Reset();
        Assert.Equal(0, s.EnumTicks);
        Assert.Equal(0, s.DwmCount);
        Assert.Equal(0, s.MaxTicks);
        Assert.Equal("", s.MaxCall);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    [InlineData(3600, false)]
    public void LogsEveryPass_OnlyInTheFirstTenMinutes(int seconds, bool expected)
    {
        Assert.Equal(expected, WindowSetPassStats.LogsEveryPass(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Format_ReportsPhasesCountsHooksAndTheSlowestCall()
    {
        var s = new WindowSetPassStats
        {
            EnumTicks = 10_000,
            CheapTicks = 1_000,
            TitleTicks = 2_000,
            FallbackTicks = 3_000,
            DwmTicks = 1_000,
            FallbackCount = 4,
            DwmCount = 2,
        };
        s.Note("dwm", 0xAB, 1_500);
        var line = s.Format(1_000_000, trickles: 3, hookDestroy: 5, hookCloaked: 1, hookUncloaked: 0, maxClass: "Chrome_WidgetWin_1");
        Assert.Equal(
            "enum=10.0ms cheap=1.0ms title=2.0ms fallback=4x3.0ms dwm=2x1.0ms rest=3.0ms trickle=3 "
            + "hooks=destroy:5,cloaked:1,uncloaked:0 max=1.5ms dwm hwnd=0xAB class=Chrome_WidgetWin_1",
            line);
    }

    [Fact]
    public void Format_OmitsTheMaxCallWhenNothingWasTimed()
    {
        var line = new WindowSetPassStats().Format(1_000_000, 0, 0, 0, 0, null);
        Assert.DoesNotContain("max=", line);
    }

    [Fact]
    public void FormatHookRate_NamesEachEventType()
    {
        Assert.Equal(
            "[window-cloak] events over 60s destroy=12 cloaked=3 uncloaked=2",
            WindowSetPassStats.FormatHookRate(60.2, 12, 3, 2));
    }

    [Fact]
    public void FallbackTargets_KeepTheFiveSlowestAndNameTheirOwners()
    {
        var s = new WindowSetPassStats();
        for (var i = 1; i <= 8; i++)
        {
            Assert.True(s.WouldKeepFallback(i * 1000) || i <= 3);
            s.AddFallback(0x100 + i, i * 1000, 40 + i, "Cls" + i);
        }
        Assert.False(s.WouldKeepFallback(1000));
        var line = s.FormatFallbackTargets(1_000_000, pid => "proc" + pid);
        Assert.Equal(
            "fallbackTargets=[8.0ms proc48 Cls8 hwnd=0x108; 7.0ms proc47 Cls7 hwnd=0x107; 6.0ms proc46 Cls6 hwnd=0x106; "
            + "5.0ms proc45 Cls5 hwnd=0x105; 4.0ms proc44 Cls4 hwnd=0x104]",
            line);
    }

    [Fact]
    public void FallbackTargets_EmptyWhenNoneRan_AndResetClears()
    {
        var s = new WindowSetPassStats();
        Assert.Equal("", s.FormatFallbackTargets(1_000_000, _ => "x"));
        s.AddFallback(1, 5, 2, "C");
        s.Reset();
        Assert.Equal("", s.FormatFallbackTargets(1_000_000, _ => "x"));
    }
}
