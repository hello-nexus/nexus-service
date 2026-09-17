using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Fps;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Routes;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxPanoramaHubTests
{
    // ── Stubs ──

    private sealed class RecordingTransport : ITryxPanoramaTransport
    {
        public bool IsOpen { get; set; } = true;
        public string Serial => "test-serial";
        public string PortName => "COM1";
        public List<byte[]> Writes { get; } = new();
        public IReadOnlyList<string> AvailableMediaIds { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> AvailableCustomMediaFilenames { get; set; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, long> MediaFileSizes { get; set; } = new Dictionary<string, long>();
        public int MediaListVersion { get; set; }
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void Dispose() { }
    }

    private sealed class StubDiscovery : ITryxPanoramaPanelDiscovery
    {
        private readonly TryxPanoramaPortInfo _port;
        public StubDiscovery(string portName = "COM1")
        {
            _port = new TryxPanoramaPortInfo { PortName = portName, Serial = "test-serial" };
        }
        public IReadOnlyList<TryxPanoramaPortInfo> Discover() => new[] { _port };
    }

    private sealed class EmptyDiscovery : ITryxPanoramaPanelDiscovery
    {
        public IReadOnlyList<TryxPanoramaPortInfo> Discover() => Array.Empty<TryxPanoramaPortInfo>();
    }

    private sealed class StubSensors : ISensorProvider
    {
        public IReadOnlyList<HardwareSensor> CpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> GpuSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> MemorySensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyList<HardwareSensor> MotherboardSensors { get; init; } = Array.Empty<HardwareSensor>();
        public IReadOnlyDictionary<string, StorageComponent> StorageComponents { get; init; } =
            new Dictionary<string, StorageComponent>();
        public List<HardwareComponent> Nics { get; init; } = new();

        public string GetCpuModel() => "TestCPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => CpuSensors;
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
        public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => GpuSensors;
        public IReadOnlyList<GpuReadout> GetGpus()
        {
            if (GpuSensors.Count == 0)
            {
                return Array.Empty<GpuReadout>();
            }
            return new[]
            {
                new GpuReadout
                {
                    Name = "TestGPU",
                    Integrated = false,
                    Sensors = new List<HardwareSensor>(GpuSensors),
                },
            };
        }
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => MemorySensors;
        public string GetMemoryTotalFormatted() => "32 GB";
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) =>
            includeSmart
                ? StorageComponents
                : StorageComponents
                    .Where(kv => !LhmComponentIdentifiers.IsSmartStorageComponent(kv.Value.Id))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
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

    private static HardwareSensor MakeSensor(string name, string type, float value) => new()
    {
        Id = $"test/{name}",
        Name = name,
        Type = type,
        Value = value,
        Units = "",
        Formatted = value.ToString(),
        Parent = new SensorParent { Id = "test", Name = "test" },
    };

    private static HardwareSensor MakeIdSensor(string id, string type, float value) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        Value = value,
        Units = "",
        Formatted = value.ToString(),
        Parent = new SensorParent { Id = "fps", Name = "FPS" },
    };

    // Mirrors the real IFpsProvider demand contract (WindowsFpsProvider.SetDemand):
    // capture is wanted while any named source's demand is true. Windows-only
    // WindowsFpsProvider is excluded from this build (Nexus.Service.csproj), so
    // this fake is what the two-source coordination and the hub's demand call
    // sites are verified against.
    private sealed class FakeFpsProvider : IFpsProvider
    {
        private readonly HashSet<string> _demands = new(StringComparer.Ordinal);
        public List<HardwareSensor> Sensors { get; set; } = new();
        public bool IsCapturing => _demands.Count > 0;

        public void SetDemand(string source, bool wanted)
        {
            if (wanted) _demands.Add(source);
            else _demands.Remove(source);
        }

        public HardwareComponent GetComponent() => new()
        {
            Id = "fps",
            Name = "FPS",
            Sensors = Sensors,
        };

        public bool TryReadCurrentFps(out double fps)
        {
            fps = 0;
            return false;
        }

        public void Dispose() { }
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private NexusSettings _doc = new();
        public string SettingsPath => "";
        public NexusSettings Load() => _doc;
        public void Update(Action<NexusSettings> mutator) { mutator(_doc); OnChanged?.Invoke(); }
        public void FlushNow() { }
        public void Reload() { }
        public event Action? OnChanged;
    }

    private static TryxPanoramaHub BuildHub(
        ITryxPanoramaPanelDiscovery? discovery = null,
        Func<TryxPanoramaPortInfo, ITryxPanoramaTransport>? transportFactory = null,
        ISensorProvider? sensors = null,
        IFpsProvider? fps = null,
        IConfigStore? configStore = null)
    {
        return new TryxPanoramaHub(
            discovery ?? new EmptyDiscovery(),
            transportFactory ?? (_ => new RecordingTransport()),
            sensors ?? new StubSensors(),
            fps ?? new FakeFpsProvider(),
            configStore ?? new InMemoryConfigStore());
    }

    // ── Task 1: Persistence ──

    [Fact]
    public void Constructor_loads_overlay_from_config_store()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.OverlayItems = [new TryxOverlaySensorItemSettings { SensorId = "s1", Device = "cpu", Label = "CPU Temp", X = 0.03, Y = 0.10 }];
            s.Tryx.OverlayColor = "#ff0000";
            s.Tryx.OverlayAlign = "right";
            s.Tryx.OverlayOpacity = 75;
            s.Tryx.OverlayDocked = true;
        });

        var hub = BuildHub(configStore: store);

        Assert.Single(hub.Overlay.Items);
        Assert.Equal("s1", hub.Overlay.Items[0].SensorId);
        Assert.Equal("cpu", hub.Overlay.Items[0].Device);
        Assert.Equal("CPU Temp", hub.Overlay.Items[0].Label);
        Assert.Equal(0.03, hub.Overlay.Items[0].X);
        Assert.Equal(0.10, hub.Overlay.Items[0].Y);
        Assert.Equal("#ff0000", hub.Overlay.Color);
        Assert.Equal("right", hub.Overlay.Align);
        Assert.Equal(75, hub.Overlay.Opacity);
        Assert.True(hub.Overlay.Docked);
    }

    [Fact]
    public void Constructor_loads_font_and_size_from_config_store()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.OverlayFont = "roboto-bold";
            s.Tryx.OverlaySize = 120;
        });

        var hub = BuildHub(configStore: store);

        Assert.Equal("roboto-bold", hub.Overlay.Font);
        Assert.Equal(120, hub.Overlay.Size);
    }

    [Fact]
    public void Constructor_defaults_items_font_size_align_and_docked_on_a_settings_file_that_predates_them()
    {
        // An empty InMemoryConfigStore mirrors a settings.json written before this
        // feature: no overlayItems/overlayFont/overlaySize/overlayDocked keys, only
        // their C# defaults apply.
        var hub = BuildHub(configStore: new InMemoryConfigStore());

        Assert.Empty(hub.Overlay.Items);
        Assert.Equal("roboto-regular", hub.Overlay.Font);
        Assert.Equal(100, hub.Overlay.Size);
        Assert.Equal("left", hub.Overlay.Align);
        Assert.False(hub.Overlay.Docked);
    }

    [Fact]
    public void Constructor_loads_current_media_from_config_store()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.CurrentMedia = "myclip.mp4";
            s.Tryx.CurrentMediaIsCustom = true;
            s.Tryx.Brightness = 60;
        });

        var hub = BuildHub(configStore: store);

        Assert.Equal("myclip.mp4", hub.State.CurrentMedia);
        Assert.True(hub.State.CurrentMediaIsCustom);
        Assert.Equal(60, hub.State.Brightness);
    }

    [Fact]
    public void SetOverlay_persists_and_writes_an_rk_overlay_frame()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();
        var overlay = new TryxOverlayConfig
        {
            Items =
            [
                new TryxOverlaySensorItem { SensorId = "s1", Device = "cpu", Label = "CPU Temp", X = 0.03, Y = 0.10 },
                new TryxOverlaySensorItem { SensorId = "s2", Device = "gpu", Label = "GPU Temp", X = 0.50, Y = 0.60 },
            ],
            Color = "#00ff00",
            Align = "center",
            Filter = "blur",
            Opacity = 80,
            Font = "roboto-bold",
            Size = 120,
            Docked = true,
        };

        var ok = hub.SetOverlay(overlay);

        Assert.True(ok);
        Assert.Equal("#00ff00", store.Load().Tryx.OverlayColor);
        Assert.Equal("center", store.Load().Tryx.OverlayAlign);
        Assert.Equal("roboto-bold", store.Load().Tryx.OverlayFont);
        Assert.Equal(120, store.Load().Tryx.OverlaySize);
        Assert.True(store.Load().Tryx.OverlayDocked);
        Assert.Equal(2, store.Load().Tryx.OverlayItems.Count);
        Assert.Equal("s1", store.Load().Tryx.OverlayItems[0].SensorId);
        Assert.Equal("cpu", store.Load().Tryx.OverlayItems[0].Device);
        Assert.Equal("CPU Temp", store.Load().Tryx.OverlayItems[0].Label);
        Assert.Equal(0.03, store.Load().Tryx.OverlayItems[0].X);
        Assert.Equal(0.10, store.Load().Tryx.OverlayItems[0].Y);
        Assert.Single(recording.Writes);
    }

    [Fact]
    public void SetOverlay_writes_an_rk_frame_using_the_configured_position_font_size_and_align()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var sensors = new StubSensors { CpuSensors = [MakeSensor("Package", "Temperature", 0f)] };
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            sensors: sensors,
            configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();
        var overlay = new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Package", Device = "cpu", Label = "CPU Temp", X = 0.03, Y = 0.10 }],
            Color = "#ffffff",
            Align = "right",
            Font = "monospace",
            Size = 50,
        };

        hub.SetOverlay(overlay);

        var expected = TryxRkProtocol.BuildOverlay(
            new[] { new TryxOverlayLine("CPU Temp", "0°C") },
            new[] { (0.03, 0.10) }, colorRgb: 0xFFFFFF, fontName: "monospace", sizePercent: 50, align: "right");

        Assert.Equal(expected, Assert.Single(recording.Writes));
    }

    [Theory]
    [InlineData("Temperature", 45f, "45°C")]
    [InlineData("Load", 18f, "18%")]
    [InlineData("Clock", 4713f, "4713MHz")]
    [InlineData("Frequency", 2400f, "2400MHz")]
    [InlineData("Power", 65f, "65W")]
    [InlineData("Fan", 1200f, "1200RPM")]
    [InlineData("Framerate", 60f, "60fps")]
    [InlineData("FrameTime", 16.7f, "16.7ms")]
    public void SetOverlay_formats_the_resolved_sensor_by_its_type(string type, float value, string expectedValueText)
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var sensors = new StubSensors { CpuSensors = [MakeSensor("Package", type, value)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors, configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Package", Device = "cpu", Label = "Stat" }],
        });

        var expected = TryxRkProtocol.BuildOverlay(
            new[] { new TryxOverlayLine("Stat", expectedValueText) },
            new[] { (0.0, 0.0) }, colorRgb: 0, fontName: "roboto-regular", sizePercent: 100, align: "left");
        Assert.Equal(expected, Assert.Single(recording.Writes));
    }

    [Fact]
    public void SetOverlay_formats_voltage_to_two_decimals()
    {
        var recording = new RecordingTransport();
        var sensors = new StubSensors { CpuSensors = [MakeSensor("VCore", "Voltage", 1.2f)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/VCore", Device = "cpu", Label = "Stat" }],
        });

        Assert.Contains("1.20V", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    [Fact]
    public void SetOverlay_formats_data_to_one_decimal_gb()
    {
        var recording = new RecordingTransport();
        var sensors = new StubSensors { MemorySensors = [MakeSensor("Used", "Data", 12.34f)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Used", Device = "memory", Label = "Stat" }],
        });

        Assert.Contains("12.3GB", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    [Fact]
    public void SetOverlay_formats_smalldata_as_whole_megabytes_not_gigabytes()
    {
        // SmallData is LHM's type for GPU VRAM (used/total/free), reported in MB;
        // formatting it as GB would render an 8192 MB card as "8192.0GB".
        var recording = new RecordingTransport();
        var sensors = new StubSensors { GpuSensors = [MakeSensor("GPU Memory Total", "SmallData", 8192f)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/GPU Memory Total", Device = "gpu", Label = "Stat" }],
        });

        var text = Encoding.UTF8.GetString(Assert.Single(recording.Writes));
        Assert.Contains("8192MB", text);
        Assert.DoesNotContain("GB", text);
    }

    [Fact]
    public void SetOverlay_scales_throughput_bytes_per_sec_to_a_readable_unit()
    {
        var recording = new RecordingTransport();
        // LHM Throughput sensors report bytes/sec; ~12 MB/s (12_000_000 B/s) must render as
        // "11.4MB/s", not the raw "12000000.0MB/s" the overlay showed before.
        var nics = new List<HardwareComponent> { new() { Sensors = [MakeSensor("Download", "Throughput", 12_000_000f)] } };
        var sensors = new StubSensors { Nics = nics };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Download", Device = "network", Label = "Stat" }],
        });

        var text = Encoding.UTF8.GetString(Assert.Single(recording.Writes));
        Assert.Contains("11.4MB/s", text);
        Assert.DoesNotContain("12000000", text);
    }

    [Fact]
    public void SetOverlay_resolves_a_storage_sensor_from_storage_components()
    {
        var recording = new RecordingTransport();
        var components = new Dictionary<string, StorageComponent>
        {
            ["C"] = new() { Sensors = [MakeSensor("Temp", "Temperature", 40f)] },
        };
        var sensors = new StubSensors { StorageComponents = components };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Temp", Device = "storage", Label = "Stat" }],
        });

        Assert.Contains("40°C", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    [Fact]
    public void SetOverlay_storage_device_excludes_lhm_smart_components()
    {
        // GetStorageComponents(includeSmart: false) drops LHM SMART rows before
        // they're built (LhmComponentIdentifiers.IsSmartStorageComponent); the
        // hub calls it with includeSmart: false so the overlay only ever
        // resolves the DriveInfo logical-volume subset.
        var recording = new RecordingTransport();
        var components = new Dictionary<string, StorageComponent>
        {
            ["C"] = new() { Id = "C", Sensors = [MakeSensor("Temp", "Temperature", 40f)] },
            ["smart/nvme/0"] = new() { Id = "smart/nvme/0", Sensors = [MakeSensor("Composite Temperature", "Temperature", 55f)] },
        };
        var sensors = new StubSensors { StorageComponents = components };

        Assert.DoesNotContain(sensors.GetStorageComponents(includeSmart: false).Keys, id => id.StartsWith("smart/"));

        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Composite Temperature", Device = "storage", Label = "Stat" }],
        });

        Assert.Contains("--", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    [Fact]
    public void SetOverlay_falls_back_to_dashes_when_the_sensor_id_is_not_found()
    {
        var recording = new RecordingTransport();
        var sensors = new StubSensors { CpuSensors = [MakeSensor("Package", "Temperature", 45f)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "does-not-exist", Device = "cpu", Label = "Stat" }],
        });

        Assert.Contains("--", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    [Fact]
    public void SetOverlay_falls_back_to_dashes_for_an_unknown_device()
    {
        var recording = new RecordingTransport();
        var sensors = new StubSensors { CpuSensors = [MakeSensor("Package", "Temperature", 45f)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Package", Device = "battery", Label = "Stat" }],
        });

        Assert.Contains("--", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    [Fact]
    public void SetOverlay_resolves_gpu_from_the_primary_discrete_gpu()
    {
        var recording = new RecordingTransport();
        var sensors = new StubSensors { GpuSensors = [MakeSensor("Core", "Temperature", 70f)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, sensors: sensors);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Core", Device = "gpu", Label = "Stat" }],
        });

        Assert.Contains("70°C", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    // ── Task 4: fps overlay sensor + demand coordination ──

    [Fact]
    public void SetOverlay_resolves_an_fps_sensor_from_the_fps_provider()
    {
        var recording = new RecordingTransport();
        var fps = new FakeFpsProvider { Sensors = [MakeIdSensor("fps/current", "Framerate", 60f)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, fps: fps);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "fps/current", Device = "fps", Label = "FPS" }],
        });

        Assert.Contains("60fps", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    [Fact]
    public void SetOverlay_resolves_an_fps_frame_time_sensor()
    {
        var recording = new RecordingTransport();
        var fps = new FakeFpsProvider { Sensors = [MakeIdSensor("fps/frame-time", "FrameTime", 16.7f)] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, fps: fps);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "fps/frame-time", Device = "fps", Label = "Frame Time" }],
        });

        Assert.Contains("16.7ms", Encoding.UTF8.GetString(Assert.Single(recording.Writes)));
    }

    [Fact]
    public void SetOverlay_asserts_fps_demand_when_an_item_uses_the_fps_device()
    {
        var fps = new FakeFpsProvider();
        var hub = BuildHub(discovery: new StubDiscovery(), fps: fps);
        hub.EnsureConnected();

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "fps/current", Device = "fps", Label = "FPS" }],
        });

        Assert.True(fps.IsCapturing);
    }

    [Fact]
    public void SetOverlay_does_not_assert_fps_demand_while_disconnected()
    {
        // Configuring an fps overlay item with no panel attached must not start ETW
        // capture nothing consumes; the heartbeat asserts it once a panel connects.
        var fps = new FakeFpsProvider();
        var hub = BuildHub(discovery: new StubDiscovery(), fps: fps);

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "fps/current", Device = "fps", Label = "FPS" }],
        });

        Assert.False(fps.IsCapturing);
    }

    [Fact]
    public void SetOverlay_clears_fps_demand_when_no_item_uses_the_fps_device()
    {
        var fps = new FakeFpsProvider();
        fps.SetDemand("tryx-overlay", true);
        var hub = BuildHub(fps: fps);

        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "test/Package", Device = "cpu", Label = "CPU" }],
        });

        Assert.False(fps.IsCapturing);
    }

    [Fact]
    public void SendHeartbeatTick_asserts_fps_demand_from_the_current_overlay_every_tick()
    {
        var fps = new FakeFpsProvider();
        var hub = BuildHub(discovery: new StubDiscovery(), fps: fps);
        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "fps/current", Device = "fps", Label = "FPS" }],
        });
        // Simulate the demand having been dropped by another owner between ticks
        // (e.g. MonitoringBroadcaster.StopAsync); the heartbeat must re-assert it.
        fps.SetDemand("tryx-overlay", false);

        hub.SendHeartbeatTick();

        Assert.True(fps.IsCapturing);
    }

    [Fact]
    public void Disconnect_clears_the_tryx_fps_demand()
    {
        var fps = new FakeFpsProvider();
        var hub = BuildHub(discovery: new StubDiscovery(), fps: fps);
        hub.EnsureConnected();
        hub.SetOverlay(new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "fps/current", Device = "fps", Label = "FPS" }],
        });
        Assert.True(fps.IsCapturing);

        hub.Disconnect();

        Assert.False(fps.IsCapturing);
    }

    [Fact]
    public void FpsProvider_demand_keeps_capture_on_while_any_source_wants_it()
    {
        // The coordination contract IFpsProvider.SetDemand must honor: capture runs
        // while any named source's demand is true, so the monitoring broadcaster
        // and the Tryx overlay never fight over one shared on/off flag.
        var fps = new FakeFpsProvider();

        fps.SetDemand("monitoring", false);
        fps.SetDemand("tryx-overlay", true);
        Assert.True(fps.IsCapturing);

        fps.SetDemand("tryx-overlay", false);
        Assert.False(fps.IsCapturing);
    }

    [Fact]
    public void SetPreset_writes_the_wallpaper_config_and_persists()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();

        var media = TryxRkProtocol.PresetMediaFile(3);
        var ok = hub.SetPreset(media);

        Assert.True(ok);
        Assert.Single(recording.Writes);
        Assert.Contains(media, Encoding.UTF8.GetString(recording.Writes[0]));
        Assert.Equal(media, store.Load().Tryx.CurrentMedia);
        Assert.False(store.Load().Tryx.CurrentMediaIsCustom);
    }

    [Fact]
    public void Constructor_loads_slideshow_from_config_store()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.SlideshowEnabled = true;
            s.Tryx.SlideshowIntervalSec = 60;
            s.Tryx.SlideshowShuffle = true;
            s.Tryx.SlideshowFinishVideos = false;
        });

        var hub = BuildHub(configStore: store);

        Assert.True(hub.Slideshow.Enabled);
        Assert.Equal(60, hub.Slideshow.IntervalSec);
        Assert.True(hub.Slideshow.Shuffle);
        Assert.False(hub.Slideshow.FinishVideos);
    }

    [Fact]
    public void Slideshow_defaults_off_on_a_settings_file_that_predates_it()
    {
        var hub = BuildHub(configStore: new InMemoryConfigStore());

        Assert.False(hub.Slideshow.Enabled);
        Assert.Equal(TryxSlideshowConfig.DefaultIntervalSec, hub.Slideshow.IntervalSec);
        Assert.False(hub.Slideshow.Shuffle);
        Assert.True(hub.Slideshow.FinishVideos);
    }

    [Fact]
    public void SetSlideshow_persists_the_clamped_settings()
    {
        var store = new InMemoryConfigStore();
        var hub = BuildHub(configStore: store);

        hub.SetSlideshow(new TryxSlideshowConfig { Enabled = true, IntervalSec = 1, Shuffle = true, FinishVideos = false });

        Assert.True(store.Load().Tryx.SlideshowEnabled);
        Assert.Equal(TryxSlideshowConfig.MinIntervalSec, store.Load().Tryx.SlideshowIntervalSec);
        Assert.True(store.Load().Tryx.SlideshowShuffle);
        Assert.False(store.Load().Tryx.SlideshowFinishVideos);
        Assert.Equal(TryxSlideshowConfig.MinIntervalSec, hub.Slideshow.IntervalSec);
    }

    [Fact]
    public void SendHeartbeatTick_starts_the_slideshow_on_the_first_custom_clip_when_enabled_over_a_preset()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport { AvailableCustomMediaFilenames = ["a.mp4", "b.mp4"] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording, configStore: store);
        hub.EnsureConnected();
        hub.SetPreset(TryxRkProtocol.PresetMediaFile(1));
        recording.Writes.Clear();

        hub.SetSlideshow(new TryxSlideshowConfig { Enabled = true, IntervalSec = 10 });
        hub.SendHeartbeatTick();

        Assert.Equal("a.mp4", hub.State.CurrentMedia);
        Assert.True(hub.State.CurrentMediaIsCustom);
        Assert.Equal("a.mp4", store.Load().Tryx.CurrentMedia);
        Assert.Contains(recording.Writes, w => w.AsSpan().SequenceEqual(TryxRkProtocol.BuildPreset("a.mp4", true, hub.State.Brightness)));
    }

    [Fact]
    public void SendHeartbeatTick_holds_a_custom_clip_for_the_interval_before_advancing()
    {
        var recording = new RecordingTransport { AvailableCustomMediaFilenames = ["a.mp4", "b.mp4"] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();
        hub.SelectCustomMedia("a.mp4");
        hub.SetSlideshow(new TryxSlideshowConfig { Enabled = true, IntervalSec = 3600 });
        recording.Writes.Clear();

        hub.SendHeartbeatTick();

        Assert.Equal("a.mp4", hub.State.CurrentMedia);
        Assert.DoesNotContain(recording.Writes, w => w.AsSpan().SequenceEqual(TryxRkProtocol.BuildPreset("b.mp4", true, hub.State.Brightness)));
    }

    [Fact]
    public void SendHeartbeatTick_leaves_the_panel_alone_while_the_slideshow_is_off()
    {
        var recording = new RecordingTransport { AvailableCustomMediaFilenames = ["a.mp4", "b.mp4"] };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();
        hub.SetPreset(TryxRkProtocol.PresetMediaFile(1));
        recording.Writes.Clear();

        hub.SendHeartbeatTick();

        Assert.False(hub.State.CurrentMediaIsCustom);
        Assert.DoesNotContain(recording.Writes, w => Encoding.UTF8.GetString(w).Contains("a.mp4"));
    }

    [Fact]
    public void SetBrightness_persists_brightness()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();

        hub.SetBrightness(55);

        Assert.Equal(55, store.Load().Tryx.Brightness);
    }

    [Fact]
    public void SetBrightness_writes_rk_brightness_frame()
    {
        var store = new InMemoryConfigStore();
        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);
        hub.EnsureConnected();
        recording.Writes.Clear();

        hub.SetBrightness(42);

        Assert.Equal(TryxRkProtocol.BuildConfig(true, 42), Assert.Single(recording.Writes));
    }

    [Fact]
    public void EnsureConnected_sends_persisted_brightness_as_rk_frame()
    {
        var store = new InMemoryConfigStore();
        // Empty overlay items keep ApplyInitialConfig's write to just the brightness
        // frame; the overlay-frame write on connect is covered separately below.
        store.Update(s => { s.Tryx.Brightness = 80; s.Tryx.OverlayItems = []; });

        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);

        hub.EnsureConnected();

        // ApplyInitialConfig writes the brightness config, then requests device_info to learn
        // the panel serial (the media list is fetched from the heartbeat once the serial lands).
        Assert.Equal(2, recording.Writes.Count);
        Assert.Equal(TryxRkProtocol.BuildConfig(true, 80), recording.Writes[0]);
        Assert.Equal(TryxRkProtocol.BuildGetDeviceInfo(), recording.Writes[1]);
    }

    [Fact]
    public void EnsureConnected_also_sends_the_overlay_frame_when_items_are_configured()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Tryx.OverlayItems = [new TryxOverlaySensorItemSettings { SensorId = "s1", Device = "cpu", Label = "CPU Temp" }]);

        var recording = new RecordingTransport();
        var hub = BuildHub(
            discovery: new StubDiscovery(),
            transportFactory: _ => recording,
            configStore: store);

        hub.EnsureConnected();

        // config + device_info request + overlay frame (the media-list fetch is sent later, from
        // the heartbeat, once the serial reply lands - not during this connect).
        Assert.Equal(3, recording.Writes.Count);
    }

    // ── Preset availability ──

    [Fact]
    public void AvailableMediaIds_reflects_the_connected_transport()
    {
        var recording = new RecordingTransport { AvailableMediaIds = new[] { "default_01", "default_02" } };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();

        var presets = TryxRoutes.ResolveAvailablePresets(hub.AvailableMediaIds);

        Assert.Equal(2, presets.Count);
        Assert.Equal("default_01", presets[0].Id);
        Assert.Equal("default_02", presets[1].Id);
    }

    [Fact]
    public void AvailableMediaIds_falls_back_to_the_first_six_before_the_panel_reports_any()
    {
        var recording = new RecordingTransport();
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();

        var presets = TryxRoutes.ResolveAvailablePresets(hub.AvailableMediaIds);

        Assert.Equal(6, presets.Count);
    }

    [Fact]
    public void AvailableMediaIds_is_empty_when_disconnected()
    {
        var hub = BuildHub();

        Assert.Empty(hub.AvailableMediaIds);
    }

    // ── Task 2: tryx.status overlay ──

    [Fact]
    public void Overlay_getter_reflects_the_setter()
    {
        var hub = BuildHub();
        var ov = new TryxOverlayConfig
        {
            Items = [new TryxOverlaySensorItem { SensorId = "s1", Device = "cpu", Label = "CPU Temp" }],
            Color = "#aabbcc",
            Align = "center",
        };

        hub.SetOverlay(ov);

        Assert.Equal("#aabbcc", hub.Overlay.Color);
        Assert.Equal("center", hub.Overlay.Align);
        Assert.Single(hub.Overlay.Items);
        Assert.Equal("CPU Temp", hub.Overlay.Items[0].Label);
    }

    [Fact]
    public void Hub_overlay_reflects_persisted_settings()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Tryx.OverlayItems =
            [
                new TryxOverlaySensorItemSettings { SensorId = "s1", Device = "gpu", Label = "GPU Temp" },
                new TryxOverlaySensorItemSettings { SensorId = "s2", Device = "cpu", Label = "CPU Usage" },
            ];
            s.Tryx.OverlayColor = "#112233";
            s.Tryx.OverlayAlign = "right";
        });
        var hub = BuildHub(configStore: store);

        Assert.Equal("#112233", hub.Overlay.Color);
        Assert.Equal("right", hub.Overlay.Align);
        Assert.Equal(2, hub.Overlay.Items.Count);
        Assert.Equal("GPU Temp", hub.Overlay.Items[0].Label);
        Assert.Equal("CPU Usage", hub.Overlay.Items[1].Label);
    }

    // ── Task 3: Sensor mapping ──

    [Fact]
    public void BuildLiveSensorJson_uses_gpu_temp_fallback_when_no_core_sensor()
    {
        // GPU with a temperature sensor NOT named "Core" - should still be read.
        var gpuSensors = new List<HardwareSensor>
        {
            MakeSensor("GPU Package", "Temperature", 72f),
            MakeSensor("GPU Core", "Load", 55f),
        };
        var sensors = new StubSensors { GpuSensors = gpuSensors };
        var hub = BuildHub(sensors: sensors);

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(72, doc.RootElement.GetProperty("gpu").GetProperty("temperature").GetInt32());
    }

    [Fact]
    public void BuildLiveSensorJson_prefers_gpu_core_temp_over_fallback()
    {
        // When "GPU Core" exists it should be preferred over any other Temperature sensor.
        var gpuSensors = new List<HardwareSensor>
        {
            MakeSensor("GPU Package", "Temperature", 80f),
            MakeSensor("GPU Core", "Temperature", 65f),
        };
        var sensors = new StubSensors { GpuSensors = gpuSensors };
        var hub = BuildHub(sensors: sensors);

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(65, doc.RootElement.GetProperty("gpu").GetProperty("temperature").GetInt32());
    }

    [Fact]
    public void BuildLiveSensorJson_uses_cpu_memory_clock_for_memory_speed()
    {
        // AMD-style: memory clock lives in CPU Clock sensors named "Memory".
        var cpuSensors = new List<HardwareSensor>
        {
            MakeSensor("Memory", "Clock", 3600f),
            MakeSensor("CPU Total", "Load", 30f),
        };
        var sensors = new StubSensors { CpuSensors = cpuSensors };
        var hub = BuildHub(sensors: sensors);

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(3600, doc.RootElement.GetProperty("memory").GetProperty("speed").GetInt32());
    }

    [Fact]
    public void BuildLiveSensorJson_uses_mobo_memory_clock_when_cpu_has_none()
    {
        var moboSensors = new List<HardwareSensor>
        {
            MakeSensor("Memory Clock", "Clock", 2400f),
        };
        var sensors = new StubSensors { MotherboardSensors = moboSensors };
        var hub = BuildHub(sensors: sensors);

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(2400, doc.RootElement.GetProperty("memory").GetProperty("speed").GetInt32());
    }

    [Fact]
    public void BuildLiveSensorJson_memory_speed_is_zero_when_no_clock_sensor()
    {
        var hub = BuildHub(sensors: new StubSensors());

        var json = hub.BuildLiveSensorJsonForTest();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("memory").GetProperty("speed").GetInt32());
    }

    // ── Session-accurate used-bytes + capacity ──

    [Fact]
    public void MediaUsedBytes_syncs_from_the_transport_after_a_version_bump()
    {
        var recording = new RecordingTransport
        {
            MediaFileSizes = new Dictionary<string, long> { ["a.mp4"] = 100, ["b.mp4"] = 200 },
            MediaListVersion = 1,
        };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);

        hub.EnsureConnected();

        Assert.Equal(300, hub.MediaUsedBytes);
    }

    [Fact]
    public void MediaUsedBytes_does_not_resync_until_the_version_bumps_again()
    {
        var recording = new RecordingTransport
        {
            MediaFileSizes = new Dictionary<string, long> { ["a.mp4"] = 100 },
            MediaListVersion = 1,
        };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();
        Assert.Equal(100, hub.MediaUsedBytes);

        // A later mutation of the transport's map with no version bump (no fresh panel
        // push) must not be picked up - only RecordMediaDeleted/an upload changes the total.
        recording.MediaFileSizes = new Dictionary<string, long> { ["a.mp4"] = 100, ["b.mp4"] = 900 };

        Assert.Equal(100, hub.MediaUsedBytes);
    }

    [Fact]
    public async Task MediaUsedBytes_reflects_a_session_upload_before_the_panel_repushes()
    {
        var recording = new RecordingTransport();
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();
        Assert.Equal(0, hub.MediaUsedBytes);

        var path = WriteTempFile(500);
        try
        {
            var ok = await hub.InstallLocalMediaAsync(path, "custom.mp4.h264_2240x1080", CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(500, hub.MediaUsedBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MediaUsedBytes_reflects_a_session_delete()
    {
        var recording = new RecordingTransport
        {
            MediaFileSizes = new Dictionary<string, long> { ["a.mp4"] = 1000 },
            MediaListVersion = 1,
        };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();
        Assert.Equal(1000, hub.MediaUsedBytes);

        hub.RecordMediaDeleted("a.mp4");

        Assert.Equal(0, hub.MediaUsedBytes);
    }

    [Fact]
    public async Task InstallLocalMediaAsync_rejects_a_transfer_that_would_exceed_capacity_with_padding()
    {
        var baseline = TryxPanoramaHub.MediaCapacityBytes - TryxPanoramaHub.MediaCapacityPaddingBytes - 100;
        var recording = new RecordingTransport
        {
            MediaFileSizes = new Dictionary<string, long> { ["bulk.mp4"] = baseline },
            MediaListVersion = 1,
        };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();

        var path = WriteTempFile(200);
        try
        {
            var ok = await hub.InstallLocalMediaAsync(path, "new.mp4.h264_2240x1080", CancellationToken.None);

            Assert.False(ok);
            Assert.Equal(baseline, hub.MediaUsedBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InstallLocalMediaAsync_accepts_a_transfer_that_fits_within_capacity_with_padding()
    {
        var baseline = TryxPanoramaHub.MediaCapacityBytes - TryxPanoramaHub.MediaCapacityPaddingBytes - 100;
        var recording = new RecordingTransport
        {
            MediaFileSizes = new Dictionary<string, long> { ["bulk.mp4"] = baseline },
            MediaListVersion = 1,
        };
        var hub = BuildHub(discovery: new StubDiscovery(), transportFactory: _ => recording);
        hub.EnsureConnected();

        var path = WriteTempFile(50);
        try
        {
            var ok = await hub.InstallLocalMediaAsync(path, "new.mp4.h264_2240x1080", CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(baseline + 50, hub.MediaUsedBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempFile(int lengthBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tryx-hub-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[lengthBytes]);
        return path;
    }
}
