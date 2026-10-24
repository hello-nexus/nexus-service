using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GlWorkQueueTests
{
    private static void Drain(GlWorkQueue q)
    {
        while (q.TryTake(out var job))
        {
            job.Work();
        }
    }

    [Fact]
    public void Live_jobs_run_before_queued_background_jobs()
    {
        var q = new GlWorkQueue();
        var ran = new List<string>();
        q.Add(() => ran.Add("bg1"), background: true);
        q.Add(() => ran.Add("bg2"), background: true);
        q.Add(() => ran.Add("live1"), background: false);
        q.Add(() => ran.Add("live2"), background: false);

        Drain(q);

        Assert.Equal(new[] { "live1", "live2", "bg1", "bg2" }, ran);
    }

    [Fact]
    public void Each_lane_keeps_arrival_order()
    {
        var q = new GlWorkQueue();
        var ran = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var n = i;
            q.Add(() => ran.Add("b" + n), background: true);
        }

        Drain(q);

        Assert.Equal(new[] { "b0", "b1", "b2", "b3" }, ran);
    }

    [Fact]
    public void A_live_job_waits_for_at_most_the_background_job_already_taken()
    {
        var q = new GlWorkQueue();
        var ran = new List<string>();
        q.Add(() => ran.Add("bg1"), background: true);
        q.Add(() => ran.Add("bg2"), background: true);

        var running = q.Take();
        q.Add(() => ran.Add("live"), background: false);
        running.Work();
        Drain(q);

        Assert.Equal(new[] { "bg1", "live", "bg2" }, ran);
    }

    [Fact]
    public void A_background_job_runs_after_a_streak_of_live_jobs()
    {
        var q = new GlWorkQueue();
        var ran = new List<string>();
        q.Add(() => ran.Add("bg"), background: true);
        for (var i = 0; i < GlWorkQueue.MaxLiveStreak + 2; i++)
        {
            q.Add(() => ran.Add("live"), background: false);
        }

        Drain(q);

        Assert.Equal("bg", ran[GlWorkQueue.MaxLiveStreak]);
        Assert.Equal(GlWorkQueue.MaxLiveStreak + 3, ran.Count);
    }

    [Fact]
    public void A_long_live_run_does_not_delay_a_later_background_job_beyond_the_streak()
    {
        var q = new GlWorkQueue();
        for (var i = 0; i < 1000; i++)
        {
            q.Add(() => { }, background: false);
            _ = q.Take();
        }
        var ran = new List<string>();
        q.Add(() => ran.Add("bg"), background: true);
        q.Add(() => ran.Add("live"), background: false);

        Drain(q);

        Assert.Equal(new[] { "bg", "live" }, ran);
    }

    [Fact]
    public void Completed_queue_refuses_adds_drains_then_throws()
    {
        var q = new GlWorkQueue();
        q.Add(() => { }, background: true);
        q.CompleteAdding();

        Assert.Throws<InvalidOperationException>(() => q.Add(() => { }, background: false));
        _ = q.Take();
        Assert.Throws<InvalidOperationException>(() => q.Take());
    }

    [Fact]
    public void Stats_report_only_windows_that_saw_background_work()
    {
        var stats = new GlQueueStats(TimeSpan.FromSeconds(1));
        var freq = System.Diagnostics.Stopwatch.Frequency;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var live = new GlJob(() => { }, false, t0);
        var bg = new GlJob(() => { }, true, t0);

        Assert.Null(stats.Record(live, t0 + freq / 100, t0 + freq / 50));
        Assert.Null(stats.Record(bg, t0 + freq / 100, t0 + freq / 50));
        var line = stats.Record(live, t0 + freq * 2, t0 + freq * 2 + 1);

        Assert.NotNull(line);
        Assert.Contains("background jobs 1", line);
        Assert.Null(stats.Record(live, t0 + freq * 4, t0 + freq * 4 + 1));
    }

    [Fact]
    public void A_deferred_job_is_not_taken_before_its_delay()
    {
        var q = new GlWorkQueue();
        var ran = new List<string>();
        // The delay dwarfs any test-host stall, so "not due yet" holds without reading a clock.
        q.AddDeferred(() => ran.Add("later"), TimeSpan.FromHours(1));
        q.Add(() => ran.Add("live"), background: false);

        Drain(q);

        Assert.Equal(new[] { "live" }, ran);
    }

    [Fact]
    public void A_deferred_job_whose_delay_has_passed_runs_after_live_jobs()
    {
        var q = new GlWorkQueue();
        var ran = new List<string>();
        q.AddDeferred(() => ran.Add("due"), TimeSpan.Zero);
        q.Add(() => ran.Add("live"), background: false);

        Drain(q);

        Assert.Equal(new[] { "live", "due" }, ran);
    }

    [Fact]
    public void Take_blocks_until_a_lone_deferred_job_is_due_then_returns_it()
    {
        var q = new GlWorkQueue();
        var ran = new List<string>();
        q.AddDeferred(() => ran.Add("later"), TimeSpan.FromMilliseconds(50));

        // The timeout only guards against a hang; no assertion depends on how long Take waited.
        var task = System.Threading.Tasks.Task.Run(() => q.Take());
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)));
        task.Result.Work();

        Assert.Equal(new[] { "later" }, ran);
    }

    [Fact]
    public void Completing_the_queue_releases_deferred_jobs_for_the_drain()
    {
        var q = new GlWorkQueue();
        var ran = new List<string>();
        q.AddDeferred(() => ran.Add("deferred"), TimeSpan.FromSeconds(30));

        q.CompleteAdding();
        Drain(q);

        Assert.Equal(new[] { "deferred" }, ran);
        Assert.Throws<InvalidOperationException>(() => q.AddDeferred(() => { }, TimeSpan.Zero));
    }
}
