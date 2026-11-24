using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmSlowItemLogTests
{
    private long _now = 1_000_000;

    private LhmSlowItemLog NewLog() => new(() => _now);

    [Fact]
    public void A_fast_update_logs_nothing()
    {
        Assert.Null(NewLog().Record("/hdd/0", "ST4000DM004", LhmSlowItemLog.SlowItemMs - 1));
    }

    [Fact]
    public void A_slow_update_names_the_item_and_duration()
    {
        var line = NewLog().Record("/hdd/0", "ST4000DM004", 2_500);

        Assert.NotNull(line);
        Assert.Contains("ST4000DM004", line);
        Assert.Contains("/hdd/0", line);
        Assert.Contains("2.5s", line);
    }

    [Fact]
    public void One_item_logs_once_per_interval_and_again_after()
    {
        var log = NewLog();
        Assert.NotNull(log.Record("/hdd/0", "d", 3_000));

        _now += LhmSlowItemLog.LogIntervalMs - 1;
        Assert.Null(log.Record("/hdd/0", "d", 3_000));

        _now += 1;
        Assert.NotNull(log.Record("/hdd/0", "d", 3_000));
    }

    [Fact]
    public void Items_are_rate_limited_independently()
    {
        var log = NewLog();
        Assert.NotNull(log.Record("/hdd/0", "a", 3_000));
        Assert.NotNull(log.Record("/hdd/1", "b", 3_000));
    }

    [Fact]
    public void The_next_line_reports_how_many_were_suppressed()
    {
        var log = NewLog();
        Assert.DoesNotContain("suppressed", log.Record("/hdd/0", "d", 3_000));
        Assert.Null(log.Record("/hdd/0", "d", 3_000));
        Assert.Null(log.Record("/hdd/0", "d", 3_000));

        _now += LhmSlowItemLog.LogIntervalMs;
        var line = log.Record("/hdd/0", "d", 3_000);
        Assert.Contains("(2 similar suppressed)", line);

        _now += LhmSlowItemLog.LogIntervalMs;
        Assert.DoesNotContain("suppressed", log.Record("/hdd/0", "d", 3_000));
    }
}
