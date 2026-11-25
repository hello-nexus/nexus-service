using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class DriveWriteHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void FullWindow_ReportsDeltaOverEachWindow()
    {
        var h = new DriveWriteHistory(null);
        // 1 GB every 5 minutes for 25 hours.
        DriveWrittenWindows? last = null;
        for (var t = 0; t <= 25 * 3600; t += 300)
        {
            last = h.Observe("/nvme/0", "Drive A", 1000 + t / 300.0, null, At(t));
        }
        Assert.Equal(12, last!.Value.LastHourGb, 3);
        Assert.Equal(288, last.Value.LastDayGb, 3);
    }

    [Fact]
    public void PartialWindow_ReportsLowerBoundFromOldestSnapshot()
    {
        var h = new DriveWriteHistory(null);
        h.Observe("/nvme/0", "Drive A", 500, null, At(0));
        h.Observe("/nvme/0", "Drive A", 505, null, At(300));
        var w = h.Observe("/nvme/0", "Drive A", 510, null, At(600))!.Value;
        Assert.Equal(10, w.LastHourGb, 3);
        Assert.Equal(10, w.LastDayGb, 3);
    }

    [Fact]
    public void CounterReset_RestartsTheSeries()
    {
        var h = new DriveWriteHistory(null);
        h.Observe("/nvme/0", "Drive A", 900, null, At(0));
        h.Observe("/nvme/0", "Drive A", 910, null, At(300));
        var w = h.Observe("/nvme/0", "Drive A", 3, null, At(600))!.Value;
        Assert.Equal(0, w.LastHourGb, 3);
        var next = h.Observe("/nvme/0", "Drive A", 7, null, At(900))!.Value;
        Assert.Equal(4, next.LastHourGb, 3);
    }

    [Fact]
    public void Persistence_RoundTripsSnapshotsAcrossRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-drive-writes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var a = new DriveWriteHistory(path);
            a.Observe("/nvme/0", "Drive A", 100, null, At(0));
            a.Observe("/nvme/0", "Drive A", 104, null, At(300));
            a.Flush();
            var b = new DriveWriteHistory(path);
            var w = b.Observe("/nvme/0", "Drive A", 110, null, At(600))!.Value;
            Assert.Equal(10, w.LastDayGb, 3);
            Assert.Equal(10, w.LastHourGb, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Persistence_CorruptFileStartsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-drive-writes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ not json");
            var h = new DriveWriteHistory(path);
            Assert.Equal(0, h.Observe("/nvme/0", "Drive A", 50, null, At(0))!.Value.LastDayGb, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RateFallback_IntegratesThroughputIntoGb()
    {
        var h = new DriveWriteHistory(null);
        const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
        h.Observe("/hdd/0", "Drive H", null, bytesPerGb, At(0));
        DriveWrittenWindows? w = null;
        for (var t = 1; t <= 600; t++)
        {
            w = h.Observe("/hdd/0", "Drive H", null, bytesPerGb, At(t));
        }
        Assert.Equal(600, w!.Value.LastHourGb, 3);
    }

    [Fact]
    public void RateFallback_GapOverTheCapIsNotIntegrated()
    {
        var h = new DriveWriteHistory(null);
        const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
        h.Observe("/hdd/0", "Drive H", null, bytesPerGb, At(0));
        var w = h.Observe("/hdd/0", "Drive H", null, bytesPerGb, At(3000))!.Value;
        Assert.Equal(0, w.LastHourGb, 3);
        Assert.True(DriveWriteHistory.MaxIntegrateSeconds > 60);
    }

    [Fact]
    public void NeitherSource_ReturnsNull()
    {
        var h = new DriveWriteHistory(null);
        Assert.Null(h.Observe("/nvme/0", "Drive A", null, null, At(0)));
        Assert.Null(h.Observe("/nvme/0", "Drive A", 0, double.NaN, At(1)));
    }

    [Fact]
    public void SamplesStayBounded()
    {
        var h = new DriveWriteHistory(null);
        for (var t = 0; t <= 3 * 86400; t += 300)
        {
            h.Observe("/nvme/0", "Drive A", 1 + t, null, At(t));
        }
        var w = h.Observe("/nvme/0", "Drive A", 1 + 3 * 86400 + 300, null, At(3 * 86400 + 300))!.Value;
        Assert.Equal(86400 + 0, w.LastDayGb, 0);
    }

    [Fact]
    public void DifferentIdentityUnderSameId_ResetsTheSeries()
    {
        var h = new DriveWriteHistory(null);
        h.Observe("/nvme/0", "Drive A", 500, null, At(0));
        h.Observe("/nvme/0", "Drive A", 510, null, At(300));
        // Another drive took the index, with a higher counter: no 400 GB phantom delta.
        var w = h.Observe("/nvme/0", "Drive B", 900, null, At(600))!.Value;
        Assert.Equal(0, w.LastDayGb, 3);
    }

    [Fact]
    public void RateAt60sCadence_IsNotUnderReported()
    {
        var h = new DriveWriteHistory(null);
        const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
        h.Observe("/hdd/0", "Drive H", null, bytesPerGb, At(0));
        DriveWrittenWindows? w = null;
        for (var t = 60; t <= 600; t += 60)
        {
            w = h.Observe("/hdd/0", "Drive H", null, bytesPerGb, At(t));
        }
        Assert.Equal(600, w!.Value.LastHourGb, 3);
    }

    [Fact]
    public void CounterSeries_SurvivesAPollWithoutACounter()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-drive-writes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var h = new DriveWriteHistory(path);
            h.Observe("/nvme/0", "Drive A", 100, null, At(0));
            h.Observe("/nvme/0", "Drive A", 110, null, At(300));
            // Counter missing (and zero) while a rate is present: history stays, source stays.
            var missing = h.Observe("/nvme/0", "Drive A", null, 1e9, At(360))!.Value;
            var zero = h.Observe("/nvme/0", "Drive A", 0, 1e9, At(420))!.Value;
            Assert.Equal(10, missing.LastDayGb, 3);
            Assert.Equal(10, zero.LastDayGb, 3);
            var back = h.Observe("/nvme/0", "Drive A", 112, null, At(480))!.Value;
            Assert.Equal(12, back.LastDayGb, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NullFieldsInPersistedSeriesDoNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-drive-writes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"drives\":{\"/a\":{\"source\":null,\"samples\":null},\"/b\":{\"source\":\"counter\",\"samples\":null},\"/c\":null}}");
            var h = new DriveWriteHistory(path);
            Assert.Equal(0, h.Observe("/b", "x", 50, null, At(0))!.Value.LastDayGb, 3);
            Assert.NotNull(h.Observe("/a", "x", 50, null, At(0)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FutureStampedSnapshots_AreDroppedAfterAClockCorrection()
    {
        var h = new DriveWriteHistory(null);
        h.Observe("/nvme/0", "Drive A", 100, null, At(10_000));
        h.Observe("/nvme/0", "Drive A", 101, null, At(10_300));
        // Clock stepped back by over an hour.
        h.Observe("/nvme/0", "Drive A", 105, null, At(0));
        var w = h.Observe("/nvme/0", "Drive A", 110, null, At(300))!.Value;
        Assert.Equal(5, w.LastHourGb, 3);
    }

    [Fact]
    public void Flush_WritesOncePerPollNotPerObservation()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-drive-writes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var h = new DriveWriteHistory(path);
            h.Observe("/nvme/0", "Drive A", 100, null, At(0));
            h.Observe("/nvme/1", "Drive B", 200, null, At(0));
            Assert.False(File.Exists(path));
            h.Flush();
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CounterGoneForGood_FallsBackToRateOrDropsTheSeries()
    {
        const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
        var h = new DriveWriteHistory(null);
        h.Observe("/hdd/0", "D", 100, null, At(0));
        var day = DriveWriteHistory.DaySeconds;
        Assert.NotNull(h.Observe("/hdd/0", "D", null, bytesPerGb, At(day - 10)));
        var w = h.Observe("/hdd/0", "D", null, bytesPerGb, At(day + 10));
        Assert.NotNull(w);
        h.Observe("/hdd/0", "D", null, bytesPerGb, At(day + 20));
        Assert.Equal(0, h.Observe("/hdd/0", "D", null, bytesPerGb, At(day + 20))!.Value.LastDayGb, 3);

        var g = new DriveWriteHistory(null);
        g.Observe("/hdd/1", "D", 100, null, At(0));
        Assert.Null(g.Observe("/hdd/1", "D", null, null, At(day + 10)));
    }

    [Fact]
    public void ShortCounterDropout_HoldsTheLastObservedCounter()
    {
        var h = new DriveWriteHistory(null);
        h.Observe("/nvme/0", "A", 100, null, At(0));
        h.Observe("/nvme/0", "A", 110, null, At(100));
        var w = h.Observe("/nvme/0", "A", null, null, At(160))!.Value;
        Assert.Equal(10, w.LastHourGb, 3);
    }

    [Fact]
    public void Append_PublishesStableIdsAndUsesCounterOverRate()
    {
        var h = new DriveWriteHistory(null);
        List<HardwareSensor> Sensors(float written) => new()
        {
            new HardwareSensor { Id = "/nvme/0/data/2", Name = "Data Written", Type = "Data", Value = written, Units = "GB" },
            new HardwareSensor { Id = "/nvme/0/throughput/1", Name = "Write Rate", Type = "Throughput", Value = 1e9f, Units = "B/s" },
        };
        DriveWriteHistory.Append(h, "/nvme/0", "Samsung 990 PRO", Sensors(100), At(0));
        var sensors = Sensors(103);
        DriveWriteHistory.Append(h, "/nvme/0", "Samsung 990 PRO", sensors, At(60));

        var h1 = sensors.Single(s => s.Id == "/nvme/0/derived/written-1h");
        var h24 = sensors.Single(s => s.Id == "/nvme/0/derived/written-24h");
        Assert.Equal("Data Written (1 h)", h1.Name);
        Assert.Equal("Data Written (24 h)", h24.Name);
        Assert.All(new[] { h1, h24 }, s =>
        {
            Assert.Equal("Data", s.Type);
            Assert.Equal("GB", s.Units);
            Assert.Equal("/nvme/0", s.Parent.Id);
        });
        Assert.Equal(3f, h1.Value, 3);
    }

    [Fact]
    public void Append_AddsNothingWithoutASource()
    {
        var sensors = new List<HardwareSensor>
        {
            new() { Id = "/nvme/0/temperature/0", Name = "Composite Temperature", Type = "Temperature", Value = 40 },
            new() { Id = "/nvme/0/data/2", Name = "Data Written", Type = "Data", Value = 100, Stale = true },
        };
        DriveWriteHistory.Append(new DriveWriteHistory(null), "/nvme/0", "x", sensors, At(0));
        Assert.Equal(2, sensors.Count);
    }
}
