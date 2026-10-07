using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class MonitoringHistoryRouteResponseTests
{
    private static readonly IReadOnlyDictionary<string, string> NoLuids = new Dictionary<string, string>();

    private static MetricSample Scalars(
        long ts, double? cpu = 50, double? mem = 60, double? netIn = 1000, double? netOut = 500, double? cpuTemp = 55,
        double? diskRead = 800, double? diskWrite = 400) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>(),
            DiskReadBytesPerSec: diskRead, DiskWriteBytesPerSec: diskWrite);

    private static MetricSample ComponentSample(long ts, params ComponentTempReading[] components) =>
        new(ts, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { ComponentTemps = components };

    [Fact]
    public void BuildHistoryResponse_ReportsRetentionDaysAndStepSeconds()
    {
        var response = MonitoringHistoryRoutes.BuildHistoryResponse(
            Array.Empty<MetricSample>(), Array.Empty<MetricSample>(), 0, 600, 600, null, NoLuids);

        Assert.True(response.Supported);
        Assert.Equal(MetricsHistory.RetentionDays, response.RetentionDays);
        Assert.Equal(1, response.StepSeconds); // 600s window / 600 maxPoints -> 1s/point
    }

    [Fact]
    public void BuildHistoryResponse_AlwaysIncludesTheEightFixedScalarSeries_WhenUnfiltered()
    {
        var db = new[] { Scalars(0) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 10, 600, null, NoLuids);

        var ids = response.Series.Select(s => s.Id).ToHashSet();
        Assert.Contains("cpu", ids);
        Assert.Contains("memory", ids);
        Assert.Contains("net-in", ids);
        Assert.Contains("net-out", ids);
        Assert.Contains("disk-read", ids);
        Assert.Contains("disk-write", ids);
        Assert.Contains("cpu-temp", ids);
        Assert.Contains("fps", ids);
    }

    [Fact]
    public void BuildHistoryResponse_CpuTempSeries_CarriesTheCpuModel_WithIdAndKindUnchanged()
    {
        var db = new[] { Scalars(0) };

        var named = MonitoringHistoryRoutes.BuildHistoryResponse(
            db, Array.Empty<MetricSample>(), 0, 10, 600, null, NoLuids, "AMD Ryzen 7 9800X3D");
        var fallback = MonitoringHistoryRoutes.BuildHistoryResponse(
            db, Array.Empty<MetricSample>(), 0, 10, 600, null, NoLuids);

        var series = named.Series.Single(x => x.Id == "cpu-temp");
        Assert.Equal("AMD Ryzen 7 9800X3D", series.Name);
        Assert.Equal("cpu-temp", series.Kind);
        Assert.Equal("CPU Temperature", fallback.Series.Single(x => x.Id == "cpu-temp").Name);
    }

    [Fact]
    public void ResolveCpuTempName_UsesTheModel_AndFallsBackWhenUnknownOrFailing()
    {
        Assert.Equal("AMD Ryzen 7 9800X3D", MonitoringHistoryRoutes.ResolveCpuTempName(() => " AMD Ryzen 7 9800X3D "));
        Assert.Equal("CPU Temperature", MonitoringHistoryRoutes.ResolveCpuTempName(() => ""));
        Assert.Equal("CPU Temperature", MonitoringHistoryRoutes.ResolveCpuTempName(() => null));
        Assert.Equal("CPU Temperature", MonitoringHistoryRoutes.ResolveCpuTempName(() => throw new InvalidOperationException("busy")));
    }

    [Fact]
    public void BuildHistoryResponse_FpsSeries_HasCorrectKindAndName()
    {
        var db = new[] { new MetricSample(0, null, null, null, null, null,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { Fps = 60 } };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 10, 600, null, NoLuids);

        var fpsSeries = response.Series.Single(s => s.Id == "fps");
        Assert.Equal("fps", fpsSeries.Kind);
        Assert.Equal("FPS", fpsSeries.Name);
        var point = Assert.Single(fpsSeries.Points);
        Assert.Equal(60, point.Avg);
    }

    [Fact]
    public void BuildHistoryResponse_SeriesFilter_fps_ReturnsOnlyFps()
    {
        var db = new[] { new MetricSample(0, 50, 60, 1000, 500, 55,
            Array.Empty<GpuReading>(), Array.Empty<FanReading>()) { Fps = 60 } };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(
            db, Array.Empty<MetricSample>(), 0, 10, 600, new HashSet<string> { "fps" }, NoLuids);

        var series = Assert.Single(response.Series);
        Assert.Equal("fps", series.Id);
    }

    [Fact]
    public void BuildHistoryResponse_ConvertsTimestampsToUtcMilliseconds()
    {
        var db = new[] { Scalars(1000, cpu: 50) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 1000, 1000, 600, new HashSet<string> { "cpu" }, NoLuids);

        var cpu = Assert.Single(response.Series);
        var point = Assert.Single(cpu.Points);
        Assert.Equal(1_000_000, point.T); // 1000s -> 1,000,000ms
    }

    [Fact]
    public void BuildHistoryResponse_RoundsPercentAndTempSeriesToOneDecimal()
    {
        var db = new[] { Scalars(0, cpu: 42.345) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "cpu" }, NoLuids);

        Assert.Equal(42.3, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_RoundsNetSeriesToWholeNumbers()
    {
        var db = new[] { Scalars(0, netIn: 1234.7) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "net-in" }, NoLuids);

        Assert.Equal(1235, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_RoundsDiskSeriesToWholeNumbers()
    {
        var db = new[] { Scalars(0, diskRead: 4321.4) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "disk-read" }, NoLuids);

        Assert.Equal(4321, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_DiskKindFilter_MatchesBothReadAndWrite()
    {
        var db = new[] { Scalars(0) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "disk" }, NoLuids);

        Assert.Equal(2, response.Series.Count);
        Assert.All(response.Series, s => Assert.Equal("disk", s.Kind));
        Assert.Contains(response.Series, s => s.Id == "disk-read");
        Assert.Contains(response.Series, s => s.Id == "disk-write");
    }

    [Fact]
    public void BuildHistoryResponse_TailSampleOverridesDbSampleAtTheSameTimestamp()
    {
        var db = new[] { Scalars(0, cpu: 10) };
        var tail = new[] { Scalars(0, cpu: 90) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, tail, 0, 0, 600, new HashSet<string> { "cpu" }, NoLuids);

        Assert.Equal(90, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_SeriesFilter_MatchesExactIds()
    {
        var db = new[] { Scalars(0) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "cpu" }, NoLuids);

        Assert.Equal(new[] { "cpu" }, response.Series.Select(s => s.Id));
    }

    [Fact]
    public void BuildHistoryResponse_GpuSeries_PairsLoadAndTemp_KeyedByGpuId()
    {
        var db = new[]
        {
            new MetricSample(0, null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 62) }, Array.Empty<FanReading>()),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, NoLuids);

        var gpuLoad = response.Series.Single(s => s.Id == "gpu:gpu-0");
        Assert.Equal("gpu", gpuLoad.Kind);
        Assert.Equal("RTX 5080", gpuLoad.Name);
        Assert.Equal(55, gpuLoad.Points.Single().Avg);

        var gpuTemp = response.Series.Single(s => s.Id == "gpu-temp:gpu-0");
        Assert.Equal("gpu-temp", gpuTemp.Kind);
        Assert.Equal(62, gpuTemp.Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_GpuSeries_CarriesAdapterLuid_WhenKnown()
    {
        var db = new[]
        {
            new MetricSample(0, null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 62) }, Array.Empty<FanReading>()),
        };
        var luids = new Dictionary<string, string> { ["gpu-0"] = "10:20" };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, luids);

        Assert.Equal("10:20", response.Series.Single(s => s.Id == "gpu:gpu-0").AdapterLuid);
        Assert.Null(response.Series.Single(s => s.Id == "cpu").AdapterLuid);
    }

    [Fact]
    public void BuildHistoryResponse_KindFilter_MatchesAllSeriesOfThatKind()
    {
        var db = new[]
        {
            new MetricSample(0, null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 62), new GpuReading("gpu-1", "RX 7900", "", 30, 40) },
                Array.Empty<FanReading>()),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "gpu" }, NoLuids);

        Assert.Equal(2, response.Series.Count);
        Assert.All(response.Series, s => Assert.Equal("gpu", s.Kind));
    }

    [Fact]
    public void BuildHistoryResponse_FanSeries_PairsRpmAndDuty_KeyedByFanId()
    {
        var db = new[]
        {
            new MetricSample(0, null, null, null, null, null,
                Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Fan 1", 1200, 45) }),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, NoLuids);

        var rpm = response.Series.Single(s => s.Id == "fan:fan-0");
        Assert.Equal("fan", rpm.Kind);
        Assert.Equal(1200, rpm.Points.Single().Avg);

        var duty = response.Series.Single(s => s.Id == "fan-duty:fan-0");
        Assert.Equal("fan-duty", duty.Kind);
        Assert.Equal(45, duty.Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_NoGpuOrFanSeries_WhenNoneEverAppeared()
    {
        var db = new[] { Scalars(0) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, NoLuids);

        Assert.DoesNotContain(response.Series, s => s.Kind is "gpu" or "gpu-temp" or "fan" or "fan-duty");
    }

    [Fact]
    public void BuildHistoryResponse_MemTempSeries_AveragesAcrossRamComponents()
    {
        var db = new[]
        {
            ComponentSample(0,
                new ComponentTempReading("ram:0", "ram", "DIMM A2", 40),
                new ComponentTempReading("ram:1", "ram", "DIMM B2", 50)),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "mem-temp" }, NoLuids);

        var memTemp = Assert.Single(response.Series);
        Assert.Equal("mem-temp", memTemp.Id);
        Assert.Equal("mem-temp", memTemp.Kind);
        Assert.Equal("Memory Temperature", memTemp.Name);
        Assert.Equal(45, memTemp.Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_MemTempSeries_AveragesOnlyThePresentComponent_WhenOneIsMissingAtATimestamp()
    {
        var db = new[]
        {
            ComponentSample(0,
                new ComponentTempReading("ram:0", "ram", "DIMM A2", 40),
                new ComponentTempReading("ram:1", "ram", "DIMM B2", 50)),
            ComponentSample(1, new ComponentTempReading("ram:0", "ram", "DIMM A2", 44)),
        };

        // fromSec=0, toSec=2 with maxPoints=1 forces stepSeconds=2, so ts=0
        // and ts=1 land in the same slot: ram:0's own avg is (40+44)/2=42,
        // ram:1's own avg stays 50 (its only reading); mem-temp then means
        // those two per-component avgs, not a flat average of all 3 raw values.
        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 2, maxPoints: 1, new HashSet<string> { "mem-temp" }, NoLuids);

        var point = Assert.Single(Assert.Single(response.Series).Points);
        Assert.Equal(46, point.Avg); // mean(42, 50)
        Assert.Equal(50, point.Max); // max(44, 50)
    }

    [Fact]
    public void BuildHistoryResponse_DriveTempSeries_OnePerStorageComponent_WithCorrectNameAndId()
    {
        var db = new[]
        {
            ComponentSample(0,
                new ComponentTempReading("storage:ABC123", "storage", "Samsung 990 Pro", 45),
                new ComponentTempReading("storage:XYZ789", "storage", "WD Black SN850", 38)),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "drive-temp" }, NoLuids);

        Assert.Equal(2, response.Series.Count);
        var drive1 = response.Series.Single(s => s.Id == "drive-temp:storage:ABC123");
        Assert.Equal("drive-temp", drive1.Kind);
        Assert.Equal("Samsung 990 Pro", drive1.Name);
        Assert.Equal(45, drive1.Points.Single().Avg);
        var drive2 = response.Series.Single(s => s.Id == "drive-temp:storage:XYZ789");
        Assert.Equal("WD Black SN850", drive2.Name);
    }

    [Fact]
    public void BuildHistoryResponse_DriveTempSeries_SanitizesSlashesInTheComponentId()
    {
        var db = new[] { ComponentSample(0, new ComponentTempReading("storage:/nvme/0", "storage", "NVMe 0", 42)) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "drive-temp" }, NoLuids);

        Assert.Equal("drive-temp:storage:-nvme-0", Assert.Single(response.Series).Id);
    }

    [Fact]
    public void BuildHistoryResponse_DriveTempSeries_DisambiguatesASanitizedIdCollision()
    {
        // "storage:a/b" and "storage:a-b" both sanitize to "storage:a-b".
        var db = new[]
        {
            ComponentSample(0,
                new ComponentTempReading("storage:a/b", "storage", "Drive One", 40),
                new ComponentTempReading("storage:a-b", "storage", "Drive Two", 50)),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "drive-temp" }, NoLuids);

        Assert.Equal(2, response.Series.Count);
        var ids = response.Series.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var driveTwo = response.Series.Single(s => s.Name == "Drive Two");
        Assert.Equal("drive-temp:storage:a-b", driveTwo.Id);

        var driveOne = response.Series.Single(s => s.Name == "Drive One");
        Assert.StartsWith("drive-temp:storage:a-b-", driveOne.Id);
    }

    [Fact]
    public void BuildHistoryResponse_DriveTempKindFilter_MatchesAllDriveSeries()
    {
        var db = new[]
        {
            ComponentSample(0,
                new ComponentTempReading("storage:ABC", "storage", "Drive A", 45),
                new ComponentTempReading("storage:XYZ", "storage", "Drive B", 38)),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "drive-temp" }, NoLuids);

        Assert.Equal(2, response.Series.Count);
        Assert.All(response.Series, s => Assert.Equal("drive-temp", s.Kind));
    }

    [Fact]
    public void BuildHistoryResponse_ComponentTempSeries_TailOverridesDbAtTheSameTimestamp()
    {
        var db = new[] { ComponentSample(0, new ComponentTempReading("storage:ABC", "storage", "Drive", 30)) };
        var tail = new[] { ComponentSample(0, new ComponentTempReading("storage:ABC", "storage", "Drive", 55)) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, tail, 0, 0, 600, new HashSet<string> { "drive-temp" }, NoLuids);

        Assert.Equal(55, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_ComponentTempSeries_DiscoversAComponentOnlySeenInTheTail()
    {
        var tail = new[] { ComponentSample(0, new ComponentTempReading("ram:0", "ram", "DIMM A2", 41)) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(Array.Empty<MetricSample>(), tail, 0, 0, 600, new HashSet<string> { "mem-temp" }, NoLuids);

        Assert.Equal(41, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_NoComponentTempSeries_WhenNoneEverAppeared()
    {
        var db = new[] { Scalars(0) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, NoLuids);

        Assert.DoesNotContain(response.Series, s => s.Kind is "mem-temp" or "drive-temp");
    }

    [Fact]
    public void BuildHistoryResponse_NoMemTempSeries_WhenNoRamComponentHasDataInWindow()
    {
        var db = new[] { ComponentSample(0, new ComponentTempReading("storage:ABC", "storage", "Drive", 45)) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, NoLuids);

        Assert.DoesNotContain(response.Series, s => s.Id == "mem-temp");
    }

    [Fact]
    public void BuildPrivacyResponse_ReportsRetentionDays()
    {
        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(Array.Empty<PrivacySession>(), 0, 1000);

        Assert.True(response.Supported);
        Assert.Equal(PrivacyAccess.RetentionDays, response.RetentionDays);
    }

    [Fact]
    public void BuildPrivacyResponse_ConvertsSecondsToMilliseconds()
    {
        var sessions = new[] { new PrivacySession("app.exe", "microphone", 1000, 1080) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 2000);

        var session = Assert.Single(response.Sessions);
        Assert.Equal("app.exe", session.App);
        Assert.Equal("microphone", session.Capability);
        Assert.Equal(1_000_000, session.Start);
        Assert.Equal(1_080_000, session.End);
    }

    [Fact]
    public void BuildPrivacyResponse_KeepsAnOpenSessionsEndAsNull()
    {
        var sessions = new[] { new PrivacySession("app.exe", "webcam", 1000, null) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 2000);

        Assert.Null(Assert.Single(response.Sessions).End);
    }

    [Fact]
    public void BuildPrivacyResponse_ExcludesAClosedSessionEntirelyBeforeTheWindow()
    {
        var sessions = new[] { new PrivacySession("app.exe", "microphone", 100, 200) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 1000, 2000);

        Assert.Empty(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_ExcludesAClosedSessionEntirelyAfterTheWindow()
    {
        var sessions = new[] { new PrivacySession("app.exe", "microphone", 5000, 5100) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 1000);

        Assert.Empty(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_IncludesAClosedSessionThatPartiallyOverlapsTheWindow()
    {
        var sessions = new[] { new PrivacySession("app.exe", "microphone", 500, 1500) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 1000, 2000);

        Assert.Single(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_IncludesAnOpenSession_WhenItStartsBeforeTheWindowEnd()
    {
        var sessions = new[] { new PrivacySession("app.exe", "webcam", 100, null) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 5000, 10_000);

        Assert.Single(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_ExcludesAnOpenSession_WhenItStartsAfterTheWindowEnd()
    {
        var sessions = new[] { new PrivacySession("app.exe", "webcam", 20_000, null) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 10_000);

        Assert.Empty(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_OrdersSessionsByStart()
    {
        var sessions = new[]
        {
            new PrivacySession("app.exe", "microphone", 2000, 2100),
            new PrivacySession("app.exe", "microphone", 1000, 1100),
        };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 10_000);

        Assert.Equal(new long[] { 1_000_000, 2_000_000 }, response.Sessions.Select(s => s.Start).ToArray());
    }
}
