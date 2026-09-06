using System;
using System.Linq;
using Nexus.Service.Mcp.History;
using Nexus.Service.Monitoring.History;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// MonitoringSensorHistoryReader against InMemoryMetricsHistoryStore (a
/// lightweight stand-in for BinaryMetricsHistoryStore that supports the same
/// IMetricsHistoryStore surface without touching disk): every id-vocabulary
/// family routes to its typed query, Summarize's min/max/avg/latest/samples
/// math, and KnownSensorIds' per-field data-presence filtering.
/// </summary>
public sealed class MonitoringSensorHistoryReaderTests
{
    private static long ToSec(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();
    private static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Theory]
    [InlineData("cpu.temp", "CPU Temperature", "C", 41)]
    [InlineData("cpu.load", "CPU Load", "%", 42)]
    [InlineData("mem.load", "Memory Load", "%", 43)]
    [InlineData("net.in", "Network In", "B/s", 44)]
    [InlineData("net.out", "Network Out", "B/s", 45)]
    [InlineData("disk.read", "Disk Read", "B/s", 46)]
    [InlineData("disk.write", "Disk Write", "B/s", 47)]
    public void QuerySensorHistory_FixedScalars_ResolveToTheRightField(
        string sensorId, string expectedName, string expectedUnit, double expectedValue)
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[]
        {
            new MetricSample(
                ToSec(now), CpuPercent: 42, MemoryPercent: 43, NetInBytesPerSec: 44, NetOutBytesPerSec: 45, CpuTempC: 41,
                Gpus: Array.Empty<GpuReading>(), Fans: Array.Empty<FanReading>(),
                DiskReadBytesPerSec: 46, DiskWriteBytesPerSec: 47),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var series = reader.QuerySensorHistory(sensorId, ToMs(now.AddMinutes(-1)), ToMs(now.AddMinutes(1)), maxPoints: 10);

        Assert.NotNull(series);
        Assert.Equal(expectedName, series!.Name);
        Assert.Equal(expectedUnit, series.Unit);
        Assert.Equal(expectedValue, Assert.Single(series.Points).Value);
    }

