using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

internal sealed class StubSensorProvider : ISensorProvider
{
    public int CpuSensorReads { get; private set; }
    public int MemorySensorReads { get; private set; }
    public int ExtrasReads { get; private set; }

    public string GetCpuModel() => "test-cpu";
    public IReadOnlyList<HardwareSensor> GetCpuSensors()
    {
        CpuSensorReads++;
        return new[]
        {
            new HardwareSensor
            {
                Id = "cpu/load",
                Name = "CPU Total",
                Type = "Load",
                Value = 42,
                Units = "%",
                Formatted = "42%",
                Parent = new SensorParent { Id = "cpu", Name = "test-cpu" },
            },
        };
    }
    public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
    public List<GpuReadout> Gpus { get; init; } = new();
    public List<HardwareComponent> MemoryModules { get; init; } = new();
    public IReadOnlyList<string> GetGpuModels() => Gpus.Select(g => g.Name).ToList();
    public IReadOnlyList<HardwareSensor> GetGpuSensors() => Gpus.SelectMany(g => g.Sensors).ToList();
    public IReadOnlyList<GpuReadout> GetGpus() => Gpus;
    public IReadOnlyList<HardwareSensor> GetMemorySensors()
    {
        MemorySensorReads++;
        return new[]
        {
            new HardwareSensor
            {
                Id = "mem/usage",
                Name = "Memory Usage",
                Type = "Load",
                Value = 30,
                Units = "%",
                Formatted = "30%",
                Parent = new SensorParent { Id = "memory", Name = "Memory" },
            },
        };
    }
    public string GetMemoryTotalFormatted() => "16 GB";
    public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) =>
        new Dictionary<string, StorageComponent>();
    public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
    public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
    public List<HardwareSensor> MotherboardSensors { get; init; } = new();
    // Fresh instances per call, matching LibreHardwareSensorProvider.
    public IReadOnlyList<HardwareSensor> GetMotherboardSensors() =>
        MotherboardSensors.Select(s => s.Clone()).ToList();
    public string GetMotherboardModel() => "test-mobo";
    public SensorExtras GetSensorExtras()
    {
        ExtrasReads++;
        return new SensorExtras
        {
            Batteries =
            {
                new HardwareComponent
                {
                    Id = "battery/0",
                    Name = "Test Battery",
                    Sensors = new List<HardwareSensor>
                    {
                        new()
                        {
                            Id = "battery/0/charge",
                            Name = "Charge Level",
                            Type = "Level",
                            Value = 80,
                            Units = "%",
                            Formatted = "80%",
                            Parent = new SensorParent { Id = "battery/0", Name = "Test Battery" },
                        },
                    },
                },
            },
            MemoryModules = new List<HardwareComponent>(MemoryModules),
        };
    }
    public string GetOsVersion() => "test-os";
    public string GetRamBrandModel() => "";
    public string GetStorageBrandModel() => "";
    public void SetPollingRate(int pollingRate) { }
    public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class StubPerformanceProvider : IPerformanceProvider
{
    public Task<PerformanceSnapshot> SampleAsync(CancellationToken ct = default) =>
        Task.FromResult(new PerformanceSnapshot { Cpu = 5.0, Memory = 30.0, Gpu = 2.0, Source = "test" });
}

internal sealed class TrackingFpsProvider : IFpsProvider
{
    private readonly HashSet<string> _demands = new(StringComparer.Ordinal);

    public int StartTransitions { get; private set; }
    public int StopTransitions { get; private set; }
    public int ComponentReads { get; private set; }
    public bool Running { get; private set; }

    public void SetDemand(string source, bool wanted)
    {
        if (wanted) _demands.Add(source);
        else _demands.Remove(source);

        var wantsCapture = _demands.Count > 0;
        if (wantsCapture && !Running)
        {
            Running = true;
            StartTransitions++;
        }
        else if (!wantsCapture && Running)
        {
            Running = false;
            StopTransitions++;
        }
    }

    public bool TryReadCurrentFps(out double fps)
    {
        fps = 0;
        return false;
    }

    public HardwareComponent GetComponent()
    {
        ComponentReads++;
        return new HardwareComponent
        {
            Id = "fps",
            Name = "FPS",
            Sensors = new List<HardwareSensor>
            {
                new()
                {
                    Id = "fps/current",
                    Name = "FPS",
                    Type = "Framerate",
                    Value = 60,
                    Units = "fps",
                    Formatted = "60 fps",
                    Parent = new SensorParent { Id = "fps", Name = "FPS" },
                },
            },
        };
    }

