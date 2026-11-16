using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmSlowWalkReportTests
{
    private long _now = 1_000_000;

    private LhmSlowWalkReport NewReport() => new(() => _now);

    [Fact]
    public void A_fast_walk_logs_nothing()
    {
        var report = NewReport();
        report.Begin();
        report.Record("/memory/dimm/0", 400);

        Assert.Null(report.Finish(900));
    }

    [Fact]
    public void A_slow_walk_names_the_slowest_items_in_order()
    {
        var report = NewReport();
        report.Begin();
        report.Record("/cpu/0", 150);
        report.Record("/memory/dimm/1", 30_000);
        report.Record("/gpu-nvidia/0", 200);
        report.Record("/memory/dimm/0", 9_000);
        report.Record("/fast", 5);

        var line = report.Finish(40_000);

        Assert.Equal("[lhm] slow walk 40.0s; slowest items: /memory/dimm/1 30.0s, /memory/dimm/0 9.0s, /gpu-nvidia/0 0.2s", line);
    }

    [Fact]
    public void A_slow_walk_with_no_single_slow_item_still_reports()
    {
        var report = NewReport();
        report.Begin();

        Assert.Equal("[lhm] slow walk 6.0s; no single item over the item threshold", report.Finish(6_000));
    }

    [Fact]
    public void Slow_walks_log_at_most_once_per_interval()
    {
        var report = NewReport();
        report.Begin();
        Assert.NotNull(report.Finish(8_000));

        _now += LhmSlowWalkReport.LogIntervalMs - 1;
        report.Begin();
        Assert.Null(report.Finish(8_000));

        _now += 1;
        report.Begin();
        Assert.NotNull(report.Finish(8_000));
    }

    [Fact]
    public void Items_from_a_previous_walk_do_not_leak_into_the_next()
    {
        var report = NewReport();
        report.Begin();
        report.Record("/memory/dimm/0", 4_000);
        report.Finish(4_500);

        _now += LhmSlowWalkReport.LogIntervalMs;
        report.Begin();
        report.Record("/cpu/0", 6_000);

        Assert.Equal("[lhm] slow walk 6.5s; slowest items: /cpu/0 6.0s", report.Finish(6_500));
    }
}
