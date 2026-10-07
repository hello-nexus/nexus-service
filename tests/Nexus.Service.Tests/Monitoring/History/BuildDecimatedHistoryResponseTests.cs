using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class BuildDecimatedHistoryResponseTests
{
    private static readonly IReadOnlyDictionary<string, string> NoLuids = new Dictionary<string, string>();

    private static ScalarDecimatedSlot ScalarSlot(long slot, double? cpuAvg, double? cpuMax) =>
        new(slot, cpuAvg, cpuMax, null, null, null, null, null, null, null, null);

    [Fact]
    public void BuildDecimatedHistoryResponse_ConvertsSlotsToUtcMillisecondPoints()
    {
        var dbScalars = new[] { ScalarSlot(1000, 42, 50) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            dbScalars, Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            fromSec: 1000, toSec: 1000, stepSeconds: 600, seriesFilter: new HashSet<string> { "cpu" }, gpuAdapterLuids: NoLuids);

        var cpu = Assert.Single(response.Series);
        var point = Assert.Single(cpu.Points);
        Assert.Equal(1_000_000, point.T);
        Assert.Equal(42, point.Avg);
        Assert.Equal(50, point.Max);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_CpuTempSeries_CarriesTheCpuModel_WithIdAndKindUnchanged()
    {
        var dbScalars = new[] { ScalarSlot(1000, 42, 50) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            dbScalars, Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            fromSec: 1000, toSec: 1000, stepSeconds: 600, seriesFilter: new HashSet<string> { "cpu-temp" }, gpuAdapterLuids: NoLuids,
            cpuTempName: "AMD Ryzen 7 9800X3D");

        var series = Assert.Single(response.Series);
        Assert.Equal("cpu-temp", series.Id);
        Assert.Equal("cpu-temp", series.Kind);
        Assert.Equal("AMD Ryzen 7 9800X3D", series.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CPU")]
    public void BuildDecimatedHistoryResponse_CpuTempSeries_FallsBackToTheGenericNameWhenTheModelIsUnknown(string? sampleCpuName)
    {
        var dbScalars = new[] { ScalarSlot(1000, 42, 50) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            dbScalars, Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            fromSec: 1000, toSec: 1000, stepSeconds: 600, seriesFilter: new HashSet<string> { "cpu-temp" }, gpuAdapterLuids: NoLuids,
            cpuTempName: MonitoringHistoryRoutes.ResolveCpuTempName(sampleCpuName));

        Assert.Equal("CPU Temperature", Assert.Single(response.Series).Name);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_FpsSlot_ProducesTheFpsSeries()
    {
        var dbScalars = new[] { new ScalarDecimatedSlot(
            1000, null, null, null, null, null, null, null, null, null, null, null, null, null, null, FpsAvg: 60, FpsMax: 90) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            dbScalars, Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            fromSec: 1000, toSec: 1000, stepSeconds: 600, seriesFilter: new HashSet<string> { "fps" }, gpuAdapterLuids: NoLuids);

        var fps = Assert.Single(response.Series);
        Assert.Equal("fps", fps.Id);
        Assert.Equal("fps", fps.Kind);
        var point = Assert.Single(fps.Points);
        Assert.Equal(60, point.Avg);
        Assert.Equal(90, point.Max);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_ReportsStepSecondsAndRetentionDays()
    {
        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(),
            Array.Empty<MetricSample>(), 0, 0, stepSeconds: 1800, seriesFilter: null, gpuAdapterLuids: NoLuids);

        Assert.True(response.Supported);
        Assert.Equal(1800, response.StepSeconds);
        Assert.Equal(MetricsHistory.RetentionDays, response.RetentionDays);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_TailOverridesADbSlotAtTheSameSlot()
    {
        var dbScalars = new[] { ScalarSlot(0, cpuAvg: 10, cpuMax: 10) };
        var tail = new[] { new MetricSample(5, 90, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>()) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            dbScalars, Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), tail,
            fromSec: 0, toSec: 9, stepSeconds: 10, seriesFilter: new HashSet<string> { "cpu" }, gpuAdapterLuids: NoLuids);

        Assert.Equal(90, Assert.Single(Assert.Single(response.Series).Points).Avg);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_KeepsSeparateDbSlots_WhenTailOnlyCoversTheLastOne()
    {
        var dbScalars = new[] { ScalarSlot(0, cpuAvg: 10, cpuMax: 10), ScalarSlot(10, cpuAvg: 20, cpuMax: 20) };
        var tail = new[] { new MetricSample(15, 99, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>()) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            dbScalars, Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), tail,
            fromSec: 0, toSec: 19, stepSeconds: 10, seriesFilter: new HashSet<string> { "cpu" }, gpuAdapterLuids: NoLuids);

        var points = Assert.Single(response.Series).Points;
        Assert.Equal(2, points.Count);
        Assert.Equal(10, points[0].Avg); // untouched db slot
        Assert.Equal(99, points[1].Avg); // tail-overridden slot
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_SkipsAFieldEntirely_WhenNoDbOrTailDataExists()
    {
        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(),
            Array.Empty<MetricSample>(), 0, 9, stepSeconds: 10, seriesFilter: new HashSet<string> { "cpu" }, gpuAdapterLuids: NoLuids);

        Assert.Empty(Assert.Single(response.Series).Points);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_DiskSeries_ReadsAvgAndMaxFromTheirOwnSlotFields()
    {
        var dbScalars = new[]
        {
            new ScalarDecimatedSlot(0, null, null, null, null, null, null, null, null, null, null,
                DiskReadAvg: 2000, DiskReadMax: 3000, DiskWriteAvg: 400, DiskWriteMax: 600),
        };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            dbScalars, Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: new HashSet<string> { "disk" }, gpuAdapterLuids: NoLuids);

        Assert.Equal(2, response.Series.Count);
        var read = response.Series.Single(s => s.Id == "disk-read");
        Assert.Equal("disk", read.Kind);
        Assert.Equal(2000, read.Points.Single().Avg);
        Assert.Equal(3000, read.Points.Single().Max);
        var write = response.Series.Single(s => s.Id == "disk-write");
        Assert.Equal(400, write.Points.Single().Avg);
        Assert.Equal(600, write.Points.Single().Max);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_GpuSeries_PairsLoadAndTemp_KeyedByGpuId()
    {
        var dbGpu = new[] { new GpuDecimatedSlot("gpu-0", "RTX 5080", 0, 55, 60, 62, 65) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), dbGpu, Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: null, gpuAdapterLuids: NoLuids);

        var load = response.Series.Single(s => s.Id == "gpu:gpu-0");
        Assert.Equal("gpu", load.Kind);
        Assert.Equal(55, load.Points.Single().Avg);
        var temp = response.Series.Single(s => s.Id == "gpu-temp:gpu-0");
        Assert.Equal(62, temp.Points.Single().Avg);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_GpuSeries_CarriesAdapterLuid()
    {
        var dbGpu = new[] { new GpuDecimatedSlot("gpu-0", "RTX 5080", 0, 55, 60, 62, 65) };
        var luids = new Dictionary<string, string> { ["gpu-0"] = "10:20" };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), dbGpu, Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: null, gpuAdapterLuids: luids);

        Assert.Equal("10:20", response.Series.Single(s => s.Id == "gpu:gpu-0").AdapterLuid);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_FanSeries_PairsRpmAndDuty_KeyedByFanId()
    {
        var dbFan = new[] { new FanDecimatedSlot("fan-0", "Fan 1", 0, 1200, 1300, 45, 50) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), dbFan, Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: null, gpuAdapterLuids: NoLuids);

        var rpm = response.Series.Single(s => s.Id == "fan:fan-0");
        Assert.Equal(1200, rpm.Points.Single().Avg);
        var duty = response.Series.Single(s => s.Id == "fan-duty:fan-0");
        Assert.Equal(45, duty.Points.Single().Avg);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_DiscoversAGpuOnlySeenInTheTail()
    {
        var tail = new[]
        {
            new MetricSample(5, null, null, null, null, null,
                new[] { new GpuReading("gpu-hotplug", "New GPU", "", 20, 30) }, Array.Empty<FanReading>()),
        };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), tail,
            0, 9, stepSeconds: 10, seriesFilter: null, gpuAdapterLuids: NoLuids);

        Assert.Contains(response.Series, s => s.Id == "gpu:gpu-hotplug");
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_KindFilter_MatchesAllSeriesOfThatKind()
    {
        var dbGpu = new[]
        {
            new GpuDecimatedSlot("gpu-0", "RTX 5080", 0, 55, 60, 62, 65),
            new GpuDecimatedSlot("gpu-1", "RX 7900", 0, 30, 35, 40, 42),
        };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), dbGpu, Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: new HashSet<string> { "gpu" }, gpuAdapterLuids: NoLuids);

        Assert.Equal(2, response.Series.Count);
        Assert.All(response.Series, s => Assert.Equal("gpu", s.Kind));
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_MemTempSeries_AveragesAcrossRamComponents()
    {
        var dbComponents = new[]
        {
            new ComponentTempDecimatedSlot("ram:0", "ram", "DIMM A2", 0, 40, 44),
            new ComponentTempDecimatedSlot("ram:1", "ram", "DIMM B2", 0, 50, 50),
        };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), dbComponents, Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: new HashSet<string> { "mem-temp" }, gpuAdapterLuids: NoLuids);

        var memTemp = Assert.Single(response.Series);
        Assert.Equal("mem-temp", memTemp.Id);
        Assert.Equal("mem-temp", memTemp.Kind);
        var point = Assert.Single(memTemp.Points);
        Assert.Equal(45, point.Avg); // mean(40, 50)
        Assert.Equal(50, point.Max); // max(44, 50)
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_DriveTempSeries_OnePerStorageComponent()
    {
        var dbComponents = new[]
        {
            new ComponentTempDecimatedSlot("storage:ABC", "storage", "Samsung 990 Pro", 0, 45, 48),
        };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), dbComponents, Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: null, gpuAdapterLuids: NoLuids);

        var drive = response.Series.Single(s => s.Id == "drive-temp:storage:ABC");
        Assert.Equal("drive-temp", drive.Kind);
        Assert.Equal("Samsung 990 Pro", drive.Name);
        Assert.Equal(45, drive.Points.Single().Avg);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_DriveTempKindFilter_MatchesAllDriveSeries()
    {
        var dbComponents = new[]
        {
            new ComponentTempDecimatedSlot("storage:ABC", "storage", "Drive A", 0, 40, 45),
            new ComponentTempDecimatedSlot("storage:XYZ", "storage", "Drive B", 0, 30, 35),
        };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), dbComponents, Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: new HashSet<string> { "drive-temp" }, gpuAdapterLuids: NoLuids);

        Assert.Equal(2, response.Series.Count);
        Assert.All(response.Series, s => Assert.Equal("drive-temp", s.Kind));
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_ComponentTempSeries_TailOverridesDbSlot()
    {
        var dbComponents = new[] { new ComponentTempDecimatedSlot("storage:ABC", "storage", "Drive", 0, 30, 30) };
        var tail = new[]
        {
            new MetricSample(5, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>())
            {
                ComponentTemps = new[] { new ComponentTempReading("storage:ABC", "storage", "Drive", 55) },
            },
        };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), dbComponents, tail,
            0, 9, stepSeconds: 10, seriesFilter: new HashSet<string> { "drive-temp" }, gpuAdapterLuids: NoLuids);

        Assert.Equal(55, Assert.Single(Assert.Single(response.Series).Points).Avg);
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_DiscoversAComponentOnlySeenInTheTail()
    {
        var tail = new[]
        {
            new MetricSample(5, null, null, null, null, null, Array.Empty<GpuReading>(), Array.Empty<FanReading>())
            {
                ComponentTemps = new[] { new ComponentTempReading("ram:0", "ram", "DIMM A2", 42) },
            },
        };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), Array.Empty<ComponentTempDecimatedSlot>(), tail,
            0, 9, stepSeconds: 10, seriesFilter: null, gpuAdapterLuids: NoLuids);

        Assert.Contains(response.Series, s => s.Id == "mem-temp");
    }

    [Fact]
    public void BuildDecimatedHistoryResponse_NoMemTempSeries_WhenNoRamComponentHasDataInWindow()
    {
        var dbComponents = new[] { new ComponentTempDecimatedSlot("storage:ABC", "storage", "Drive", 0, 40, 45) };

        var response = MonitoringHistoryRoutes.BuildDecimatedHistoryResponse(
            Array.Empty<ScalarDecimatedSlot>(), Array.Empty<GpuDecimatedSlot>(), Array.Empty<FanDecimatedSlot>(), dbComponents, Array.Empty<MetricSample>(),
            0, 9, stepSeconds: 10, seriesFilter: null, gpuAdapterLuids: NoLuids);

        Assert.DoesNotContain(response.Series, s => s.Id == "mem-temp");
    }
}
