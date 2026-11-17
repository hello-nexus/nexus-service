using System;
using System.Collections.Generic;
using Xunit;
using Nexus.Service.Panel.Streams;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class BacklightLogTests
{
    private readonly List<string> _lines = new();

    private BacklightLog Create() => new(_lines.Add);

    [Fact]
    public void First_level_logs_at_once()
    {
        var log = Create();
        log.Applied("h", 40, 0);
        Assert.Equal(new[] { "[h] backlight 40%" }, _lines);
    }

    [Fact]
    public void A_burst_logs_its_settled_level_after_the_interval()
    {
        var log = Create();
        log.Applied("h", 10, 0);
        log.Applied("h", 20, 100);
        log.Applied("h", 30, 200);
        log.Settled("h", 300);
        Assert.Single(_lines);

        log.Settled("h", BacklightLog.IntervalMs);

        Assert.Equal(new[] { "[h] backlight 10%", "[h] backlight 30%" }, _lines);
        log.Settled("h", BacklightLog.IntervalMs * 5);
        Assert.Equal(2, _lines.Count);
    }

    [Fact]
    public void A_level_after_the_interval_logs_without_a_pending_repeat()
    {
        var log = Create();
        log.Applied("h", 10, 0);
        log.Applied("h", 20, BacklightLog.IntervalMs);
        log.Settled("h", BacklightLog.IntervalMs * 3);
        Assert.Equal(2, _lines.Count);
    }
}
