using System.Collections.Generic;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GpuDiagScheduleTests
{
    private static readonly GpuDiagSchedule Default = new();

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Warmup_windows_are_baseline(int window)
    {
        Assert.Equal(GpuDiagMode.Baseline, Default.ModeFor(window));
        Assert.True(Default.IsWarmup(window));
    }

    [Fact]
    public void Each_mode_runs_one_block_and_the_list_repeats()
    {
        var order = GpuDiagSchedule.DefaultOrder;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            for (var m = 0; m < order.Length; m++)
            {
                var first = 12 + (cycle * order.Length + m) * 9;
                for (var w = first; w < first + 9; w++)
                {
                    Assert.Equal(order[m], Default.ModeFor(w));
                }
            }
        }
    }

    [Fact]
    public void Sequence_ends_in_baseline_and_reports_done()
    {
        var total = Default.TotalWindows;
        Assert.Equal(12 + 9 * GpuDiagSchedule.DefaultOrder.Length * 2, total);
        Assert.False(Default.IsDone(total - 1));
        Assert.True(Default.IsDone(total));
        Assert.Equal(GpuDiagMode.Baseline, Default.ModeFor(total));
        Assert.Equal(GpuDiagMode.Baseline, Default.ModeFor(total + 500));
    }

    [Fact]
    public void Only_the_first_window_of_a_block_settles()
    {
        Assert.True(Default.IsSettleWindow(12));
        Assert.False(Default.IsSettleWindow(13));
        Assert.True(Default.IsSettleWindow(21));
        Assert.False(Default.IsSettleWindow(Default.TotalWindows));
    }

    [Fact]
    public void Custom_order_is_honored()
    {
        var s = new GpuDiagSchedule(10, 10, 20, 1, new[] { GpuDiagMode.NoRender, GpuDiagMode.SleepWait });
        Assert.Equal(GpuDiagMode.Baseline, s.ModeFor(0));
        Assert.Equal(GpuDiagMode.NoRender, s.ModeFor(1));
        Assert.Equal(GpuDiagMode.NoRender, s.ModeFor(2));
        Assert.Equal(GpuDiagMode.SleepWait, s.ModeFor(3));
        Assert.True(s.IsDone(5));
    }

    [Theory]
    [InlineData(new double[] { }, 0.0)]
    [InlineData(new double[] { 5 }, 5.0)]
    [InlineData(new double[] { 9, 1, 5 }, 5.0)]
    [InlineData(new double[] { 4, 1, 2, 10 }, 3.0)]
    public void Median_handles_odd_even_and_empty(double[] values, double expected)
    {
        Assert.Equal(expected, GpuDiagSummary.Median(values));
    }

    [Fact]
    public void Summary_skips_settle_unsupported_and_frameless_windows()
    {
        var windows = new List<GpuDiagWindow>
        {
            new(GpuDiagMode.Baseline, true, 300, 900, new double[] { 900, 1 }),
            new(GpuDiagMode.Baseline, false, 300, 100, new double[] { 10, 2 }),
            new(GpuDiagMode.Baseline, false, 300, 120, new double[] { 30, 4 }),
            new(GpuDiagMode.Baseline, false, 300, 110, new double[] { 20, 3 }),
            new(GpuDiagMode.Baseline, false, 0, 999, new double[] { 999, 999 }),
            new(GpuDiagMode.AsyncReadback, false, 0, 50, new double[] { 1, 1 }),
        };

        var summary = GpuDiagSummary.Summarize(windows, 2);

        Assert.Equal(2, summary.Count);
        Assert.Equal(GpuDiagMode.Baseline, summary[0].Mode);
        Assert.Equal(3, summary[0].Windows);
        Assert.Equal(110, summary[0].MedianProcPctOfCore);
        Assert.Equal(new double[] { 20, 3 }, summary[0].MedianPhaseCpuMsPerSec);
        Assert.Equal(0, summary[1].Windows);
    }
}