    [Fact]
    public void QuerySensorHistory_GpuLoadAndTemp_ResolveToQueryGpuDecimated()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[]
        {
            new MetricSample(ToSec(now), null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 65) }, Array.Empty<FanReading>()),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var load = reader.QuerySensorHistory("gpu.gpu-0.load", ToMs(now.AddMinutes(-1)), ToMs(now.AddMinutes(1)), maxPoints: 10);
        var temp = reader.QuerySensorHistory("gpu.gpu-0.temp", ToMs(now.AddMinutes(-1)), ToMs(now.AddMinutes(1)), maxPoints: 10);

        Assert.NotNull(load);
        Assert.Equal("RTX 5080 Load", load!.Name);
        Assert.Equal("%", load.Unit);
        Assert.Equal(55, Assert.Single(load.Points).Value);

        Assert.NotNull(temp);
        Assert.Equal("RTX 5080 Temperature", temp!.Name);
        Assert.Equal("C", temp.Unit);
        Assert.Equal(65, Assert.Single(temp.Points).Value);
    }

    [Fact]
    public void QuerySensorHistory_FanRpmAndDuty_ResolveToQueryFanDecimated()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[]
        {
            new MetricSample(ToSec(now), null, null, null, null, null,
                Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Front Fan", 1200, 75) }),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var rpm = reader.QuerySensorHistory("fan.fan-0.rpm", ToMs(now.AddMinutes(-1)), ToMs(now.AddMinutes(1)), maxPoints: 10);
        var duty = reader.QuerySensorHistory("fan.fan-0.duty", ToMs(now.AddMinutes(-1)), ToMs(now.AddMinutes(1)), maxPoints: 10);

        Assert.NotNull(rpm);
        Assert.Equal("Front Fan RPM", rpm!.Name);
        Assert.Equal("RPM", rpm.Unit);
        Assert.Equal(1200, Assert.Single(rpm.Points).Value);

        Assert.NotNull(duty);
        Assert.Equal("Front Fan Duty", duty!.Name);
        Assert.Equal("%", duty.Unit);
        Assert.Equal(75, Assert.Single(duty.Points).Value);
    }

    [Fact]
    public void QuerySensorHistory_ComponentTemp_ResolvesToQueryComponentTempDecimated()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[]
        {
            new MetricSample(ToSec(now), null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
                ComponentTemps: new[] { new ComponentTempReading("ram:0", "ram", "Memory", 38.5) }),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var series = reader.QuerySensorHistory("temp.ram:0", ToMs(now.AddMinutes(-1)), ToMs(now.AddMinutes(1)), maxPoints: 10);

        Assert.NotNull(series);
        Assert.Equal("Memory", series!.Name);
        Assert.Equal("C", series.Unit);
        Assert.Equal(38.5, Assert.Single(series.Points).Value);
    }

    [Theory]
    [InlineData("bogus.id")]
    [InlineData("gpu.foo.bogus")]
    [InlineData("fan.foo.bogus")]
    public void QuerySensorHistory_IdsThatDoNotMatchTheVocabulary_ReturnNull(string sensorId)
    {
        var reader = new MonitoringSensorHistoryReader(new InMemoryMetricsHistoryStore());

        var series = reader.QuerySensorHistory(sensorId, 0, ToMs(DateTime.UtcNow), maxPoints: 10);

        Assert.Null(series);
    }

    [Fact]
    public void QuerySensorHistory_GpuIdNeverRecordedAnywhere_ReturnsNull()
    {
        var reader = new MonitoringSensorHistoryReader(new InMemoryMetricsHistoryStore());
        var now = DateTime.UtcNow;

        var series = reader.QuerySensorHistory("gpu.does-not-exist.load", ToMs(now.AddMinutes(-5)), ToMs(now), maxPoints: 10);

        Assert.Null(series);
    }

    [Fact]
    public void QuerySensorHistory_GpuKnownOutsideTheRequestedWindow_ReturnsEmptyPointsWithItsName()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[]
        {
            new MetricSample(ToSec(now.AddHours(-2)), null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, 60) }, Array.Empty<FanReading>()),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var series = reader.QuerySensorHistory("gpu.gpu-0.load", ToMs(now.AddMinutes(-5)), ToMs(now), maxPoints: 10);

        Assert.NotNull(series);
        Assert.Empty(series!.Points);
        Assert.Equal("RTX 5080 Load", series.Name);
    }

    [Fact]
    public void Summarize_ReportsMinMaxAvgLatestSamples()
    {
        var store = new InMemoryMetricsHistoryStore();
        var t0 = DateTime.UtcNow.AddSeconds(-2);
        var t1 = DateTime.UtcNow.AddSeconds(-1);
        store.Append(new[]
        {
            new MetricSample(ToSec(t0), null, null, null, null, 40, Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
            new MetricSample(ToSec(t1), null, null, null, null, 60, Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var rows = reader.Summarize(ToMs(t0) - 1000, ToMs(t1) + 1000);

        var row = Assert.Single(rows, r => r.SensorId == "cpu.temp");
        Assert.Equal(40, row.Min);
        Assert.Equal(60, row.Max);
        Assert.Equal(50, row.Avg, precision: 6);
        Assert.Equal(60, row.Latest);
        Assert.Equal(2, row.Samples);
    }

    [Fact]
    public void Summarize_OnlyIncludesIdsWithDataInTheWindow()
    {
        var store = new InMemoryMetricsHistoryStore();
        store.Append(new[]
        {
            new MetricSample(ToSec(DateTime.UtcNow.AddSeconds(-1)), null, null, null, null, 40, Array.Empty<GpuReading>(), Array.Empty<FanReading>()),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var rows = reader.Summarize(ToMs(DateTime.UtcNow.AddHours(2)), ToMs(DateTime.UtcNow.AddHours(3)));

        Assert.Empty(rows);
    }

    [Fact]
    public void KnownSensorIds_OnlyListsIdsWithANonNullReadingInTheDiscoveryWindow()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[]
        {
            new MetricSample(ToSec(now), null, null, null, null, 40,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 50, null) },
                new[] { new FanReading("fan-0", "Front Fan", 1200, null) },
                ComponentTemps: new[] { new ComponentTempReading("ram:0", "ram", "Memory", 38) }),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var ids = reader.KnownSensorIds();

        Assert.Contains("cpu.temp", ids);
        Assert.DoesNotContain("cpu.load", ids);
        Assert.Contains("gpu.gpu-0.load", ids);
        Assert.DoesNotContain("gpu.gpu-0.temp", ids);
        Assert.Contains("fan.fan-0.rpm", ids);
        Assert.DoesNotContain("fan.fan-0.duty", ids);
        Assert.Contains("temp.ram:0", ids);
    }

    [Fact]
    public void Summarize_CoversEveryFamilyAndEveryEntity_WhenTheStoreHoldsMultipleOfEach()
    {
        var store = new InMemoryMetricsHistoryStore();
        var now = DateTime.UtcNow;
        store.Append(new[]
        {
            new MetricSample(ToSec(now), 10, 20, 30, 40, 50,
                new[]
                {
                    new GpuReading("gpu-0", "RTX 5080", "", 60, 70),
                    new GpuReading("gpu-1", "RTX 4060", "", 65, 75),
                },
                new[]
                {
                    new FanReading("fan-0", "Front Fan", 1200, 80),
                    new FanReading("fan-1", "Rear Fan", 1100, 85),
                },
                ComponentTemps: new[]
                {
                    new ComponentTempReading("storage:serial1", "storage", "Samsung 990 Pro", 45),
                    new ComponentTempReading("ram:0", "ram", "DIMM_A1", 38),
                },
                DiskReadBytesPerSec: 90, DiskWriteBytesPerSec: 95),
        }, null);
        var reader = new MonitoringSensorHistoryReader(store);

        var rows = reader.Summarize(ToMs(now.AddMinutes(-1)), ToMs(now.AddMinutes(1)));

        var expected = new[]
        {
            "cpu.temp", "cpu.load", "mem.load", "net.in", "net.out", "disk.read", "disk.write",
            "gpu.gpu-0.load", "gpu.gpu-0.temp", "gpu.gpu-1.load", "gpu.gpu-1.temp",
            "fan.fan-0.rpm", "fan.fan-0.duty", "fan.fan-1.rpm", "fan.fan-1.duty",
            "temp.ram:0", "temp.storage:serial1",
        };
        var ids = rows.Select(r => r.SensorId).ToList();
        Assert.Equal(expected.Length, ids.Count);
        Assert.Equal(expected.OrderBy(id => id, StringComparer.Ordinal), ids.OrderBy(id => id, StringComparer.Ordinal));
    }
}