    public void Dispose() => SetDemand("monitoring", false);
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow += amount;
}

/// <summary>
/// End-to-end coverage of the monitoring broadcast path: a simulated subscriber
/// turns a topic "live", the broadcaster gathers data from its provider stack
/// (all stubs for test determinism), and assembles + fires a frame through
/// <see cref="MultiplexHub"/>. Catches regressions in:
///   - DI-injected provider contracts (a stub provider returning null / empty
///     in a new field would throw here before it ships)
///   - Topic-first-subscriber event wiring
///   - JSON serialisation of MonitoringFrame / ProcessFrame / NetworkFrame via
///     the AOT source-gen context (an unregistered type serialises as "{}"
///     silently in AOT; the test asserts a non-trivial payload)
///   - `Tick()` internal gating logic (an early-return regression would skip
///     the broadcast even with a subscriber)
///
/// The test runs purely in-process with stub providers -- no hardware, no
/// Kestrel, no sockets -- by using MultiplexHub's internal test-subscription
/// hook and OnBroadcastForTest event. That keeps the test to <50 ms while still
/// exercising the real class graph the broadcaster uses in production.
/// </summary>
public class MonitoringBroadcastTests
{
    /// <summary>Fan provider exposing one hub-owned probe plus one motherboard source that must be filtered out.</summary>
    private sealed class StubHubFanProvider : Nexus.Service.Cooling.IFanControlProvider
    {
        public IReadOnlyList<Nexus.Service.Models.Cooling.FanChannel> GetFanChannels() => System.Array.Empty<Nexus.Service.Models.Cooling.FanChannel>();

        public IReadOnlyList<Nexus.Service.Models.Cooling.TemperatureSource> GetTemperatureSources() => new[]
        {
            new Nexus.Service.Models.Cooling.TemperatureSource
            {
                Id = "board-cpu", Name = "CPU", Category = "Motherboard", Value = 53f,
            },
            new Nexus.Service.Models.Cooling.TemperatureSource
            {
                Id = "np50:A:port1:dev1:temp", Name = "FP12 probe (Port 1 #1)", Category = "Hub",
                Value = 35f, DeviceId = "np50:A", DeviceName = "HYTE NP50",
            },
        };

        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<Nexus.Service.Models.Cooling.FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds,
            IProgress<Nexus.Service.Models.Cooling.FanCalibrationProgress> progress,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Nexus.Service.Models.Cooling.FanCalibration>>(System.Array.Empty<Nexus.Service.Models.Cooling.FanCalibration>());
    }

    private static MonitoringBroadcaster BuildBroadcaster(
        MultiplexHub hub,
        StubSensorProvider? sensors = null,
        TrackingFpsProvider? fps = null,
        TimeProvider? timeProvider = null,
        ProcessMonitor? processes = null,
        Nexus.Service.Cooling.IFanControlProvider? fans = null,
        Nexus.Service.Persistence.IConfigStore? config = null)
    {
        // Stub providers for every dependency so Tick can run end-to-end
        // without touching hardware.
        sensors ??= new StubSensorProvider();
        fps ??= new TrackingFpsProvider();
        var network = new StubNetworkProvider();
        var screenTime = new StubScreenTimeProvider();
        var performance = new StubPerformanceProvider();
        processes ??= new ProcessMonitor(hub);
        var gpuProcesses = new GpuProcessMonitor(hub, new InMemoryConfigStore());
        var volume = new StubVolumeProvider();
        return timeProvider is null
            ? new MonitoringBroadcaster(sensors, processes, gpuProcesses, network, performance, screenTime, volume, fps, hub, fans, config)
            : new MonitoringBroadcaster(sensors, processes, gpuProcesses, network, performance, screenTime, volume, fps, hub, timeProvider, fans, config);
    }

    [Fact]
    public async Task Tick_WithNoSubscribers_ShortCircuits_NoBroadcast()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        await broadcaster.Tick(CancellationToken.None);

