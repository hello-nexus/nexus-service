using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmDimmBackoffTests
{
    private long _now = 1_000_000;

    private LhmDimmBackoff NewBackoff() => new(() => _now);

    private static LhmDimmBackoff.Outcome Walk(LhmDimmBackoff b, long stickMs)
    {
        b.BeginWalk();
        b.RecordStick(stickMs);
        return b.EndWalk();
    }

    private LhmDimmBackoff NewPaused()
    {
        var b = NewBackoff();
        Walk(b, 4_000);
        Walk(b, 4_000);
        Assert.Equal(LhmDimmBackoff.Outcome.Paused, Walk(b, 4_000));
        return b;
    }

    [Fact]
    public void Three_consecutive_slow_walks_pause()
    {
        var b = NewBackoff();
        Assert.Equal(LhmDimmBackoff.Outcome.None, Walk(b, 4_000));
        Assert.Equal(LhmDimmBackoff.Outcome.None, Walk(b, 4_000));
        Assert.False(b.ShouldSkip);
        Assert.Equal(LhmDimmBackoff.Outcome.Paused, Walk(b, 4_000));
        Assert.True(b.ShouldSkip);
        Assert.Equal(4_000, b.LastSlowestMs);
    }

    [Fact]
    public void A_single_slow_walk_does_not_pause()
    {
        var b = NewBackoff();
        Assert.Equal(LhmDimmBackoff.Outcome.None, Walk(b, 4_000));
        Assert.False(b.ShouldSkip);
    }

    [Fact]
    public void A_fast_walk_resets_the_slow_count()
    {
        var b = NewBackoff();
        Walk(b, 4_000);
        Walk(b, 4_000);
        Walk(b, 100);
        Walk(b, 4_000);
        Assert.Equal(LhmDimmBackoff.Outcome.None, Walk(b, 4_000));
        Assert.False(b.ShouldSkip);
    }

    [Fact]
    public void The_slow_threshold_is_inclusive()
    {
        var b = NewBackoff();
        Walk(b, LhmDimmBackoff.SlowStickMs);
        Walk(b, LhmDimmBackoff.SlowStickMs);
        Assert.Equal(LhmDimmBackoff.Outcome.Paused, Walk(b, LhmDimmBackoff.SlowStickMs));
    }

    [Fact]
    public void The_slowest_stick_decides_the_walk()
    {
        var b = NewBackoff();
        b.BeginWalk();
        b.RecordStick(50);
        b.RecordStick(3_000);
        b.EndWalk();
        Assert.Equal(3_000, b.LastSlowestMs);
    }

    [Fact]
    public void Pause_holds_until_it_expires_then_probes()
    {
        var b = NewPaused();
        _now += LhmDimmBackoff.PauseMs - 1;
        Assert.True(b.ShouldSkip);
        _now += 1;
        Assert.False(b.ShouldSkip);
    }

    [Fact]
    public void A_walk_that_read_no_stick_changes_nothing()
    {
        var b = NewPaused();
        b.BeginWalk();
        Assert.Equal(LhmDimmBackoff.Outcome.None, b.EndWalk());
        Assert.True(b.ShouldSkip);
    }

    [Fact]
    public void A_fast_probe_recovers()
    {
        var b = NewPaused();
        _now += LhmDimmBackoff.PauseMs;
        Assert.Equal(LhmDimmBackoff.Outcome.Recovered, Walk(b, 30));
        Assert.False(b.ShouldSkip);
        Assert.Equal(LhmDimmBackoff.Outcome.None, Walk(b, 4_000));
        Assert.Equal(LhmDimmBackoff.Outcome.None, Walk(b, 4_000));
        Assert.Equal(LhmDimmBackoff.Outcome.Paused, Walk(b, 4_000));
    }

    [Fact]
    public void A_slow_probe_pauses_again_and_probes_again_after_expiry()
    {
        var b = NewPaused();
        _now += LhmDimmBackoff.PauseMs;
        Assert.Equal(LhmDimmBackoff.Outcome.Paused, Walk(b, 4_000));
        Assert.True(b.ShouldSkip);
        _now += LhmDimmBackoff.PauseMs;
        Assert.False(b.ShouldSkip);
        Assert.Equal(LhmDimmBackoff.Outcome.Recovered, Walk(b, 30));
    }

    [Fact]
    public void A_slow_probe_stick_skips_the_remaining_sticks_of_that_walk()
    {
        var b = NewPaused();
        _now += LhmDimmBackoff.PauseMs;
        b.BeginWalk();
        Assert.False(b.ShouldSkip);
        b.RecordStick(4_000);
        Assert.True(b.ShouldSkip);
        Assert.Equal(LhmDimmBackoff.Outcome.Paused, b.EndWalk());
    }

    [Fact]
    public void Paused_holds_past_expiry_until_a_fast_probe_walk()
    {
        var b = NewBackoff();
        Assert.False(b.Paused);
        b = NewPaused();
        Assert.True(b.Paused);
        _now += LhmDimmBackoff.PauseMs;
        Assert.False(b.ShouldSkip);
        Assert.True(b.Paused);
        Walk(b, 4_000);
        Assert.True(b.Paused);
        _now += LhmDimmBackoff.PauseMs;
        Walk(b, 30);
        Assert.False(b.Paused);
    }
}
