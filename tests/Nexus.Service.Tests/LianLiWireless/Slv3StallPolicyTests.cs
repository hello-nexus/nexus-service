using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3StallPolicyTests
{
    private static char Poll(Slv3StallPolicy p, long now, bool heard = true) => p.OnNoData(now, heard, out _, out _);

    [Theory]
    [InlineData(null, true, 30000L, "ab")]
    [InlineData("", true, 30000L, "ab")]
    [InlineData("off", false, 30000L, "")]
    [InlineData("OFF", false, 30000L, "")]
    [InlineData("45000 b", true, 45000L, "b")]
    [InlineData("  1000   abc\r\n", true, 1000L, "abc")]
    [InlineData("1000 xyz", false, 1000L, "")]
    [InlineData("1000", false, 30000L, "")]
    [InlineData("junk text", true, 30000L, "ab")]
    public void Config_parsing(string? text, bool enabled, long threshold, string actions)
    {
        var c = Slv3StallConfig.Parse(text);
        Assert.Equal(enabled, c.Enabled);
        Assert.Equal(threshold, c.ThresholdMs);
        Assert.Equal(actions, c.Actions);
    }

    [Fact]
    public void Nothing_happens_before_the_threshold()
    {
        var p = new Slv3StallPolicy();
        Assert.Equal(Slv3StallPolicy.NoAction, Poll(p, 1000));
        Assert.Equal(Slv3StallPolicy.NoAction, Poll(p, 30999));
        Assert.Equal('a', Poll(p, 31000));
    }

    [Fact]
    public void At_most_one_action_per_stall_and_actions_alternate_across_stalls()
    {
        var p = new Slv3StallPolicy { Config = new Slv3StallConfig(true, 100, "ab") };
        Poll(p, 1000);
        Assert.Equal('a', Poll(p, 1100));
        Assert.Equal(Slv3StallPolicy.NoAction, Poll(p, 6000));
        Assert.NotNull(p.OnGood(20_000));
        Poll(p, 30_000);
        Assert.Equal('b', Poll(p, 30_100));
        p.OnGood(40_000);
        Poll(p, 400_000);
        Assert.Equal('a', Poll(p, 400_100));
    }

    [Fact]
    public void A_stall_that_ends_below_the_threshold_does_not_consume_a_rotation_slot()
    {
        var p = new Slv3StallPolicy { Config = new Slv3StallConfig(true, 1000, "ab") };
        Poll(p, 0);
        p.OnGood(500);
        Poll(p, 10_000);
        Assert.Equal('a', Poll(p, 11_000));
    }

    [Fact]
    public void Reset_is_rate_limited_and_skipped_without_losing_its_slot()
    {
        var p = new Slv3StallPolicy { Config = new Slv3StallConfig(true, 0, "a") };
        Assert.Equal('a', Poll(p, 1000));
        p.OnGood(2000);
        Poll(p, 3000);
        var action = p.OnNoData(3001, true, out _, out var suppressed);
        Assert.Equal(Slv3StallPolicy.NoAction, action);
        Assert.False(suppressed);
        p.OnGood(4000);
        p.OnNoData(5000, true, out _, out suppressed);
        Assert.True(suppressed);
        p.OnGood(6000);
        Assert.Equal('a', Poll(p, 1000 + Slv3StallPolicy.MinResetGapMs + 10));
    }

    [Fact]
    public void No_action_while_no_chain_was_ever_heard_or_when_disabled()
    {
        var p = new Slv3StallPolicy { Config = new Slv3StallConfig(true, 0, "ab") };
        Assert.Equal(Slv3StallPolicy.NoAction, Poll(p, 100_000, heard: false));
        Assert.Equal('a', Poll(p, 100_001, heard: true));
        var off = new Slv3StallPolicy { Config = Slv3StallConfig.Parse("off") };
        Assert.Equal(Slv3StallPolicy.NoAction, Poll(off, 1_000_000));
    }

    [Fact]
    public void Result_reports_time_from_action_to_first_good_reply_or_none()
    {
        var p = new Slv3StallPolicy { Config = new Slv3StallConfig(true, 100, "b") };
        Poll(p, 0);
        Poll(p, 100);
        var end = p.OnGood(700);
        Assert.Equal(new Slv3StallPolicy.StallEnd(1, 700, "b", "600ms"), end);
        Assert.Null(p.OnGood(800));

        Poll(p, 1000);
        Poll(p, 1100);
        Assert.Equal(new Slv3StallPolicy.StallEnd(2, 500, "b", "none"), p.OnLinkDown(1500));

        Poll(p, 2000);
        Assert.Equal(new Slv3StallPolicy.StallEnd(3, 50, "none", "none"), p.OnGood(2050));
    }
}
