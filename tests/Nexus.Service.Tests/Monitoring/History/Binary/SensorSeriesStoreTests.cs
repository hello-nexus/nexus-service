using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>Tracked-sensor series through the real BinaryMetricsHistoryStore (ring files on a temp dir): recording, decimation tiers, retention and deletion.</summary>
public class SensorSeriesStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-sensorseries-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MetricSample Sample(long ts, params (string Id, double Value)[] readings) =>
        new(ts, 10, 20, 0, 0, 40, Array.Empty<GpuReading>(), Array.Empty<FanReading>())
        {
            SensorReadings = readings.Select(r => new SensorSeriesReading(r.Id, r.Value)).ToList(),
        };

    [Fact]
    public void Raw_tier_returns_one_slot_per_second_and_the_minute_tier_folds()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var t0 = 1_800_000_000L / 60 * 60;
        store.Append(Enumerable.Range(0, 120).Select(i => Sample(t0 + i, ("rule-a", 1000 + i))).ToList(), null);

        var raw = store.QuerySensorSeriesDecimated(t0, t0 + 119, 1);
        var rolled = store.QuerySensorSeriesDecimated(t0, t0 + 119, 60);

        Assert.Equal(120, raw.Count);
        Assert.Equal(1000, raw[0].Avg);
        Assert.Equal(2, rolled.Count);
        Assert.Equal(1029.5, rolled[0].Avg, 3);
        Assert.Equal(1059, rolled[0].Max, 3);
    }

    [Fact]
    public void Series_are_keyed_by_rule_id_and_keep_native_magnitudes()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var t0 = 1_800_000_000L;
        store.Append(new[] { Sample(t0, ("a", 4321.5), ("b", 0.012)) }, null);

        var slots = store.QuerySensorSeriesDecimated(t0, t0, 1);

        Assert.Equal(4321.5, slots.Single(s => s.SeriesId == "a").Avg);
        Assert.Equal(0.012, slots.Single(s => s.SeriesId == "b").Avg, 6);
    }

    [Fact]
    public void Non_finite_readings_are_not_recorded()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var t0 = 1_800_000_000L;
        store.Append(new[] { Sample(t0, ("a", double.NaN)), Sample(t0 + 1, ("a", double.PositiveInfinity)) }, null);

        Assert.Empty(store.QuerySensorSeriesDecimated(t0, t0 + 1, 1));
    }

    [Fact]
    public void History_survives_a_reopen()
    {
        var t0 = 1_800_000_000L;
        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { Sample(t0, ("a", 7)) }, null);
        }

        using var reopened = new BinaryMetricsHistoryStore(_dir);

        Assert.Equal(7, reopened.QuerySensorSeriesDecimated(t0, t0, 1).Single().Avg);
    }

    [Fact]
    public void Delete_removes_the_history_and_drops_a_flush_that_was_already_in_flight()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var t0 = 1_800_000_000L;
        store.Append(new[] { Sample(t0, ("a", 1), ("b", 2)) }, null);

        store.DeleteSensorSeries("a", t0 + 5);
        store.Append(new[] { Sample(t0 + 3, ("a", 9)), Sample(t0 + 10, ("a", 3), ("b", 4)) }, null);

        var slots = store.QuerySensorSeriesDecimated(t0, t0 + 20, 1);
        Assert.Equal(new[] { t0 + 10 }, slots.Where(s => s.SeriesId == "a").Select(s => s.Slot).ToArray());
        Assert.Equal(2, slots.Count(s => s.SeriesId == "b"));
    }

    [Fact]
    public void Prune_keeps_the_minute_tier_for_ninety_days_and_the_raw_tier_for_the_shorter_floor()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var now = 1_800_000_000L;
        // Offsets keep the three seconds on different raw-ring slots (the raw tier wraps every two days).
        var old = now - 100L * 86_400 - 3600;
        var mid = now - 60L * 86_400 - 7200;
        store.Append(new[] { Sample(old, ("a", 1)), Sample(mid, ("a", 2)), Sample(now, ("a", 3)) }, null);

        // The sampler's hourly prune passes now minus the raw retention (7 days).
        store.Append(Array.Empty<MetricSample>(), now - MetricsHistory.RetentionDays * 86_400L);

        Assert.Empty(store.QuerySensorSeriesDecimated(old - 60, old + 60, 60));
        Assert.Equal(2, store.QuerySensorSeriesDecimated(mid - 60, mid + 60, 60).Single().Avg);
        Assert.Equal(3, store.QuerySensorSeriesDecimated(now - 60, now + 60, 60).Single().Avg);
        Assert.Empty(store.QuerySensorSeriesDecimated(mid, mid, 1));
    }

    [Fact]
    public void Unsafe_ids_are_not_recorded()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var t0 = 1_800_000_000L;
        store.Append(new[] { Sample(t0, ("../escape", 1)) }, null);

        Assert.Empty(store.QuerySensorSeriesDecimated(t0, t0, 1));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.ring")));
    }

    [Fact]
    public void An_existing_database_without_the_new_directory_just_gains_it()
    {
        using (var first = new BinaryMetricsHistoryStore(_dir)) { }
        Directory.Delete(Path.Combine(_dir, "sensorseries"), recursive: true);

        using var store = new BinaryMetricsHistoryStore(_dir);
        var t0 = 1_800_000_000L;
        store.Append(new[] { Sample(t0, ("a", 1)) }, null);

        Assert.Single(store.QuerySensorSeriesDecimated(t0, t0, 1));
    }

    [Fact]
    public void The_in_memory_store_follows_the_same_contract()
    {
        using var store = new InMemoryMetricsHistoryStore();
        var t0 = 1_800_000_000L;
        store.Append(new[] { Sample(t0 - 5, ("a", 1), ("b", 2)) }, null);
        store.DeleteSensorSeries("a", t0);
        store.Append(new[] { Sample(t0, ("a", 5)), Sample(t0 + 1, ("a", 6)) }, null);

        var slots = store.QuerySensorSeriesDecimated(t0 - 10, t0 + 5, 1);

        Assert.Equal(new[] { t0 + 1 }, slots.Where(s => s.SeriesId == "a").Select(s => s.Slot).ToArray());
        Assert.Single(slots, s => s.SeriesId == "b");
    }

    [Fact]
    public async System.Threading.Tasks.Task Queries_racing_delete_and_recreate_never_touch_an_unmapped_ring()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var t0 = 1_800_000_000L;
        store.Append(new[] { Sample(t0, ("a", 1)) }, null);
        using var stop = new System.Threading.CancellationTokenSource();

        var readers = Enumerable.Range(0, 3).Select(i => System.Threading.Tasks.Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                store.QuerySensorSeriesDecimated(t0, t0 + 1000, i == 0 ? 60 : 1);
            }
        })).ToArray();

        for (var i = 1; i <= 150; i++)
        {
            store.DeleteSensorSeries("a", t0 + i * 2);
            store.Append(new[] { Sample(t0 + i * 2 + 1, ("a", i)) }, null);
        }
        stop.Cancel();
        await System.Threading.Tasks.Task.WhenAll(readers);

        Assert.Single(store.QuerySensorSeriesDecimated(t0 + 300, t0 + 400, 1));
    }

    [Fact]
    public void Prune_drops_series_files_that_are_not_live_rules_so_they_free_the_series_cap()
    {
        var t0 = 1_800_000_000L;
        var dir = Path.Combine(_dir, "sensorseries");
        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { Sample(t0, ("keep", 1), ("dead", 2)) }, null);
        }
        File.WriteAllBytes(Path.Combine(dir, "stray.min"), new byte[8]);

        using var reopened = new BinaryMetricsHistoryStore(_dir);
        reopened.PruneSensorSeries(new System.Collections.Generic.HashSet<string> { "keep" });

        Assert.Equal(new[] { "keep" }, reopened.QuerySensorSeriesDecimated(t0, t0, 1).Select(s => s.SeriesId).ToArray());
        Assert.False(File.Exists(Path.Combine(dir, "dead.ring")));
        Assert.False(File.Exists(Path.Combine(dir, "dead.min")));
        Assert.False(File.Exists(Path.Combine(dir, "stray.min")));
        Assert.True(File.Exists(Path.Combine(dir, "keep.ring")));
    }

    [Fact]
    public void Built_in_ids_are_safe_series_ids_that_a_prune_keeps_and_fit_under_the_cap()
    {
        var t0 = 1_800_000_000L;
        Assert.All(Nexus.Service.Diagnostics.Alerts.BuiltInSensorRules.Ids, id => Assert.True(SensorSeriesRingStore.IsSafeId(id)));
        Assert.True(SensorSeriesRingStore.MaxSeries >= Nexus.Service.Diagnostics.Alerts.SensorAlertRules.MaxRules);
        using (var store = new BinaryMetricsHistoryStore(_dir))
        {
            store.Append(new[] { Sample(t0, ("builtin-cpu", 1), ("user-rule", 2), ("dead", 3)) }, null);
        }

        using var reopened = new BinaryMetricsHistoryStore(_dir);
        var live = new System.Collections.Generic.HashSet<string>(Nexus.Service.Diagnostics.Alerts.BuiltInSensorRules.Ids) { "user-rule" };
        reopened.PruneSensorSeries(live);

        Assert.Equal(new[] { "builtin-cpu", "user-rule" }, reopened.QuerySensorSeriesDecimated(t0, t0, 1).Select(x => x.SeriesId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void An_old_delete_stops_filtering_once_it_is_older_than_the_buffer_can_replay()
    {
        var t0 = 1_800_000_000L;
        using var binary = new BinaryMetricsHistoryStore(_dir);
        using var memory = new InMemoryMetricsHistoryStore();
        foreach (IMetricsHistoryStore store in new IMetricsHistoryStore[] { binary, memory })
        {
            store.DeleteSensorSeries("a", t0);
            // The newest sample is past the buffer retention cap, so the delete can no longer match a replayed sample.
            store.Append(new[] { Sample(t0 - 100, ("a", 1)), Sample(t0 + 3000, ("b", 2)) }, null);

            Assert.Contains(store.QuerySensorSeriesDecimated(t0 - 200, t0 + 4000, 1), s => s.SeriesId == "a");
        }
    }

    [Fact]
    public void A_narrowed_query_reads_only_the_requested_series()
    {
        using var store = new BinaryMetricsHistoryStore(_dir);
        var t0 = 1_800_000_000L;
        store.Append(new[] { Sample(t0, ("a", 1), ("b", 2)) }, null);

        var slots = store.QuerySensorSeriesDecimated(t0, t0, 1, new System.Collections.Generic.HashSet<string> { "b" });

        Assert.Equal("b", slots.Single().SeriesId);
    }

    /// <summary>Worst-case timing probe (all series, 90-day minute rollup). Runs only with NEXUS_BENCH=1: it writes millions of slots.</summary>
    [Fact]
    public void Worst_case_ninety_day_query_timing()
    {
        if (Environment.GetEnvironmentVariable("NEXUS_BENCH") != "1")
        {
            return;
        }
        using var store = new BinaryMetricsHistoryStore(_dir);
        var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60;
        var days = MetricsHistory.TempRetentionDays;
        var ids = Enumerable.Range(0, SensorSeriesRingStore.MaxSeries).Select(i => $"rule-{i}").ToArray();
        // One sample per minute, written in two-day batches so the two-day raw ring never collides within a batch.
        for (var day = days; day > 0; day--)
        {
            store.Append(Enumerable.Range(0, 1440).Select(m => Sample(end - day * 86_400L + m * 60L, ids.Select(id => (id, 40.0 + m % 7)).ToArray())).ToList(), null);
        }
        var from = end - days * 86_400L;
        var step = MetricsDecimation.StepSecondsFor(days * 86_400L, 600);

        foreach (var (label, filter) in new (string, System.Collections.Generic.HashSet<string>?)[]
        {
            ("32 series", null),
            ("10 series", ids.Take(10).ToHashSet()),
            ("1 series", ids.Take(1).ToHashSet()),
        })
        {
            store.QuerySensorSeriesDecimated(from, end, step, filter);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rows = store.QuerySensorSeriesDecimated(from, end, step, filter).Count;
            Console.Error.WriteLine($"[bench] sensor-series 90d step={step}s {label}: {sw.ElapsedMilliseconds} ms, {rows} rows");
        }
    }
}
