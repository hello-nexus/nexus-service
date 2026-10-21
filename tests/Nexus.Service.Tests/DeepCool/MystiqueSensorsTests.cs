using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.DeepCool;

public class MystiqueSensorsTests
{
    private static HardwareSensor Fan(string id, string name, float rpm) => new() { Id = id, Name = name, Type = "Fan", Value = rpm, Units = "RPM" };

    private static FakeSensors Board(float? temp = 55f, float mhz = 5380f) => new(temp, mhz)
    {
        Motherboard = new[]
        {
            Fan("fan/chassis", "Chassis Fan #1", 900),
            Fan("fan/cpu", "CPU Fan", 1700),
            Fan("fan/opt", "CPU_OPT", 3341),
        },
    };

    [Fact]
    public void Reads_cpu_summary_values_and_the_auto_picked_headers()
    {
        var reading = MystiqueSensors.Read(Board(), new DeepCoolMystiqueSettings());

        Assert.Equal(new MystiqueReading(55, 25, 5380, 1700, 3341), reading);
    }

    [Fact]
    public void A_picked_sensor_overrides_the_header_guess()
    {
        var settings = new DeepCoolMystiqueSettings
        {
            CpuFan = new DeepCoolMystiqueSensorPick { Category = "motherboard", Sensor = "fan/chassis" },
        };

        Assert.Equal(900, MystiqueSensors.Read(Board(), settings).FanRpm);
    }

    [Fact]
    public void A_missing_header_reads_zero_rather_than_another_headers_speed()
    {
        var sensors = new FakeSensors(55f, 5380f) { Motherboard = new[] { Fan("fan/chassis", "Chassis Fan #1", 900) } };

        var reading = MystiqueSensors.Read(sensors, new DeepCoolMystiqueSettings());

        Assert.Equal(0, reading.FanRpm);
        Assert.Equal(0, reading.PumpRpm);
    }

    [Fact]
    public void Without_cpu_sensors_every_value_is_zero()
    {
        var reading = MystiqueSensors.Read(new FakeSensors(null, 0f, withCpu: false), new DeepCoolMystiqueSettings());

        Assert.Equal(new MystiqueReading(0, 0, 0, 0, 0), reading);
    }

    [Fact]
    public void A_non_finite_reading_is_sent_as_zero()
    {
        var sensors = new FakeSensors(float.NaN, float.PositiveInfinity);

        var reading = MystiqueSensors.Read(sensors, new DeepCoolMystiqueSettings());

        Assert.Equal(0, reading.TempC);
        Assert.Equal(0, reading.CpuMhz);
    }

    private sealed class FakeSensors : ISensorProvider
    {
        private readonly float? _temp;
        private readonly float _mhz;
        private readonly bool _withCpu;

        public FakeSensors(float? temp, float mhz, bool withCpu = true)
        {
            _temp = temp;
            _mhz = mhz;
            _withCpu = withCpu;
        }

        public IReadOnlyList<HardwareSensor> Motherboard { get; init; } = Array.Empty<HardwareSensor>();

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => !_withCpu ? Array.Empty<HardwareSensor>() : new[]
        {
            new HardwareSensor { Id = "c/t", Name = "CPU Package", Type = "Temperature", Value = _temp ?? 0f },
            new HardwareSensor { Id = "c/l", Name = "CPU Total", Type = "Load", Value = 25 },
            new HardwareSensor { Id = "c/clk", Name = "Core Average", Type = "Clock", Value = _mhz },
        };
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Motherboard;
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