        Assert.Empty(captured);
    }

    [Fact]
    public async Task Tick_WithMonitoringSubscriber_BroadcastsCompositeFrame()
    {
        var hub = new MultiplexHub();
        var fps = new TrackingFpsProvider();
        var broadcaster = BuildBroadcaster(hub, fps: fps);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("monitoring");
        Assert.True(hub.TopicHasSubscribers("monitoring"));

        await broadcaster.Tick(CancellationToken.None);

        // Composite "monitoring" topic must have been broadcast and carry a
        // non-empty envelope with the expected wrapper shape.
        var monitoring = captured.FirstOrDefault(c => c.Topic == "monitoring");
        Assert.NotEqual(default, monitoring);
        var json = System.Text.Encoding.UTF8.GetString(monitoring.Payload);
        Assert.StartsWith("{\"t\":\"monitoring\"", json);
        Assert.Contains("\"d\":{", json);
        // AOT-safety guard: if MonitoringFrame's TypeInfo isn't registered in
        // AppJsonContext the payload becomes `"d":{}` and this assertion fails.
        Assert.DoesNotContain("\"d\":{}", json);
        Assert.DoesNotContain("fps", captured.Select(c => c.Topic));
        Assert.Equal(0, fps.StartTransitions);
        Assert.Equal(0, fps.ComponentReads);
    }

    [Fact]
    public async Task Tick_GpuTopic_PartitionsSensorsPerGpu_DiscreteFirst()
    {
        // Platform enumerates the integrated GPU first; the broadcast must order
        // discrete first and give each component only its own GPU's sensors.
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider
        {
            Gpus =
            {
                new GpuReadout
                {
                    Id = "gpu/intel", Name = "Intel UHD Graphics", Vendor = "intel", Integrated = true,
                    Sensors =
                    {
                        new HardwareSensor
                        {
                            Id = "intel/load", Name = "GPU Core", Type = "Load", Value = 10, Units = "%",
                            Formatted = "10%", Parent = new SensorParent { Id = "intel", Name = "Intel UHD Graphics" },
                        },
                    },
                },
                new GpuReadout
                {
                    Id = "gpu/nvidia", Name = "NVIDIA GeForce RTX 4090", Vendor = "nvidia", Integrated = false,
                    Sensors =
                    {
                        new HardwareSensor
                        {
                            Id = "nvidia/load", Name = "GPU Core", Type = "Load", Value = 80, Units = "%",
                            Formatted = "80%", Parent = new SensorParent { Id = "nvidia", Name = "NVIDIA GeForce RTX 4090" },
                        },
                    },
                },
            },
        };
        var broadcaster = BuildBroadcaster(hub, sensors);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("gpu");
        await broadcaster.Tick(CancellationToken.None);

        var gpu = captured.FirstOrDefault(c => c.Topic == "gpu");
        Assert.NotEqual(default, gpu);

        using var doc = System.Text.Json.JsonDocument.Parse(gpu.Payload);
        var arr = doc.RootElement.GetProperty("d");
        Assert.Equal(2, arr.GetArrayLength());

        // Discrete (NVIDIA) first, regardless of enumeration order.
        var first = arr[0];
        Assert.Equal("NVIDIA GeForce RTX 4090", first.GetProperty("name").GetString());
        Assert.Equal("nvidia", first.GetProperty("vendor").GetString());
        Assert.False(first.GetProperty("integrated").GetBoolean());
        var firstIds = first.GetProperty("sensors").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("nvidia/load", firstIds);
        Assert.DoesNotContain("intel/load", firstIds); // no cross-GPU leakage

        var second = arr[1];
        Assert.Equal("Intel UHD Graphics", second.GetProperty("name").GetString());
        Assert.True(second.GetProperty("integrated").GetBoolean());
        var secondIds = second.GetProperty("sensors").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("intel/load", secondIds);
        Assert.DoesNotContain("nvidia/load", secondIds);
    }

    [Fact]
    public async Task Tick_WithFpsSubscriber_StartsAndBroadcastsFpsTopicOnly()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var fps = new TrackingFpsProvider();
        var broadcaster = BuildBroadcaster(hub, sensors, fps);
        var captured = new List<(string Topic, string Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, System.Text.Encoding.UTF8.GetString(payload.Span)));

        using (var sub = hub.AddTestSubscription("fps"))
        {
            Assert.True(fps.Running);
            await broadcaster.Tick(CancellationToken.None);
        }

        Assert.Contains(captured, c => c.Topic == "fps" && c.Payload.Contains("\"FPS\""));
        Assert.DoesNotContain("monitoring", captured.Select(c => c.Topic));
        Assert.DoesNotContain("cpu", captured.Select(c => c.Topic));
        Assert.Equal(0, sensors.CpuSensorReads);
        Assert.Equal(1, fps.StartTransitions);
        Assert.Equal(1, fps.StopTransitions);
        Assert.Equal(1, fps.ComponentReads);
    }

    [Fact]
    public async Task Tick_WithCompositeAndSensorSubscribers_ReusesSensorPayload()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var broadcaster = BuildBroadcaster(hub, sensors);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        using var monitoring = hub.AddTestSubscription("monitoring");
        using var cpu = hub.AddTestSubscription("cpu");
        using var memory = hub.AddTestSubscription("memory");

        await broadcaster.Tick(CancellationToken.None);

        Assert.Contains("monitoring", captured);
        Assert.Contains("cpu", captured);
        Assert.Contains("memory", captured);
        Assert.Equal(1, sensors.CpuSensorReads);
        Assert.Equal(1, sensors.MemorySensorReads);
    }

    [Fact]
    public async Task Tick_WithCompositeSubscriberOnly_DoesNotBroadcastSummary()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        using var monitoring = hub.AddTestSubscription("monitoring");
        await broadcaster.Tick(CancellationToken.None);

        Assert.Contains("monitoring", captured);
        Assert.DoesNotContain("summary", captured);
    }

    [Fact]
    public async Task Tick_WithSummarySubscriber_BroadcastsSummaryTopicWithoutDoubleReadingSensors()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var broadcaster = BuildBroadcaster(hub, sensors);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var monitoring = hub.AddTestSubscription("monitoring");
        using var summary = hub.AddTestSubscription("summary");
        await broadcaster.Tick(CancellationToken.None);

        var summaryPayload = captured.FirstOrDefault(c => c.Topic == "summary");
        Assert.NotEqual(default, summaryPayload);

        using var doc = System.Text.Json.JsonDocument.Parse(summaryPayload.Payload);
        var sensorIds = doc.RootElement.GetProperty("d").GetProperty("sensors").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("summary/cpu-usage", sensorIds);
        Assert.Contains("summary/memory-usage", sensorIds);

        // Composite already reads cpu/memory sensors this tick; the summary
        // derivation must reuse that data instead of reading them again.
        Assert.Equal(1, sensors.CpuSensorReads);
        Assert.Equal(1, sensors.MemorySensorReads);
    }

    /// <summary>Motherboard fan channels keyed the way LibreHardwareMonitor keys them: the channel id is the PWM control sensor, the tach is a separate id.</summary>
    private sealed class StubMotherboardFanProvider : Nexus.Service.Cooling.IFanControlProvider
    {
        public IReadOnlyList<Nexus.Service.Models.Cooling.FanChannel> GetFanChannels() => new[]
        {
            new Nexus.Service.Models.Cooling.FanChannel
            {
                Id = "/lpc/nct6797d/control/0", Name = "Fan #1", RpmSensorId = "/lpc/nct6797d/fan/0",
            },
            new Nexus.Service.Models.Cooling.FanChannel
            {
                Id = "/lpc/nct6797d/control/1", Name = "Fan #2", RpmSensorId = "/lpc/nct6797d/fan/1",
            },
        };

        public IReadOnlyList<Nexus.Service.Models.Cooling.TemperatureSource> GetTemperatureSources() =>
            System.Array.Empty<Nexus.Service.Models.Cooling.TemperatureSource>();
        public IReadOnlyList<Nexus.Service.Models.Cooling.TemperatureSource> GetDeviceTemperatureSources() =>
            System.Array.Empty<Nexus.Service.Models.Cooling.TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<Nexus.Service.Models.Cooling.FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds,
            IProgress<Nexus.Service.Models.Cooling.FanCalibrationProgress> progress,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Nexus.Service.Models.Cooling.FanCalibration>>(System.Array.Empty<Nexus.Service.Models.Cooling.FanCalibration>());
    }

    private static StubSensorProvider MotherboardFanSensors() => new()
    {
        MotherboardSensors =
        {
            new HardwareSensor
            {
                Id = "/lpc/nct6797d/fan/0", Name = "Fan #1", Type = "Fan", Value = 900,
                Units = "RPM", Formatted = "900 RPM",
                Parent = new SensorParent { Id = "motherboard", Name = "test-mobo" },
            },
            new HardwareSensor
            {
                Id = "/lpc/nct6797d/fan/1", Name = "Fan #2", Type = "Fan", Value = 1200,
                Units = "RPM", Formatted = "1200 RPM",
                Parent = new SensorParent { Id = "motherboard", Name = "test-mobo" },
            },
        },
    };

    private static List<string> BroadcastMotherboardSensorNames(byte[] payload)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("d").GetProperty("sensors").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString() ?? "").ToList();
    }

    [Fact]
    public async Task Tick_MotherboardTopic_CarriesRenamedFanHeadersOntoTheirTachSensors()
    {
        var hub = new MultiplexHub();
        var config = new InMemoryConfigStore();
        config.Update(s => s.Cooling.FanNames["/lpc/nct6797d/control/0"] = "Radiator Fans");
        var broadcaster = BuildBroadcaster(
            hub, MotherboardFanSensors(), fans: new StubMotherboardFanProvider(), config: config);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var motherboard = hub.AddTestSubscription("motherboard");
        await broadcaster.Tick(CancellationToken.None);

        var payload = captured.First(c => c.Topic == "motherboard");
        Assert.Equal(new[] { "Radiator Fans", "Fan #2" }, BroadcastMotherboardSensorNames(payload.Payload));
    }

    [Fact]
    public async Task Tick_MotherboardTopic_KeepsHardwareNamesWhenNothingIsRenamed()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(
            hub, MotherboardFanSensors(), fans: new StubMotherboardFanProvider(), config: new InMemoryConfigStore());
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var motherboard = hub.AddTestSubscription("motherboard");
        await broadcaster.Tick(CancellationToken.None);

        var payload = captured.First(c => c.Topic == "motherboard");
        Assert.Equal(new[] { "Fan #1", "Fan #2" }, BroadcastMotherboardSensorNames(payload.Payload));
    }

    [Fact]
    public async Task Tick_MotherboardTopic_RenameDoesNotPersistIntoTheNextTick()
    {
        // Each tick rebuilds the map from settings, so clearing a rename takes effect on the
        // next broadcast rather than sticking until a restart.
        var hub = new MultiplexHub();
        var config = new InMemoryConfigStore();
        config.Update(s => s.Cooling.FanNames["/lpc/nct6797d/control/0"] = "Radiator Fans");
        var broadcaster = BuildBroadcaster(
            hub, MotherboardFanSensors(), fans: new StubMotherboardFanProvider(), config: config);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var motherboard = hub.AddTestSubscription("motherboard");
        await broadcaster.Tick(CancellationToken.None);
        config.Update(s => s.Cooling.FanNames.Clear());
        await broadcaster.Tick(CancellationToken.None);

        var payloads = captured.Where(c => c.Topic == "motherboard").ToList();
        Assert.Equal(new[] { "Radiator Fans", "Fan #2" }, BroadcastMotherboardSensorNames(payloads[0].Payload));
        Assert.Equal(new[] { "Fan #1", "Fan #2" }, BroadcastMotherboardSensorNames(payloads[1].Payload));
    }

    [Fact]
    public async Task Tick_WithScreenTimeSubscriber_BroadcastsScreenTimeTopicOnly()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        using var sub = hub.AddTestSubscription("screentime");
        await broadcaster.Tick(CancellationToken.None);

        // screentime on its own must NOT trigger LHM sensor gathering or the
        // composite frame. Regression here wastes CPU on users who only want
        // focus tracking.
        Assert.Contains("screentime", captured);
        Assert.DoesNotContain("monitoring", captured);
        Assert.DoesNotContain("cpu", captured);
        Assert.DoesNotContain("processes", captured);
    }

    [Fact]
    public async Task Tick_WithScreenTimeSubscriber_ThrottlesScreenTimeToTenSeconds()
    {
        var hub = new MultiplexHub();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero));
        var broadcaster = BuildBroadcaster(hub, timeProvider: time);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        using var sub = hub.AddTestSubscription("screentime");

        await broadcaster.Tick(CancellationToken.None);
        await broadcaster.Tick(CancellationToken.None);

        Assert.Equal(1, captured.Count(t => t == "screentime"));

        time.Advance(TimeSpan.FromSeconds(9));
        await broadcaster.Tick(CancellationToken.None);

        Assert.Equal(1, captured.Count(t => t == "screentime"));

        time.Advance(TimeSpan.FromSeconds(1));
        await broadcaster.Tick(CancellationToken.None);

        Assert.Equal(2, captured.Count(t => t == "screentime"));
    }

    [Fact]
    public async Task Tick_SubscribeThenUnsubscribe_ReturnsToIdle()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        var sub = hub.AddTestSubscription("monitoring");
        await broadcaster.Tick(CancellationToken.None);
        Assert.NotEmpty(captured);

        captured.Clear();
        sub.Dispose();

        Assert.False(hub.TopicHasSubscribers("monitoring"));
        await broadcaster.Tick(CancellationToken.None);
        Assert.Empty(captured);
    }

    [Fact]
    public async Task Tick_WithExtrasSubscriber_BroadcastsExtrasTopicOnly()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var broadcaster = BuildBroadcaster(hub, sensors);
        var captured = new List<(string Topic, string Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, System.Text.Encoding.UTF8.GetString(payload.Span)));

        using var sub = hub.AddTestSubscription("extras");
        await broadcaster.Tick(CancellationToken.None);

        // Extras must broadcast under its own topic only and never piggy-back
        // on the composite "monitoring" frame -- other pages must stay free of
        // the detailed-tab payload.
        var extras = captured.FirstOrDefault(c => c.Topic == "extras");
        Assert.NotEqual(default, extras);
        Assert.StartsWith("{\"t\":\"extras\"", extras.Payload);
        Assert.Contains("\"batteries\"", extras.Payload);
        Assert.DoesNotContain("monitoring", captured.Select(c => c.Topic));
        Assert.DoesNotContain("cpu", captured.Select(c => c.Topic));
        Assert.Equal(1, sensors.ExtrasReads);
        Assert.Equal(0, sensors.CpuSensorReads);
    }

    [Fact]
    public async Task Tick_ExtrasCarriesCoolingHubProbes()
    {
        // Pins the wiring, not just the projection: hub probes reach the extras topic that the
        // monitoring sensor picker's Cooler category reads.
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub, fans: new StubHubFanProvider());
        var captured = new List<(string Topic, string Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, System.Text.Encoding.UTF8.GetString(payload.Span)));

        using var sub = hub.AddTestSubscription("extras");
        await broadcaster.Tick(CancellationToken.None);

        var extras = captured.First(c => c.Topic == "extras");
        Assert.Contains("HYTE NP50", extras.Payload);
        Assert.Contains("np50:A:port1:dev1:temp", extras.Payload);
        // The motherboard source has no DeviceId, so it must not be duplicated into coolers.
        Assert.DoesNotContain("board-cpu", extras.Payload);
    }

    [Fact]
    public async Task Tick_WithoutAFanProvider_StillBroadcastsExtras()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub);
        var captured = new List<(string Topic, string Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, System.Text.Encoding.UTF8.GetString(payload.Span)));

        using var sub = hub.AddTestSubscription("extras");
        await broadcaster.Tick(CancellationToken.None);

        Assert.Contains(captured, c => c.Topic == "extras");
    }

    [Fact]
    public async Task Tick_WithExtrasSubscriber_IncludesMemoryModules()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider
        {
            MemoryModules =
            {
                new HardwareComponent
                {
                    Id = "/memory/dimm/0",
                    Name = "Corsair - CMK16GX4M2B3200C16 (#0)",
                    Sensors = new List<HardwareSensor>
                    {
                        new()
                        {
                            Id = "/memory/dimm/0/temperature/0",
                            Name = "DIMM #0",
                            Type = "Temperature",
                            Value = 38,
                            Units = "°C",
                            Formatted = "38.0 °C",
                            Parent = new SensorParent { Id = "/memory/dimm/0", Name = "Corsair - CMK16GX4M2B3200C16 (#0)" },
                        },
                    },
                },
            },
        };
        var broadcaster = BuildBroadcaster(hub, sensors);
        var captured = new List<(string Topic, string Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, System.Text.Encoding.UTF8.GetString(payload.Span)));

        using var sub = hub.AddTestSubscription("extras");
        await broadcaster.Tick(CancellationToken.None);

        var extras = captured.FirstOrDefault(c => c.Topic == "extras");
        Assert.NotEqual(default, extras);
        Assert.Contains("\"memoryModules\"", extras.Payload);
        Assert.Contains("CMK16GX4M2B3200C16", extras.Payload);
    }

    [Fact]
    public async Task Tick_WithoutExtrasSubscriber_DoesNotGatherExtras()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var broadcaster = BuildBroadcaster(hub, sensors);

        // Composite + each per-domain topic, but never "extras". The broadcaster
        // must still skip extras gathering -- this is the contract that keeps
        // the Overview/CPU/Memory/Network/ScreenTime tabs flood-free.
        using var monitoring = hub.AddTestSubscription("monitoring");
        using var cpu = hub.AddTestSubscription("cpu");

        await broadcaster.Tick(CancellationToken.None);

        Assert.Equal(0, sensors.ExtrasReads);
    }

    [Fact]
    public async Task Tick_ProcessesTopic_CarriesStartedAtMs()
    {
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        processes.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = 1, Name = "app.exe", CpuPercent = 5, MemoryMb = 100, StartedAtMs = 123_456 },
            new ProcessInfo { Pid = 2, Name = "no-start-time.exe", CpuPercent = 1, MemoryMb = 10 },
        });
        var broadcaster = BuildBroadcaster(hub, processes: processes);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("processes");
        await broadcaster.Tick(CancellationToken.None);

        var frame = captured.Single(c => c.Topic == "processes");
        using var doc = System.Text.Json.JsonDocument.Parse(frame.Payload);
        var arr = doc.RootElement.GetProperty("d").GetProperty("processes");
        Assert.Equal(123_456, arr[0].GetProperty("startedAtMs").GetInt64());
        Assert.False(arr[1].TryGetProperty("startedAtMs", out _)); // null omitted on the wire
    }

    [Fact]
    public async Task Tick_ProcessesTopic_CarriesIsAppTrue_WhenAnyInstanceOfTheNameOwnsAWindow()
    {
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        processes.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = 1, Name = "app.exe", HasWindow = false },
            new ProcessInfo { Pid = 2, Name = "app.exe", HasWindow = true },
            new ProcessInfo { Pid = 3, Name = "background.exe", HasWindow = false },
        });
        var broadcaster = BuildBroadcaster(hub, processes: processes);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("processes");
        await broadcaster.Tick(CancellationToken.None);

        var frame = captured.Single(c => c.Topic == "processes");
        using var doc = System.Text.Json.JsonDocument.Parse(frame.Payload);
        var arr = doc.RootElement.GetProperty("d").GetProperty("processes");

        // Every row sharing the "app.exe" name reads isApp=true, since one
        // instance (pid 2) owns a window - the group, not the single pid,
        // decides the classification.
        Assert.True(arr[0].GetProperty("isApp").GetBoolean());
        Assert.True(arr[1].GetProperty("isApp").GetBoolean());
        Assert.False(arr[2].GetProperty("isApp").GetBoolean());
    }

    [Fact]
    public async Task Tick_ProcessesTopic_OmitsPublisherAndSigned_BeforeTheBackgroundResolveCompletes()
    {
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        processes.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = 999_999, Name = "unresolvable.exe" }, // not a live pid: path never resolves
        });
        var broadcaster = BuildBroadcaster(hub, processes: processes);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("processes");
        await broadcaster.Tick(CancellationToken.None);

        var frame = captured.Single(c => c.Topic == "processes");
        using var doc = System.Text.Json.JsonDocument.Parse(frame.Payload);
        var entry = doc.RootElement.GetProperty("d").GetProperty("processes")[0];

        Assert.False(entry.TryGetProperty("publisher", out _));
        Assert.False(entry.TryGetProperty("signed", out _));
        Assert.False(entry.GetProperty("isApp").GetBoolean());
    }

    [Fact]
    public async Task Tick_ProcessesTopic_CarriesPublisherAndSigned_OnceTheMetaCacheIsWarm()
    {
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        processes.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = 1, Name = "app.exe" },
        });
        processes.SeedResolvedPathForTest("app.exe", "/Applications/App.app/Contents/MacOS/app");
        processes.SeedProcessMetaForTest(
            "/Applications/App.app/Contents/MacOS/app", new ProcessMeta("Example Publisher", "signed"));
        var broadcaster = BuildBroadcaster(hub, processes: processes);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("processes");
        await broadcaster.Tick(CancellationToken.None);

        var frame = captured.Single(c => c.Topic == "processes");
        using var doc = System.Text.Json.JsonDocument.Parse(frame.Payload);
        var entry = doc.RootElement.GetProperty("d").GetProperty("processes")[0];

        Assert.Equal("Example Publisher", entry.GetProperty("publisher").GetString());
        Assert.Equal("signed", entry.GetProperty("signed").GetString());
    }

    [Fact]
    public async Task Tick_ProcessesTopic_StaysWellUnderTheSanityBound_WithPublisherAndSignedPopulated()
    {
        // Same 300-process shape as the uncapped-list test above, but with
        // every row carrying a resolved publisher/signed pair - the realistic
        // steady-state size once the meta cache has warmed up for every name.
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        const int count = 300;
        processes.SetProcessesForTest(Enumerable.Range(0, count)
            .Select(i => new ProcessInfo { Pid = i, Name = $"proc{i}", CpuPercent = 1, MemoryMb = 10 })
            .ToList());
        for (var i = 0; i < count; i++)
        {
            var path = $"/Applications/App{i}.app/Contents/MacOS/app{i}";
            processes.SeedResolvedPathForTest($"proc{i}", path);
            processes.SeedProcessMetaForTest(path, new ProcessMeta("Example Publisher Co.", "signed"));
        }
        var broadcaster = BuildBroadcaster(hub, processes: processes);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("processes");
        await broadcaster.Tick(CancellationToken.None);

        var frame = captured.Single(c => c.Topic == "processes");
        Assert.True(frame.Payload.Length < SanityFrameSizeBytes,
            $"processes frame at {count} entries with publisher/signed was {frame.Payload.Length} bytes, expected < {SanityFrameSizeBytes}");
    }

    // No WebSocketOptions size limit is configured anywhere in Program.cs
    // and no other constant caps a text frame's size, so this is a
    // regression-guard sanity bound (chosen well above any realistic
    // process-list payload), not an enforced system limit.
    private const int SanityFrameSizeBytes = 1024 * 1024;

    [Fact]
    public async Task Tick_ProcessesTopic_ShipsTheFullList_NotCappedAtTwentyFive()
    {
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        const int count = 300;
        processes.SetProcessesForTest(Enumerable.Range(0, count)
            .Select(i => new ProcessInfo { Pid = i, Name = $"proc{i}", CpuPercent = 1, MemoryMb = 10 })
            .ToList());
        var broadcaster = BuildBroadcaster(hub, processes: processes);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("processes");
        await broadcaster.Tick(CancellationToken.None);

        var frame = captured.Single(c => c.Topic == "processes");
        using var doc = System.Text.Json.JsonDocument.Parse(frame.Payload);
        Assert.Equal(count, doc.RootElement.GetProperty("d").GetProperty("processes").GetArrayLength());
        Assert.True(frame.Payload.Length < SanityFrameSizeBytes,
            $"processes frame at {count} entries was {frame.Payload.Length} bytes, expected < {SanityFrameSizeBytes}");
    }

    [Fact]
    public async Task Tick_CompositeMonitoringTopic_AlsoEmbedsTheFullProcessList_StaysSmall()
    {
        // BuildProcessFrame feeds both the dedicated "processes" topic and
        // the composite "monitoring" frame (every general dashboard
        // subscriber) - the uncapped list ships to composite subscribers
        // too, so the size claim must hold there as well, not only for a
        // subscriber requesting "processes" alone.
        var hub = new MultiplexHub();
        var processes = new ProcessMonitor(hub);
        const int count = 300;
        processes.SetProcessesForTest(Enumerable.Range(0, count)
            .Select(i => new ProcessInfo { Pid = i, Name = $"proc{i}", CpuPercent = 1, MemoryMb = 10 })
            .ToList());
        var broadcaster = BuildBroadcaster(hub, processes: processes);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("monitoring");
        await broadcaster.Tick(CancellationToken.None);

        var frame = captured.Single(c => c.Topic == "monitoring");
        using var doc = System.Text.Json.JsonDocument.Parse(frame.Payload);
        Assert.Equal(count, doc.RootElement.GetProperty("d").GetProperty("processes").GetProperty("processes").GetArrayLength());
        Assert.True(frame.Payload.Length < SanityFrameSizeBytes,
            $"composite monitoring frame at {count} processes was {frame.Payload.Length} bytes, expected < {SanityFrameSizeBytes}");
    }

    [Fact]
    public void AddTestSubscription_FiresFirstSubscriberEvent()
    {
        var hub = new MultiplexHub();
        var firstTopics = new List<string>();
        hub.OnTopicFirstSubscriber += t => firstTopics.Add(t);

        using var s1 = hub.AddTestSubscription("processes");
        using var s2 = hub.AddTestSubscription("processes"); // second sub, same topic
        using var s3 = hub.AddTestSubscription("network");

        // First-subscriber event must fire exactly once per topic transition
        // from 0 -> 1 subscriber. Regression here would cause BeatsProvider-
        // style lazy subsystems to start twice or never.
        Assert.Equal(new[] { "processes", "network" }, firstTopics);
    }
}
