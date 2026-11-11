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
}
