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

public class Slv3ReappearAbTests
{
    [Theory]
    [InlineData("reappear=off", true, false)]
    [InlineData("45000 b reappear=off", true, false)]
    [InlineData("reappear=off 1000 abc", true, false)]
    [InlineData("45000 b", true, true)]
    [InlineData("off", false, true)]
    [InlineData("off reappear=off", false, false)]
    public void Config_reappear_token(string text, bool enabled, bool reappear)
    {
        var c = Slv3StallConfig.Parse(text);
        Assert.Equal(enabled, c.Enabled);
        Assert.Equal(reappear, c.ReappearAb);
    }

    [Fact]
    public void Reappear_config_token_keeps_the_other_settings()
    {
        var c = Slv3StallConfig.Parse("45000 b reappear=off");
        Assert.Equal(45000L, c.ThresholdMs);
        Assert.Equal("b", c.Actions);
    }

    [Fact]
    public void Modes_alternate_and_only_L_holds_the_window_for_30_s()
    {
        var ab = new Slv3ReappearAb();
        Assert.Equal('W', ab.OnReappear("AA", 1000));
        Assert.False(ab.IsHeld("AA", 1001));
        Assert.Equal('L', ab.OnReappear("AA", 2000));
        Assert.True(ab.IsHeld("AA", 2000 + Slv3ReappearAb.HoldMs - 1));
        Assert.False(ab.IsHeld("AA", 2000 + Slv3ReappearAb.HoldMs));
        Assert.False(ab.IsHeld("BB", 2001));
        Assert.Equal('W', ab.OnReappear("BB", 3000));
    }

    [Fact]
    public void A_drop_ends_the_hold_and_a_W_event_clears_a_stale_one()
    {
        var ab = new Slv3ReappearAb();
        ab.OnReappear("AA", 0);
        ab.OnReappear("AA", 1000);
        Assert.True(ab.IsHeld("AA", 2000));
        ab.OnDropped("AA");
        Assert.False(ab.IsHeld("AA", 2000));
        ab.OnReappear("AA", 3000);
        ab.OnReappear("AA", 4000);
        ab.OnReappear("AA", 5000);
        Assert.False(ab.IsHeld("AA", 5001));
    }

    [Fact]
    public void Disabled_never_holds_or_records()
    {
        var ab = new Slv3ReappearAb { Enabled = false };
        Assert.Equal('W', ab.OnReappear("AA", 0));
        Assert.Equal('W', ab.OnReappear("AA", 1000));
        Assert.False(ab.IsHeld("AA", 1001));
        Assert.Empty(ab.Expire(100_000));
    }

    [Fact]
    public void A_stall_within_15_s_is_attributed_with_its_age_and_later_events_expire_as_no()
    {
        var ab = new Slv3ReappearAb();
        ab.OnReappear("AA", 1000);
        ab.OnReappear("BB", 2000);
        ab.OnReappear("CC", 50_000);
        var hit = ab.OnStall(4500);
        Assert.Equal(2, hit.Count);
        Assert.Equal("reappear chain=AA mode=W stallWithin15s=yes stallAge=3500ms", hit[0].Describe());
        Assert.Equal("reappear chain=BB mode=L stallWithin15s=yes stallAge=2500ms", hit[1].Describe());
        Assert.Empty(ab.Expire(60_000));
        var none = Assert.Single(ab.Expire(70_000));
        Assert.Equal("reappear chain=CC mode=W stallWithin15s=no", none.Describe());
    }

    [Fact]
    public void A_stall_more_than_15_s_after_is_not_attributed()
    {
        var ab = new Slv3ReappearAb();
        ab.OnReappear("AA", 0);
        Assert.Empty(ab.OnStall(20_000));
        Assert.Single(ab.Expire(20_000));
    }
}
