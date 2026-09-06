using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Mcp.History;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// Covers SystemMetricsSource's storage/RAM temperature reads and CPU name
/// caching.
/// </summary>
public class SystemMetricsSourceTests
{
    private sealed class StubPerformanceProvider : IPerformanceProvider
    {
        public Task<PerformanceSnapshot> SampleAsync(CancellationToken ct = default) =>
            Task.FromResult(new PerformanceSnapshot { Cpu = 10, Memory = 20 });
    }

    private sealed class StubFanControlProvider : IFanControlProvider
    {
        public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => 0;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private sealed class StubSensors : ISensorProvider
    {
        public string CpuModel { get; set; } = "Test CPU Model";
        public IReadOnlyList<HardwareSensor> MemorySensors { get; set; } = Array.Empty<HardwareSensor>();

        public string GetCpuModel() => CpuModel;
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "Stub Board";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubSmartHealthSource : ISmartHealthSource
    {
        public SmartSnapshot Result { get; set; } = new() { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };
        public SmartSnapshot Snapshot() => Result;
    }

    private static HardwareSensor Sensor(string name, string type, float value) => new()
    {
        Id = $"test/{name}",
        Name = name,
        Type = type,
        Value = value,
        Parent = new SensorParent { Id = "test", Name = "test" },
    };

    private static SystemMetricsSource CreateSource(StubSensors sensors, StubSmartHealthSource? smart = null) =>
        new(new StubPerformanceProvider(), sensors, new StubFanControlProvider(), new NetworkRateReader(), new DiskRateReader(),
            smart ?? new StubSmartHealthSource());

    [Fact]
    public async Task SampleAsync_IncludesStorageTemperature_WhenSmartHealthIsSupported()
    {
        var smart = new StubSmartHealthSource
        {
            Result = new SmartSnapshot
            {
                Supported = true,
                Drives = new List<SmartDriveInfo>
                {
                    new() { Id = "storage:serial1", Name = "Samsung 990 Pro", TemperatureC = 45.0 },
                },
            },
        };
        var source = CreateSource(new StubSensors(), smart);

        var sample = await source.SampleAsync(1000, CancellationToken.None);

        var reading = Assert.Single(sample.ComponentTemps);
        Assert.Equal("storage:serial1", reading.ComponentId);
        Assert.Equal("storage", reading.Kind);
        Assert.Equal("Samsung 990 Pro", reading.Name);
        Assert.Equal(45.0, reading.ValueC);
    }

    [Fact]
    public async Task SampleAsync_ExcludesStorage_WhenSmartHealthIsUnsupported()
    {
        var source = CreateSource(new StubSensors());

        var sample = await source.SampleAsync(1000, CancellationToken.None);

        Assert.Empty(sample.ComponentTemps);
    }

    [Fact]
    public async Task SampleAsync_IncludesRamTemperature_ForSensorsNamedDimmOrMemory()
    {
        var sensors = new StubSensors
        {
            MemorySensors = new[]
            {
                Sensor("DIMM_A1", "Temperature", 40f),
                Sensor("Memory Controller", "Load", 10f), // wrong type: not a temperature sensor
                Sensor("Fan Speed", "Temperature", 30f),  // wrong name: not dimm/memory
            },
        };
        var source = CreateSource(sensors);

        var sample = await source.SampleAsync(1000, CancellationToken.None);

        var reading = Assert.Single(sample.ComponentTemps);
        Assert.Equal("ram:0", reading.ComponentId);
        Assert.Equal("ram", reading.Kind);
        Assert.Equal(40.0, reading.ValueC);
    }

    [Fact]
    public async Task SampleAsync_IndexesMultipleRamSensors_InEncounterOrder()
    {
        var sensors = new StubSensors
        {
            MemorySensors = new[]
            {
                Sensor("DIMM_A1", "Temperature", 40f),
                Sensor("DIMM_B1", "Temperature", 42f),
            },
        };
        var source = CreateSource(sensors);

        var sample = await source.SampleAsync(1000, CancellationToken.None);

        Assert.Equal(2, sample.ComponentTemps.Count);
        Assert.Equal("ram:0", sample.ComponentTemps[0].ComponentId);
        Assert.Equal("ram:1", sample.ComponentTemps[1].ComponentId);
    }

    [Fact]
    public async Task SampleAsync_RamComponentIds_AgreeWithHistoryIdMappingsRamSensorIds()
    {
        var memorySensors = new[]
        {
            Sensor("Memory Controller", "Load", 10f),
            Sensor("VRM", "Temperature", 35f),
            Sensor("DIMM_A1", "Temperature", 40f),
            Sensor("Memory Bank 2", "Temperature", 42f),
        };
        var sensors = new StubSensors { MemorySensors = memorySensors };
        var source = CreateSource(sensors);

        var sample = await source.SampleAsync(1000, CancellationToken.None);
        var map = HistoryIdMapping.RamSensorIds(memorySensors);

        Assert.Equal(2, sample.ComponentTemps.Count);
        Assert.Equal("ram:0", sample.ComponentTemps[0].ComponentId);
        Assert.Equal("ram:1", sample.ComponentTemps[1].ComponentId);
        Assert.Equal("temp.ram:0", map[memorySensors[2].Id]);
        Assert.Equal("temp.ram:1", map[memorySensors[3].Id]);
    }

    [Fact]
    public async Task SampleAsync_ResolvesCpuNameFromSensors_AndCachesItAcrossCalls()
    {
        var sensors = new StubSensors { CpuModel = "AMD Ryzen 9 7950X" };
        var source = CreateSource(sensors);

        var first = await source.SampleAsync(1000, CancellationToken.None);
        sensors.CpuModel = "changed after first call";
        var second = await source.SampleAsync(1001, CancellationToken.None);

        Assert.Equal("AMD Ryzen 9 7950X", first.CpuName);
        Assert.Equal("AMD Ryzen 9 7950X", second.CpuName);
    }

    [Fact]
    public async Task SampleAsync_FallsBackToCpuLiteral_WhenTheModelStringIsBlank()
    {
        var sensors = new StubSensors { CpuModel = "   " };
        var source = CreateSource(sensors);

        var sample = await source.SampleAsync(1000, CancellationToken.None);

        Assert.Equal("CPU", sample.CpuName);
    }
}
