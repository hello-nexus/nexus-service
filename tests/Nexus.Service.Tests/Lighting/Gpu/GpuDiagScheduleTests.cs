using System.Collections.Generic;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GpuDiagScheduleTests
{
    private static readonly GpuDiagSchedule Default = new();

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Warmup_windows_are_baseline_start(int window)
    {
        Assert.Equal(GpuDiagMode.BaselineStart, Default.ModeFor(window));
    }

    [Fact]
    public void Each_variant_runs_one_block_in_order()
    {
        var order = GpuDiagSchedule.DefaultOrder;
        for (var m = 0; m < order.Length; m++)
        {
            var first = 3 + m * 4;
            for (var w = first; w < first + 4; w++)
            {
                Assert.Equal(order[m], Default.ModeFor(w));
            }
        }
    }

    [Fact]
    public void Order_is_the_requested_variants_then_baseline()
    {
        Assert.Equal(
            new[]
            {
                GpuDiagMode.Unbind, GpuDiagMode.IdleUnbound, GpuDiagMode.Finish, GpuDiagMode.ExtraCtx,
                GpuDiagMode.ExtraDeleted, GpuDiagMode.Swap, GpuDiagMode.Baseline,
            },
            GpuDiagSchedule.DefaultOrder);
    }

    [Fact]
    public void Sequence_ends_in_baseline_and_reports_done()
    {
        var total = Default.TotalWindows;
        Assert.Equal(3 + 4 * GpuDiagSchedule.DefaultOrder.Length, total);
        Assert.False(Default.IsDone(total - 1));
        Assert.Equal(GpuDiagMode.Baseline, Default.ModeFor(total - 1));
        Assert.True(Default.IsDone(total));
        Assert.Equal(GpuDiagMode.Baseline, Default.ModeFor(total));
        Assert.Equal(GpuDiagMode.Baseline, Default.ModeFor(total + 500));
    }

    [Fact]
    public void First_window_and_the_first_of_each_block_settle()
    {
        Assert.True(Default.IsSettleWindow(0));
        Assert.False(Default.IsSettleWindow(1));
        Assert.False(Default.IsSettleWindow(2));
        Assert.True(Default.IsSettleWindow(3));
        Assert.False(Default.IsSettleWindow(4));
        Assert.True(Default.IsSettleWindow(7));
        Assert.False(Default.IsSettleWindow(Default.TotalWindows));
    }

    [Fact]
    public void Custom_order_is_honored()
    {
        var s = new GpuDiagSchedule(10, 10, 20, new[] { GpuDiagMode.Swap, GpuDiagMode.Finish });
        Assert.Equal(GpuDiagMode.BaselineStart, s.ModeFor(0));
        Assert.Equal(GpuDiagMode.Swap, s.ModeFor(1));
        Assert.Equal(GpuDiagMode.Swap, s.ModeFor(2));
        Assert.Equal(GpuDiagMode.Finish, s.ModeFor(3));
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
    public void Summary_skips_settle_windows_and_keeps_frameless_ones()
    {
        var windows = new List<GpuDiagWindow>
        {
            new(GpuDiagMode.Baseline, true, 300, 900, new double[] { 900, 1 }, 10, 5, 900),
            new(GpuDiagMode.Baseline, false, 300, 100, new double[] { 10, 2 }, 10, 1, 90),
            new(GpuDiagMode.Baseline, false, 300, 120, new double[] { 30, 4 }, 10, 1, 110),
            new(GpuDiagMode.Baseline, false, 300, 110, new double[] { 20, 3 }, 10, 2, 100),
            new(GpuDiagMode.IdleUnbound, false, 0, 50, new double[] { 1, 1 }, 10, 0, 0),
        };

        var summary = GpuDiagSummary.Summarize(windows, 2);

        Assert.Equal(2, summary.Count);
        Assert.Equal(GpuDiagMode.Baseline, summary[0].Mode);
        Assert.Equal(3, summary[0].Windows);
        Assert.Equal(110, summary[0].MedianProcPctOfCore);
        Assert.Equal(new double[] { 20, 3 }, summary[0].MedianPhaseCpuMsPerSec);
        Assert.Equal(1, summary[0].MedianIcdThreads);
        Assert.Equal(100, summary[0].MedianIcdCpuMsPerSec);
        Assert.Equal(1, summary[1].Windows);
        Assert.Equal(0, summary[1].MedianProcCpuMsPerFrame);
    }

    [Fact]
    public void Summary_reports_cpu_per_main_frame()
    {
        var windows = new List<GpuDiagWindow>
        {
            new(GpuDiagMode.Swap, false, 300, 60, new double[] { 30, 6 }, 10),
            new(GpuDiagMode.Swap, false, 600, 90, new double[] { 60, 12 }, 10),
            new(GpuDiagMode.Swap, false, 300, 150, new double[] { 90, 3 }, 5),
        };

        var s = GpuDiagSummary.Summarize(windows, 2)[0];

        // 60% of a core over 10 s on 300 frames = 20 ms/frame; 90% over 10 s on 600 = 15; 150% over 5 s on 300 = 25.
        Assert.Equal(20, s.MedianProcCpuMsPerFrame, 6);
        // Phase 0: 30*10/300 = 1, 60*10/600 = 1, 90*5/300 = 1.5.
        Assert.Equal(1, s.MedianPhaseCpuMsPerFrame![0], 6);
    }
}
