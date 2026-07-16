using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class SensorSnapshotResolverTests
{
    private sealed class StubSensors : ISensorProvider
    {
        public IReadOnlyList<HardwareSensor> CpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> Gpus { get; init; } = Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> MemorySensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> MotherboardSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyDictionary<string, StorageComponent> StorageComponents { get; init; } = new Dictionary<string, StorageComponent>();
        public List<HardwareComponent> Nics { get; init; } = new();

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => StorageComponents;
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => MotherboardSensors;
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras() => new() { Nics = Nics };
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static HardwareSensor MakeSensor(string id, string type, float value) =>
        MakeSensor(id, id, type, value);

    private static HardwareSensor MakeSensor(string id, string name, string type, float value) => new()
    {
        Id = id,
        Name = name,
        Type = type,
        Value = value,
        Units = "",
        Formatted = value.ToString(),
        Parent = new SensorParent { Id = "test", Name = "test" },
    };

    [Fact]
    public void Resolve_finds_a_quick_summary_sensor()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/package", "Temperature", 55f) } };

        var found = SensorSnapshotResolver.Resolve(sensors, "quick", "summary/cpu-temp");

        Assert.NotNull(found);
        Assert.Equal(55f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_a_cpu_sensor()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/core0", "Load", 12f) } };

        var found = SensorSnapshotResolver.Resolve(sensors, "cpu", "cpu/core0");

        Assert.NotNull(found);
        Assert.Equal(12f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_a_gpu_sensor_on_the_only_gpu()
    {
        var sensors = new StubSensors
        {
            Gpus = new[] { new GpuReadout { Name = "GPU0", Sensors = new List<HardwareSensor> { MakeSensor("gpu/core", "Load", 88f) } } },
        };

        var found = SensorSnapshotResolver.Resolve(sensors, "gpu", "gpu/core");

        Assert.NotNull(found);
        Assert.Equal(88f, found!.Value);
    }

    [Fact]
    public void Resolve_matches_a_gpu_sensor_id_across_multiple_gpus()
    {
        var sensors = new StubSensors
        {
            Gpus = new[]
            {
                new GpuReadout { Name = "GPU0", Integrated = true, Sensors = new List<HardwareSensor> { MakeSensor("gpu0/core", "Load", 10f) } },
                new GpuReadout { Name = "GPU1", Integrated = false, Sensors = new List<HardwareSensor> { MakeSensor("gpu1/core", "Load", 77f) } },
            },
        };

        var fromFirst = SensorSnapshotResolver.Resolve(sensors, "gpu", "gpu0/core");
        var fromSecond = SensorSnapshotResolver.Resolve(sensors, "gpu", "gpu1/core");

        Assert.NotNull(fromFirst);
        Assert.Equal(10f, fromFirst!.Value);
        Assert.NotNull(fromSecond);
        Assert.Equal(77f, fromSecond!.Value);
    }

    [Fact]
    public void Resolve_finds_a_memory_sensor()
    {
        var sensors = new StubSensors { MemorySensors = new[] { MakeSensor("mem/used", "Data", 12.3f) } };

        var found = SensorSnapshotResolver.Resolve(sensors, "memory", "mem/used");

        Assert.NotNull(found);
        Assert.Equal(12.3f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_a_motherboard_sensor()
    {
        var sensors = new StubSensors { MotherboardSensors = new[] { MakeSensor("mobo/temp", "Temperature", 40f) } };

        var found = SensorSnapshotResolver.Resolve(sensors, "motherboard", "mobo/temp");

        Assert.NotNull(found);
        Assert.Equal(40f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_a_storage_sensor()
    {
        var sensors = new StubSensors
        {
            StorageComponents = new Dictionary<string, StorageComponent>
            {
                ["C:"] = new StorageComponent { Sensors = new List<HardwareSensor> { MakeSensor("storage/used", "Load", 63f) } },
            },
        };

        var found = SensorSnapshotResolver.Resolve(sensors, "storage", "storage/used");

        Assert.NotNull(found);
        Assert.Equal(63f, found!.Value);
    }

    [Fact]
    public void Resolve_returns_null_for_an_unknown_sensor_id()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/core0", "Load", 12f) } };

        Assert.Null(SensorSnapshotResolver.Resolve(sensors, "cpu", "cpu/does-not-exist"));
    }

    [Fact]
    public void Resolve_returns_null_for_an_unrecognized_category()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/core0", "Load", 12f) } };

        Assert.Null(SensorSnapshotResolver.Resolve(sensors, "fps", "cpu/core0"));
    }

    [Fact]
    public void ResolveOrDefault_finds_an_exact_id_match()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[]
            {
                MakeSensor("cpu/total", "CPU Total", "Load", 21f),
                MakeSensor("cpu/core0", "Core #0", "Load", 12f),
            },
        };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "cpu", "cpu/core0");

        Assert.NotNull(found);
        Assert.Equal("cpu/core0", found!.Id);
    }

    /// <summary>
    /// Every sensor here is distinct, so only the name rung can return Core #0:
    /// the id rung misses, and both defaults would return CPU Total.
    /// </summary>
    [Fact]
    public void ResolveOrDefault_falls_back_to_a_name_match_when_the_id_no_longer_resolves()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[]
            {
                MakeSensor("cpu/total", "CPU Total", "Load", 21f),
                MakeSensor("cpu/core0/v2", "Core #0", "Load", 12f),
            },
        };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "cpu", "Core #0");

        Assert.NotNull(found);
        Assert.Equal("cpu/core0/v2", found!.Id);
    }

    /// <summary>
    /// resolveSensor's cpu branch has no first-sensor rung for a non-empty id
    /// (MonitoringWidget.tsx:62-66), so an unresolvable id on a box without a
    /// CPU Total sensor stays unresolved on the deck exactly as in the editor.
    /// </summary>
    [Fact]
    public void ResolveOrDefault_returns_null_for_an_unresolvable_cpu_id_when_no_CpuTotal_exists()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[] { MakeSensor("cpu/core0", "Core #0", "Load", 12f) },
        };

        Assert.Null(SensorSnapshotResolver.ResolveOrDefault(sensors, "cpu", "cpu/gone"));
    }

    [Fact]
    public void ResolveOrDefault_falls_back_to_the_quick_summary_default()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("summary/cpu-temp", "Temperature", 55f) } };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "quick", "/amdcpu/0");

        Assert.NotNull(found);
        Assert.Equal(55f, found!.Value);
    }

    /// <summary>
    /// The Elgato import leaves every monitoring key's sensor empty, so the
    /// empty id must reach the category default rather than name-matching a
    /// nameless sensor.
    /// </summary>
    [Fact]
    public void ResolveOrDefault_with_an_empty_id_takes_the_default_over_a_nameless_sensor()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[]
            {
                MakeSensor("cpu/unnamed", "", "Clock", 4200f),
                MakeSensor("cpu/total", "CPU Total", "Load", 21f),
            },
        };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "cpu", "");

        Assert.NotNull(found);
        Assert.Equal("CPU Total", found!.Name);
    }

    [Fact]
    public void ResolveOrDefault_falls_back_to_CpuTotal_by_name_for_a_hardware_level_uid()
    {
        var sensors = new StubSensors
        {
            CpuSensors = new[]
            {
                MakeSensor("cpu/core0", "Load", 12f),
                MakeSensor("cpu/total", "CPU Total", "Load", 34f),
            },
        };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "cpu", "/amdcpu/0");

        Assert.NotNull(found);
        Assert.Equal(34f, found!.Value);
    }

    [Fact]
    public void ResolveOrDefault_falls_back_to_the_first_cpu_sensor_when_no_CpuTotal_exists()
    {
        var sensors = new StubSensors { CpuSensors = new[] { MakeSensor("cpu/core0", "Core #0", "Load", 12f) } };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "cpu", "");

        Assert.NotNull(found);
        Assert.Equal("cpu/core0", found!.Id);
    }

    [Fact]
    public void ResolveOrDefault_falls_back_to_GpuCoreLoad_by_name_and_type()
    {
        var sensors = new StubSensors
        {
            Gpus = new[]
            {
                new GpuReadout
                {
                    Name = "GPU0",
                    Sensors = new List<HardwareSensor>
                    {
                        MakeSensor("gpu/temp", "GPU Core", "Temperature", 60f),
                        MakeSensor("gpu/load", "GPU Core", "Load", 91f),
                    },
                },
            },
        };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "gpu", "/gpu-nvidia/0");

        Assert.NotNull(found);
        Assert.Equal(91f, found!.Value);
    }

    [Fact]
    public void ResolveOrDefault_falls_back_to_the_first_gpu_sensor_when_no_GpuCoreLoad_exists()
    {
        var sensors = new StubSensors
        {
            Gpus = new[] { new GpuReadout { Name = "GPU0", Sensors = new List<HardwareSensor> { MakeSensor("gpu/temp", "Temperature", 60f) } } },
        };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "gpu", "/gpu-nvidia/0");

        Assert.NotNull(found);
        Assert.Equal(60f, found!.Value);
    }

    [Fact]
    public void ResolveOrDefault_falls_back_to_MemoryUsage_by_name()
    {
        var sensors = new StubSensors
        {
            MemorySensors = new[]
            {
                MakeSensor("mem/used", "Data", 12.3f),
                MakeSensor("mem/usage", "Memory Usage", "Level", 45f),
            },
        };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "memory", "/ram/0");

        Assert.NotNull(found);
        Assert.Equal(45f, found!.Value);
    }

    [Fact]
    public void ResolveOrDefault_falls_back_to_the_first_motherboard_sensor()
    {
        var sensors = new StubSensors { MotherboardSensors = new[] { MakeSensor("mobo/temp", "Temperature", 40f) } };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "motherboard", "/lpc/nct6798d/0");

        Assert.NotNull(found);
        Assert.Equal(40f, found!.Value);
    }

    [Fact]
    public void ResolveOrDefault_falls_back_to_the_first_storage_sensor()
    {
        var sensors = new StubSensors
        {
            StorageComponents = new Dictionary<string, StorageComponent>
            {
                ["C:"] = new StorageComponent { Sensors = new List<HardwareSensor> { MakeSensor("storage/used", "Load", 63f) } },
            },
        };

        var found = SensorSnapshotResolver.ResolveOrDefault(sensors, "storage", "/nvme/0");

        Assert.NotNull(found);
        Assert.Equal(63f, found!.Value);
    }

    [Fact]
    public void ResolveOrDefault_returns_null_when_the_category_has_no_sensors()
    {
        var sensors = new StubSensors();

        Assert.Null(SensorSnapshotResolver.ResolveOrDefault(sensors, "cpu", "/amdcpu/0"));
    }
}
