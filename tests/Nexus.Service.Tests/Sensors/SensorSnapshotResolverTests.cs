using System;
using System.Collections.Generic;
using System.Linq;
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
        public List<HardwareComponent> MemoryModules { get; init; } = new();
        public List<HardwareComponent> Batteries { get; init; } = new();
        public List<HardwareComponent> Coolers { get; init; } = new();
        public List<HardwareComponent> Psus { get; init; } = new();
        public List<HardwareComponent> EmbeddedControllers { get; init; } = new();
        public int ExtrasCalls { get; private set; }
        public bool? LastIncludeSmart { get; private set; }

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true)
        {
            LastIncludeSmart = includeSmart;
            return StorageComponents;
        }
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => MotherboardSensors;
        public string GetMotherboardModel() => "TestMobo";
        public SensorExtras GetSensorExtras()
        {
            ExtrasCalls++;
            return new SensorExtras
            {
                Nics = Nics,
                MemoryModules = MemoryModules,
                Batteries = Batteries,
                Coolers = Coolers,
                Psus = Psus,
                EmbeddedControllers = EmbeddedControllers,
            };
        }
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

    private static HardwareComponent Component(string id, params HardwareSensor[] sensors) =>
        new() { Id = id, Name = id, Sensors = new List<HardwareSensor>(sensors) };

    private static HardwareSensor Throughput(string id, string name, float value) =>
        MakeSensor(id, name, "Throughput", value);

    // ── Extras-backed categories (the deck monitoring key's wider set) ──

    [Theory]
    [InlineData("memoryModule")]
    [InlineData("battery")]
    [InlineData("cooler")]
    [InlineData("psu")]
    [InlineData("embeddedController")]
    [InlineData("network")]
    public void CategoryUsesExtras_covers_every_extras_backed_category(string category)
    {
        Assert.True(SensorSnapshotResolver.CategoryUsesExtras(category));
        Assert.False(SensorSnapshotResolver.CategoryUsesFps(category));
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("cpu")]
    [InlineData("gpu")]
    [InlineData("memory")]
    [InlineData("motherboard")]
    [InlineData("storage")]
    [InlineData("smart")]
    [InlineData("fps")]
    public void CategoryUsesExtras_is_false_for_the_rest(string category)
    {
        Assert.False(SensorSnapshotResolver.CategoryUsesExtras(category));
    }

    [Fact]
    public void CategoryUsesFps_is_true_for_fps_alone()
    {
        Assert.True(SensorSnapshotResolver.CategoryUsesFps("fps"));
        Assert.False(SensorSnapshotResolver.CategoryUsesFps("gpu"));
    }

    [Fact]
    public void Resolve_finds_a_dimm_sensor_in_the_extras_topic()
    {
        var sensors = new StubSensors
        {
            MemoryModules = { Component("/memory/dimm/0", MakeSensor("dimm0/temp", "Temperature", 41f)) },
        };

        var found = SensorSnapshotResolver.Resolve(sensors, "memoryModule", "dimm0/temp");

        Assert.NotNull(found);
        Assert.Equal(41f, found!.Value);
    }

    [Fact]
    public void Resolve_finds_battery_cooler_psu_and_embedded_controller_sensors()
    {
        var sensors = new StubSensors
        {
            Batteries = { Component("/battery/0", MakeSensor("batt/level", "Level", 88f)) },
            Coolers = { Component("/cooler/0", MakeSensor("cool/pump", "Fan", 2100f)) },
            Psus = { Component("/psu/0", MakeSensor("psu/watts", "Power", 310f)) },
            EmbeddedControllers = { Component("/ec/0", MakeSensor("ec/fan", "Fan", 900f)) },
        };

        Assert.Equal(88f, SensorSnapshotResolver.Resolve(sensors, "battery", "batt/level")!.Value);
        Assert.Equal(2100f, SensorSnapshotResolver.Resolve(sensors, "cooler", "cool/pump")!.Value);
        Assert.Equal(310f, SensorSnapshotResolver.Resolve(sensors, "psu", "psu/watts")!.Value);
        Assert.Equal(900f, SensorSnapshotResolver.Resolve(sensors, "embeddedController", "ec/fan")!.Value);
    }

    [Fact]
    public void GetCategorySensors_prefers_a_passed_extras_snapshot_over_a_fresh_walk()
    {
        var sensors = new StubSensors
        {
            Batteries = { Component("/battery/0", MakeSensor("batt/level", "Level", 88f)) },
        };
        var extras = new SensorExtras
        {
            Batteries = { Component("/battery/0", MakeSensor("batt/level", "Level", 12f)) },
        };

        var found = SensorSnapshotResolver.Resolve(sensors, "battery", "batt/level", new SensorSnapshotSources(extras));

        Assert.Equal(12f, found!.Value);
        Assert.Equal(0, sensors.ExtrasCalls);
    }

    // ── SMART ──

    [Fact]
    public void GetCategorySensors_smart_returns_only_the_smart_prefixed_components()
    {
        var sensors = new StubSensors
        {
            StorageComponents = new Dictionary<string, StorageComponent>
            {
                ["C:"] = new StorageComponent { Sensors = new List<HardwareSensor> { MakeSensor("storage/used", "Load", 63f) } },
                ["smart//nvme/0"] = new StorageComponent { Sensors = new List<HardwareSensor> { MakeSensor("smart/temp", "Temperature", 44f) } },
            },
        };

        var smart = SensorSnapshotResolver.GetCategorySensors(sensors, "smart");

        Assert.Equal(new[] { "smart/temp" }, smart.Select(x => x.Id));
        Assert.True(sensors.LastIncludeSmart);
    }

    [Fact]
    public void GetCategorySensors_storage_still_asks_for_the_logical_volume_subset()
    {
        var sensors = new StubSensors();

        SensorSnapshotResolver.GetCategorySensors(sensors, "storage");

        Assert.False(sensors.LastIncludeSmart);
    }

    // ── FPS ──

    [Fact]
    public void GetCategorySensors_fps_returns_the_passed_sensors_and_nothing_otherwise()
    {
        var sensors = new StubSensors();
        var fps = new[] { MakeSensor("fps/current", "FPS", "Framerate", 144f) };

        var sources = new SensorSnapshotSources(FpsSensors: fps);
        Assert.Equal(144f, SensorSnapshotResolver.ResolveOrDefault(sensors, "fps", "FPS", sources)!.Value);
        Assert.Empty(SensorSnapshotResolver.GetCategorySensors(sensors, "fps"));

        // Mirrors resolveSensor's fps branch: an unmatched key resolves to
        // nothing rather than defaulting to the first sensor, so the physical
        // key and the touch tile both render the unresolved placeholder.
        Assert.Null(SensorSnapshotResolver.ResolveOrDefault(sensors, "fps", "Frames Per Second", sources));
    }

    // ── Network aggregate ──
    // Kept behaviourally identical to nexus-web's buildNicNetworkSensors; see
    // networkSensors.test.ts for the mirror of these cases.

    [Fact]
    public void Network_sums_upload_and_download_speeds_across_every_adapter()
    {
        var sensors = new StubSensors
        {
            Nics =
            {
                Component("eth", Throughput("a", "Download Speed", 1000f), Throughput("b", "Upload Speed", 200f)),
                Component("wifi", Throughput("c", "Download Speed", 500f), Throughput("d", "Upload Speed", 50f)),
            },
        };

        var network = SensorSnapshotResolver.BuildNicNetworkSensors(sensors.Nics);

        Assert.Equal(new[] { "network-total", "network-in", "network-out" }, network.Select(x => x.Id));
        Assert.Equal(new[] { "Network Total", "Network In", "Network Out" }, network.Select(x => x.Name));
        Assert.Equal(1750f, network[0].Value);
        Assert.Equal(1500f, network[1].Value);
        Assert.Equal(250f, network[2].Value);
        Assert.All(network, s => Assert.Equal("Rate", s.Type));
        Assert.All(network, s => Assert.Equal("B/s", s.Units));
    }

    [Fact]
    public void Network_reads_the_linux_provider_rx_tx_naming_too()
    {
        var sensors = new StubSensors
        {
            Nics = { Component("eth0", Throughput("a", "eth0 RX", 800f), Throughput("b", "eth0 TX", 100f)) },
        };

        var network = SensorSnapshotResolver.BuildNicNetworkSensors(sensors.Nics);

        Assert.Equal(900f, network[0].Value);
        Assert.Equal(800f, network[1].Value);
        Assert.Equal(100f, network[2].Value);
    }

    [Fact]
    public void Network_ignores_non_throughput_sensors_on_the_same_adapter()
    {
        var sensors = new StubSensors
        {
            Nics = { Component("eth", MakeSensor("x", "Data Downloaded", "Data", 900f), Throughput("a", "Download Speed", 10f)) },
        };

        Assert.Equal(10f, SensorSnapshotResolver.BuildNicNetworkSensors(sensors.Nics)[0].Value);
    }

    [Fact]
    public void Network_has_no_aggregate_when_no_adapter_reports_a_directional_throughput_sensor()
    {
        Assert.Empty(SensorSnapshotResolver.BuildNicNetworkSensors(Array.Empty<HardwareComponent>()));
        Assert.Empty(SensorSnapshotResolver.BuildNicNetworkSensors(
            new[] { Component("eth", Throughput("a", "Network Utilization", 5f)) }));
    }

    /// <summary>
    /// A caller that asks for the aggregate gets exactly it - an empty one
    /// included, which resolves to nothing rather than to a per-adapter
    /// reading nexus-web's tile would render as "--".
    /// </summary>
    [Fact]
    public void Network_opted_into_the_aggregate_never_falls_back_to_a_per_adapter_sensor()
    {
        var sensors = new StubSensors
        {
            Nics = { Component("eth", Throughput("nic/util", "Network Utilization", 5f)) },
        };
        var sources = new SensorSnapshotSources(
            NetworkSensors: SensorSnapshotResolver.BuildNicNetworkSensors(sensors.Nics));

        Assert.Empty(SensorSnapshotResolver.GetCategorySensors(sensors, "network", sources));
        Assert.Null(SensorSnapshotResolver.ResolveOrDefault(sensors, "network", "Network Total", sources));
    }

    /// <summary>
    /// Without that opt-in the category keeps returning each adapter's own
    /// sensors - the list the Tryx overlay has always resolved against.
    /// </summary>
    [Fact]
    public void Network_without_the_aggregate_returns_the_per_adapter_sensors()
    {
        var sensors = new StubSensors
        {
            Nics = { Component("eth", Throughput("nic/dl", "Download Speed", 1000f)) },
        };

        Assert.Equal(new[] { "nic/dl" }, SensorSnapshotResolver.GetCategorySensors(sensors, "network").Select(x => x.Id));
        Assert.Equal(1000f, SensorSnapshotResolver.Resolve(sensors, "network", "nic/dl")!.Value);
    }

    [Fact]
    public void Network_treats_one_unusable_reading_as_zero_without_poisoning_the_other_direction()
    {
        var sensors = new StubSensors
        {
            Nics =
            {
                Component("eth", Throughput("a", "Download Speed", float.NaN), Throughput("b", "Upload Speed", 64f)),
                Component("wifi", Throughput("c", "Download Speed", -5f)),
            },
        };

        var network = SensorSnapshotResolver.BuildNicNetworkSensors(sensors.Nics);

        Assert.Equal(64f, network[0].Value);
        Assert.Equal(0f, network[1].Value);
        Assert.Equal(64f, network[2].Value);
    }

    [Theory]
    // Mirrors networkSensors.ts' formatNetworkRate, the string both tile
    // renderers fall back to (neither formatter scales a "B/s" unit itself).
    [InlineData(0f, "0 B/s")]
    [InlineData(512f, "512 B/s")]
    [InlineData(2048f, "2.0 KB/s")]
    [InlineData(1024f * 1024f * 12f, "12 MB/s")]
    [InlineData(1024f * 1024f * 1024f * 3.5f, "3.5 GB/s")]
    public void Network_formats_a_rate_the_way_the_web_builder_does(float bytesPerSecond, string expected)
    {
        var sensors = new StubSensors
        {
            Nics = { Component("eth", Throughput("a", "Download Speed", bytesPerSecond)) },
        };

        Assert.Equal(expected, SensorSnapshotResolver.BuildNicNetworkSensors(sensors.Nics)[1].Formatted);
    }

    private static StubSensors MotherboardFans() => new()
    {
        MotherboardSensors = new HardwareSensor[]
        {
            new() { Id = "/lpc/nct6797d/fan/0", Name = "Fan #1", Type = "Fan", Value = 900f },
            new() { Id = "/lpc/nct6797d/fan/1", Name = "Fan #2", Type = "Fan", Value = 1200f },
        },
    };

    private static SensorSnapshotSources RenamedFirstHeader() => new(
        FanHeaderNames: new Dictionary<string, string> { ["/lpc/nct6797d/fan/0"] = "Radiator Fans" });

    [Fact]
    public void A_renamed_fan_header_reaches_the_resolved_sensor()
    {
        // Deck keys and the touch tile render sensor.Name, so this keeps a physical key
        // reading the same as the editor that picked the sensor.
        var resolved = SensorSnapshotResolver.Resolve(
            MotherboardFans(), "motherboard", "/lpc/nct6797d/fan/0", RenamedFirstHeader());

        Assert.Equal("Radiator Fans", resolved!.Name);
        Assert.Equal(900f, resolved.Value);
    }

    [Fact]
    public void Without_a_rename_map_the_hardware_name_stands()
    {
        Assert.Equal("Fan #1", SensorSnapshotResolver.Resolve(
            MotherboardFans(), "motherboard", "/lpc/nct6797d/fan/0")!.Name);
    }

    [Fact]
    public void A_key_stored_by_hardware_name_still_matches_after_a_rename()
    {
        // ResolveOrDefault falls back to a name match for keys whose id was never concrete
        // (an imported Elgato profile). Renaming the list before that lookup would drop the
        // match and silently land on the category default - a different sensor, not a blank.
        // The second header is the one renamed here, so that wrong fallback reads 900, not 1200.
        var sources = new SensorSnapshotSources(
            FanHeaderNames: new Dictionary<string, string> { ["/lpc/nct6797d/fan/1"] = "Radiator Fans" });

        var resolved = SensorSnapshotResolver.ResolveOrDefault(
            MotherboardFans(), "motherboard", "Fan #2", sources);

        Assert.Equal(1200f, resolved!.Value);
        Assert.Equal("Radiator Fans", resolved.Name);
    }

    [Fact]
    public void A_key_stored_as_the_custom_name_resolves_to_that_header()
    {
        // A key picked AFTER a rename holds the custom name, which no hardware sensor
        // carries; without the fan-name fallback it lands on the category default.
        var sources = new SensorSnapshotSources(
            FanHeaderNames: new Dictionary<string, string> { ["/lpc/nct6797d/fan/1"] = "Radiator Fans" });

        var resolved = SensorSnapshotResolver.ResolveOrDefault(
            MotherboardFans(), "motherboard", "Radiator Fans", sources);

        Assert.Equal(1200f, resolved!.Value);
        Assert.Equal("Radiator Fans", resolved.Name);
    }

    [Fact]
    public void An_unmatched_key_still_falls_back_to_the_category_default()
    {
        var resolved = SensorSnapshotResolver.ResolveOrDefault(
            MotherboardFans(), "motherboard", "No Such Sensor", RenamedFirstHeader());

        Assert.Equal(900f, resolved!.Value);
    }

    [Fact]
    public void A_rename_never_reaches_the_providers_own_sensors()
    {
        var sensors = MotherboardFans();

        SensorSnapshotResolver.Resolve(sensors, "motherboard", "/lpc/nct6797d/fan/0", RenamedFirstHeader());

        Assert.Equal("Fan #1", sensors.MotherboardSensors[0].Name);
        Assert.Equal("Fan #1", SensorSnapshotResolver.Resolve(
            sensors, "motherboard", "/lpc/nct6797d/fan/0")!.Name);
    }
}
