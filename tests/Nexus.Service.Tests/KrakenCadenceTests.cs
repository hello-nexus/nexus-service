using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>The slot clock and the gap statistics of the 2023 Kraken's cadenced LCD wire, on a fake clock in microseconds.</summary>
public class KrakenCadenceTests
{
    private sealed class FakeClock
    {
        public long Now = 5_000_000;
        public bool Stopped;
        // Extra delay a wait lands after its deadline, in microseconds.
        public Func<int, long> Lateness = _ => 0;
        public int Waits;

        public KrakenCadence Cadence() => new(() => Now, 1_000_000, deadline =>
        {
            if (Stopped)
            {
                return false;
            }
            var late = Lateness(Waits++);
            Now = Math.Max(Now, deadline + late);
            return true;
        });
    }

    private static List<long> Starts(FakeClock clock, KrakenCadence cadence, int slots, Func<int, long>? workUs = null)
    {
        var starts = new List<long>();
        for (int i = 0; i < slots; i++)
        {
            Assert.True(cadence.WaitNextSlot(out var start));
            starts.Add(start);
            clock.Now += workUs?.Invoke(i) ?? 0;
        }
        return starts;
    }

    [Fact]
    public void Starts_sit_on_a_fixed_grid_when_the_work_is_short()
    {
        var clock = new FakeClock();
        var starts = Starts(clock, clock.Cadence(), 20, _ => 9_000);

        for (int i = 1; i < starts.Count; i++)
        {
            Assert.Equal(KrakenCadence.PeriodUs, starts[i] - starts[i - 1]);
        }
    }

    [Fact]
    public void A_wake_a_little_late_does_not_shift_the_grid_so_error_does_not_accumulate()
    {
        var clock = new FakeClock { Lateness = i => i % 2 == 0 ? 600 : 0 };
        var starts = Starts(clock, clock.Cadence(), 41);

        for (int i = 0; i < starts.Count; i++)
        {
            Assert.InRange(starts[i] - starts[0] - (i * KrakenCadence.PeriodUs), -600, 600);
        }
    }

    [Fact]
    public void No_start_is_sooner_than_the_minimum_gap_after_the_previous_one()
    {
        var clock = new FakeClock { Lateness = i => (i * 7_919L) % 1_000 };
        var starts = Starts(clock, clock.Cadence(), 200);

        for (int i = 1; i < starts.Count; i++)
        {
            Assert.True(starts[i] - starts[i - 1] >= KrakenCadence.MinGapUs, $"slot {i}: {starts[i] - starts[i - 1]} us");
        }
    }

    [Fact]
    public void A_real_overrun_re_anchors_instead_of_bursting_to_catch_up()
    {
        var clock = new FakeClock();
        var cadence = clock.Cadence();
        var starts = Starts(clock, cadence, 3);
        // One send takes five periods.
        clock.Now += 5 * KrakenCadence.PeriodUs;
        starts.AddRange(Starts(clock, cadence, 4));

        for (int i = 4; i < starts.Count; i++)
        {
            Assert.Equal(KrakenCadence.PeriodUs, starts[i] - starts[i - 1]);
        }
        Assert.True(starts[3] - starts[2] >= 5 * KrakenCadence.PeriodUs);
    }

    [Fact]
    public void A_stopped_wait_ends_the_slots()
    {
        var clock = new FakeClock();
        var cadence = clock.Cadence();
        Assert.True(cadence.WaitNextSlot(out _));

        clock.Stopped = true;

        Assert.False(cadence.WaitNextSlot(out _));
    }

    [Fact]
    public void The_summary_reports_gap_percentiles_and_new_versus_repeated_counts()
    {
        var stats = new KrakenCadenceStats();
        long t = 1_000;
        stats.RecordStart(t);
        // Gaps of 37 ms for 90 frames, 38 ms for 9, one stall of 90 ms; the tick is one microsecond.
        for (int i = 0; i < 100; i++)
        {
            t += i == 99 ? 90_000 : i >= 90 ? 38_000 : 37_000;
            stats.RecordStart(t);
        }
        for (int i = 0; i < 70; i++)
        {
            stats.CountSend(fresh: true);
        }
        for (int i = 0; i < 31; i++)
        {
            stats.CountSend(fresh: false);
        }

        var s = stats.Take(1_000_000)!.Value;

        Assert.Equal(100, s.Samples);
        Assert.Equal(37.0, s.MinMs, 3);
        Assert.Equal(37.0, s.P50Ms, 3);
        Assert.Equal(38.0, s.P95Ms, 3);
        Assert.Equal(90.0, s.MaxMs, 3);
        Assert.Equal(70, s.Fresh);
        Assert.Equal(31, s.Repeated);
    }

    [Fact]
    public void A_summary_starts_a_new_window_and_an_empty_one_is_null()
    {
        var stats = new KrakenCadenceStats();
        Assert.Null(stats.Take(1_000_000));
        stats.RecordStart(10);
        stats.RecordStart(20);
        stats.CountSend(fresh: true);
        Assert.NotNull(stats.Take(1_000_000));

        Assert.Null(stats.Take(1_000_000));
    }

    [Fact]
    public void A_stream_boundary_is_not_counted_as_a_gap()
    {
        var stats = new KrakenCadenceStats();
        stats.RecordStart(1_000);
        stats.BreakChain();
        stats.RecordStart(900_000_000);
        stats.CountSend(fresh: true);

        Assert.Equal(0, stats.Take(1_000_000)!.Value.Samples);
    }
}
