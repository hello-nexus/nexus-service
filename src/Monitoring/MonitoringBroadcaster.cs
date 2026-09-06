using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Cooling;
using Nexus.Service.Fps;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Monitoring;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Monitoring;

/// <summary>
/// Subscription-aware broadcaster that checks which topics have subscribers
/// each tick and only gathers data from the required sources. Reads are
/// volatile snapshots (~1 ms each) so Tick gathers them sequentially.
/// </summary>
public sealed class MonitoringBroadcaster : BackgroundService
{
    private readonly ISensorProvider _sensors;
    private readonly ProcessMonitor _processes;
    private readonly GpuProcessMonitor _gpuProcesses;
    private readonly INetworkProvider _network;
    private readonly IPerformanceProvider _performance;
    private readonly IScreenTimeProvider _screenTime;
    private readonly IVolumeProvider _volume;
    private readonly IFpsProvider _fps;
    // Cooling-hub probes are not visible to the platform sensor provider; null in tests that don't need them.
    private readonly IFanControlProvider? _fans;
    // Only read for the cooling page's fan-header renames; null in tests that don't need them.
    private readonly IConfigStore? _config;
    private readonly MultiplexHub _hub;
    private readonly TimeProvider _timeProvider;

    private VolumeState? _lastVolumeBroadcast;

    private int _intervalMs = 1000;
    private long _lastScreenTimeBroadcastTicks;

    // Cached envelope bytes for slow / event-driven topics. Read by the hub on
    // a new subscriber's receive thread; written by Tick on the broadcaster
    // thread. Reference assignment of byte[] is atomic on every supported
    // runtime; volatile gives publication ordering.
    private volatile byte[]? _screenTimeSnapshotBytes;
    private volatile byte[]? _volumeSnapshotBytes;

    private readonly Dictionary<string, (long bytesIn, long bytesOut)> _prevNet = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _prevNetTime = DateTime.MinValue;
    private IReadOnlyList<Models.Activity.NetworkProcessInfo>? _prevNetSnapshot;
    private NetworkFrame? _cachedNetFrame;

    // Reused across ticks to avoid per-cycle allocations on the broadcast path.
    // Tick is single-threaded and the frame is fully serialized into bytes
    // before the next Tick runs, so the list is safe to clear-and-refill.
    private readonly List<string> _gpuModelsBuf = new();

    // Processes has no cap (see BuildProcessFrame) - the full live list is
    // needed to stop membership churn as apps enter/exit a truncated top-N.
    // Network keeps its cap: no equivalent requirement has been raised for it.
    private const int TopNetwork = 25;
    private static readonly long ScreenTimeBroadcastIntervalTicks = TimeSpan.FromSeconds(10).Ticks;

    public MonitoringBroadcaster(
        ISensorProvider sensors,
        ProcessMonitor processes,
        GpuProcessMonitor gpuProcesses,
        INetworkProvider network,
        IPerformanceProvider performance,
        IScreenTimeProvider screenTime,
        IVolumeProvider volume,
        IFpsProvider fps,
        MultiplexHub hub,
        IFanControlProvider? fans = null,
        IConfigStore? config = null)
        : this(sensors, processes, gpuProcesses, network, performance, screenTime, volume, fps, hub, TimeProvider.System, fans, config)
    {
    }

    internal MonitoringBroadcaster(
        ISensorProvider sensors,
        ProcessMonitor processes,
        GpuProcessMonitor gpuProcesses,
        INetworkProvider network,
        IPerformanceProvider performance,
        IScreenTimeProvider screenTime,
        IVolumeProvider volume,
        IFpsProvider fps,
        MultiplexHub hub,
        TimeProvider timeProvider,
        IFanControlProvider? fans = null,
        IConfigStore? config = null)
    {
        _sensors = sensors;
        _processes = processes;
        _gpuProcesses = gpuProcesses;
        _network = network;
        _performance = performance;
        _screenTime = screenTime;
        _volume = volume;
        _fps = fps;
        _hub = hub;
        _fans = fans;
        _config = config;
        _timeProvider = timeProvider;
        _hub.OnTopicFirstSubscriber += OnTopicFirstSubscriber;
        _hub.OnTopicLastUnsubscriber += OnTopicLastUnsubscriber;
        _hub.RegisterSnapshotProvider("screentime", GetScreenTimeSnapshot);
        _hub.RegisterSnapshotProvider("volume", GetVolumeSnapshot);
    }

    private ReadOnlyMemory<byte>? GetScreenTimeSnapshot()
    {
        var bytes = _screenTimeSnapshotBytes;
        return bytes is null ? null : new ReadOnlyMemory<byte>(bytes);
    }

    private ReadOnlyMemory<byte>? GetVolumeSnapshot()
    {
        var bytes = _volumeSnapshotBytes;
        return bytes is null ? null : new ReadOnlyMemory<byte>(bytes);
    }

    public int GetInterval() => _intervalMs;
    public void SetInterval(int ms) => _intervalMs = Math.Max(200, ms);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _fps.SetDemand("monitoring", false);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _hub.OnTopicFirstSubscriber -= OnTopicFirstSubscriber;
        _hub.OnTopicLastUnsubscriber -= OnTopicLastUnsubscriber;
        _hub.UnregisterSnapshotProvider("screentime");
        _hub.UnregisterSnapshotProvider("volume");
        _fps.SetDemand("monitoring", false);
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Tick(stoppingToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[monitoring-broadcaster] cycle failed: {ex.Message}");
            }

            try
            { await Task.Delay(_intervalMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    internal async Task Tick(CancellationToken ct)
    {
        bool composite = _hub.TopicHasSubscribers("monitoring");
        // Not folded with composite: the summary component is never embedded in
        // MonitoringFrame, so a composite-only tick must not pay for building it.
        bool needSummary = _hub.TopicHasSubscribers("summary");
        // The summary set draws from the same cpu/gpu/memory sensors those components
        // read; folding needSummary in here means BuildSummaryComponent can reuse the
        // already-fetched lists below instead of triggering a second sensor read.
        bool needCpu = composite || _hub.TopicHasSubscribers("cpu") || needSummary;
        bool needGpu = composite || _hub.TopicHasSubscribers("gpu") || needSummary;
        bool needMemory = composite || _hub.TopicHasSubscribers("memory") || needSummary;
        bool needStorage = composite || _hub.TopicHasSubscribers("storage");
        bool needMotherboard = composite || _hub.TopicHasSubscribers("motherboard");
        bool needLhm = needCpu || needGpu || needMemory || needStorage || needMotherboard;
        bool needProcesses = composite || _hub.TopicHasSubscribers("processes");
        bool needGpuProcesses = _hub.TopicHasSubscribers("gpu-processes");
        bool needNetwork = composite || _hub.TopicHasSubscribers("network");
        bool needScreenTime = _hub.TopicHasSubscribers("screentime")
            && ShouldBroadcastScreenTime(_timeProvider.GetUtcNow().UtcTicks);
        bool needVolume = _hub.TopicHasSubscribers("volume");
        bool needFps = _hub.TopicHasSubscribers("fps");
        bool needExtras = _hub.TopicHasSubscribers("extras");

        if (needVolume)
        {
            await BroadcastVolumeIfChangedAsync();
        }

        _fps.SetDemand("monitoring", needFps);

        if (!needLhm && !needProcesses && !needGpuProcesses && !needNetwork && !needScreenTime && !needFps && !needExtras)
            return;

        // Process/network/screentime reads are volatile snapshots (<1ms each),
        // gathered sequentially since scheduling them exceeds the read cost.
        ProcessFrame? processFrame = needProcesses ? BuildProcessFrame(ct) : null;
        NetworkFrame? networkFrame = needNetwork ? BuildNetworkFrame() : null;
        ScreenTimeFrame? screenTimeFrame = needScreenTime ? BuildScreenTimeFrame() : null;
        HardwareComponent? fpsComponent = needFps ? _fps.GetComponent() : null;
        HardwareComponent? cpuComponent = needCpu ? BuildCpuComponent() : null;
        // Read once per tick: both fan-carrying components need it, and it costs a fan-channel walk.
        var fanHeaderNames = needGpu || needMotherboard ? FanHeaderNames() : null;
        List<HardwareComponent>? gpuComponents = needGpu ? BuildGpuComponents(fanHeaderNames) : null;
        HardwareComponent? memoryComponent = needMemory ? BuildMemoryComponent() : null;
        Dictionary<string, StorageComponent>? storageComponents = needStorage
            ? new Dictionary<string, StorageComponent>(_sensors.GetStorageComponents())
            : null;
        HardwareComponent? motherboardComponent = needMotherboard ? BuildMotherboardComponent(fanHeaderNames) : null;
        // needSummary implies needCpu/needGpu/needMemory above, so these are populated
        // whenever a summary component is needed.
        HardwareComponent? summaryComponent = needSummary
            ? BuildSummaryComponent(cpuComponent!, gpuComponents!, memoryComponent!)
            : null;
        SensorExtras? extras = needExtras ? _sensors.GetSensorExtras() : null;
        if (extras is not null && _fans is not null)
        {
            // Hub state is walked lock-free while its poll thread mutates it, so a torn read can
            // throw. Contain it to the coolers list rather than losing every topic this cycle.
            try { extras.Coolers.AddRange(HubCoolerSensors.Build(_fans.GetDeviceTemperatureSources())); }
            catch (Exception ex) { Console.Error.WriteLine($"[monitoring-broadcaster] hub coolers skipped: {ex.GetType().Name}: {ex.Message}"); }
        }
        string cpuModel = cpuComponent?.Name ?? "";
        _gpuModelsBuf.Clear();
        if (gpuComponents is not null)
        {
            for (int i = 0; i < gpuComponents.Count; i++)
            {
                _gpuModelsBuf.Add(gpuComponents[i].Name);
            }
        }
        var gpuModels = _gpuModelsBuf;
        string memoryTotal = needMemory ? _sensors.GetMemoryTotalFormatted() : "";
        string motherboardModel = motherboardComponent?.Name ?? "";

        if (composite)
        {
            var frame = new MonitoringFrame
            {
                Cpu = cpuComponent,
                Gpu = gpuComponents,
                Memory = memoryComponent,
                Storage = storageComponents,
                Motherboard = motherboardComponent,
                CpuModel = cpuModel,
                GpuModels = gpuModels,
                MemoryTotal = memoryTotal,
                MotherboardModel = motherboardModel,
                Processes = processFrame,
                Network = networkFrame,
            };

            var envelope = WsEnvelope.Build("monitoring", frame,
                AppJsonContext.Default.MonitoringFrame);
            await _hub.BroadcastTopicAsync("monitoring", envelope);
        }

        // Per-domain sensor topics for clients subscribed beside the composite.
        if (needLhm)
        {
            if (cpuComponent is not null && _hub.TopicHasSubscribers("cpu"))
            {
                var env = WsEnvelope.Build("cpu", cpuComponent, AppJsonContext.Default.HardwareComponent);
                await _hub.BroadcastTopicAsync("cpu", env);
            }
            if (gpuComponents is not null && _hub.TopicHasSubscribers("gpu"))
            {
                var env = WsEnvelope.Build("gpu", gpuComponents, AppJsonContext.Default.ListHardwareComponent);
                await _hub.BroadcastTopicAsync("gpu", env);
            }
            if (memoryComponent is not null && _hub.TopicHasSubscribers("memory"))
            {
                var env = WsEnvelope.Build("memory", memoryComponent, AppJsonContext.Default.HardwareComponent);
                await _hub.BroadcastTopicAsync("memory", env);
            }
            if (storageComponents is not null && _hub.TopicHasSubscribers("storage"))
            {
                var env = WsEnvelope.Build("storage", storageComponents,
                    AppJsonContext.Default.IReadOnlyDictionaryStringStorageComponent);
                await _hub.BroadcastTopicAsync("storage", env);
            }
            if (motherboardComponent is not null && _hub.TopicHasSubscribers("motherboard"))
            {
                var env = WsEnvelope.Build("motherboard", motherboardComponent, AppJsonContext.Default.HardwareComponent);
                await _hub.BroadcastTopicAsync("motherboard", env);
            }
            if (summaryComponent is not null && _hub.TopicHasSubscribers("summary"))
            {
                var env = WsEnvelope.Build("summary", summaryComponent, AppJsonContext.Default.HardwareComponent);
                await _hub.BroadcastTopicAsync("summary", env);
            }
        }

        if (needProcesses && processFrame != null && _hub.TopicHasSubscribers("processes"))
        {
            var env = WsEnvelope.Build("processes", processFrame, AppJsonContext.Default.ProcessFrame);
            await _hub.BroadcastTopicAsync("processes", env);
        }
        if (_hub.TopicHasSubscribers("gpu-processes"))
        {
            var gpuFrame = new GpuProcessFrame
            {
                Processes = new List<GpuProcessEntry>(_gpuProcesses.GetSnapshot()),
            };
            var env = WsEnvelope.Build("gpu-processes", gpuFrame, AppJsonContext.Default.GpuProcessFrame);
            await _hub.BroadcastTopicAsync("gpu-processes", env);
        }
        if (needNetwork && networkFrame != null && _hub.TopicHasSubscribers("network"))
        {
            var env = WsEnvelope.Build("network", networkFrame, AppJsonContext.Default.NetworkFrame);
            await _hub.BroadcastTopicAsync("network", env);
        }
        if (needScreenTime && screenTimeFrame != null)
        {
            var env = WsEnvelope.Build("screentime", screenTimeFrame, AppJsonContext.Default.ScreenTimeFrame);
            _screenTimeSnapshotBytes = env.ToArray();
            await _hub.BroadcastTopicAsync("screentime", env);
            Interlocked.Exchange(ref _lastScreenTimeBroadcastTicks, _timeProvider.GetUtcNow().UtcTicks);
        }
        if (needFps && fpsComponent != null)
        {
            var env = WsEnvelope.Build("fps", fpsComponent, AppJsonContext.Default.HardwareComponent);
            await _hub.BroadcastTopicAsync("fps", env);
        }
        // Detailed-tab extras: broadcast only when subscribed; the composite
        // frame omits them so other pages don't get data they don't render.
        if (needExtras && extras is not null)
        {
            var env = WsEnvelope.Build("extras", extras, AppJsonContext.Default.SensorExtras);
            await _hub.BroadcastTopicAsync("extras", env);
        }
    }

    private void OnTopicFirstSubscriber(string topic)
    {
        if (string.Equals(topic, "fps", StringComparison.OrdinalIgnoreCase))
        {
            _fps.SetDemand("monitoring", true);
        }
        if (string.Equals(topic, "screentime", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _lastScreenTimeBroadcastTicks, 0);
        }
    }

    private void OnTopicLastUnsubscriber(string topic)
    {
        if (string.Equals(topic, "fps", StringComparison.OrdinalIgnoreCase))
            _fps.SetDemand("monitoring", false);
    }

    private HardwareComponent BuildCpuComponent() => new()
    {
        Id = "cpu",
        Name = _sensors.GetCpuModel(),
        Sensors = new List<HardwareSensor>(_sensors.GetCpuSensors()),
    };

    private List<HardwareComponent> BuildGpuComponents(IReadOnlyDictionary<string, string>? fanHeaderNames)
    {
        // One component per physical GPU, each carrying only its own sensors.
        // Discrete first, so any consumer that still reads gpu[0] defaults to the
        // dGPU rather than whichever the platform enumerated first (often the iGPU);
        // the client resolves its preferred GPU by name on top of this.
        var gpus = _sensors.GetGpus().OrderBy(g => g.Integrated).ToList();
        var result = new List<HardwareComponent>(gpus.Count);
        for (int i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i];
            result.Add(new HardwareComponent
            {
                Id = $"gpu/{i}",
                Name = g.Name,
                Vendor = g.Vendor,
                Integrated = g.Integrated,
                AdapterLuid = string.IsNullOrEmpty(g.AdapterLuid) ? null : g.AdapterLuid,
                Sensors = AsList(FanSensorNames.WithRenames(g.Sensors, fanHeaderNames)),
            });
        }
        return result;
    }

    private HardwareComponent BuildMemoryComponent() => new()
    {
        Id = "memory",
        Name = "Memory",
        Sensors = new List<HardwareSensor>(_sensors.GetMemorySensors()),
    };

    private HardwareComponent BuildMotherboardComponent(IReadOnlyDictionary<string, string>? fanHeaderNames) => new()
    {
        Id = "motherboard",
        Name = _sensors.GetMotherboardModel(),
        Sensors = new List<HardwareSensor>(
            FanSensorNames.WithRenames(_sensors.GetMotherboardSensors(), fanHeaderNames)),
    };

    /// <summary>Avoids re-copying when WithRenames already returned a fresh list.</summary>
    private static List<HardwareSensor> AsList(IReadOnlyList<HardwareSensor> sensors) =>
        sensors as List<HardwareSensor> ?? new List<HardwareSensor>(sensors);

    /// <summary>Both halves of the rename join are injected; optional ctor params fail silently otherwise.</summary>
    internal bool FanHeaderRenamesWired => _fans is not null && _config is not null;

    /// <summary>
    /// The cooling page's renamed fan headers keyed by tach sensor id. Null unless something
    /// is renamed, so the common case never walks the fan channels.
    /// </summary>
    private IReadOnlyDictionary<string, string>? FanHeaderNames()
    {
        if (_fans is null || _config is null) return null;
        var fanNames = _config.Load().Cooling.FanNames;
        if (fanNames.Count == 0) return null;
        // Same containment as the hub-cooler read: a torn read of provider state must cost
        // the renames, not the whole motherboard topic.
        try { return FanSensorNames.BuildMap(_fans.GetFanChannels(), fanNames); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[monitoring-broadcaster] fan header names skipped: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static HardwareComponent BuildSummaryComponent(
        HardwareComponent cpuComponent, List<HardwareComponent> gpuComponents, HardwareComponent memoryComponent) => new()
    {
        Id = "summary",
        Name = "Quick",
        Sensors = SummarySensors.BuildFrom(
            cpuComponent.Sensors,
            gpuComponents.Count > 0 ? gpuComponents[0].Sensors : Array.Empty<HardwareSensor>(),
            memoryComponent.Sensors),
    };

    // Push the system volume on the "volume" topic only when it changed since
    // the last broadcast (tracks within one tick, no wire traffic on idle).
    // Subscribers get a fresh snapshot on connect via the multiplex hub.
    private async Task BroadcastVolumeIfChangedAsync()
    {
        VolumeState state;
        try
        { state = _volume.GetState(); }
        catch { return; }

        if (_lastVolumeBroadcast is { } prev
            && prev.Supported == state.Supported
            && prev.Muted == state.Muted
            && Math.Abs(prev.Volume - state.Volume) < 0.001)
        {
            return;
        }

        _lastVolumeBroadcast = new VolumeState { Supported = state.Supported, Volume = state.Volume, Muted = state.Muted };
        var envelope = WsEnvelope.Build("volume", state, AppJsonContext.Default.VolumeState);
        _volumeSnapshotBytes = envelope.ToArray();
        await _hub.BroadcastTopicAsync("volume", envelope);
    }

    private ProcessFrame BuildProcessFrame(CancellationToken ct)
    {
        var procs = _processes.GetProcesses();
        var snap = _performance.SampleAsync(ct).GetAwaiter().GetResult();
        var grouped = ProcessAggregation.GroupByName(procs);

        var all = new List<ProcessEntry>(procs.Count);
        for (int i = 0; i < procs.Count; i++)
        {
            var p = procs[i];
            var isApp = grouped.TryGetValue(p.Name, out var agg) && agg.HasWindow;
            var meta = _processes.GetProcessMeta(p.Name);
            all.Add(new ProcessEntry
            {
                Name = p.Name,
                CpuPercent = p.CpuPercent,
                MemoryMb = p.MemoryMb,
                StartedAtMs = p.StartedAtMs,
                IsApp = isApp,
                Publisher = meta?.Publisher,
                Signed = meta?.Signed,
                StorageBytesPerSec = p.StorageBytesPerSec,
            });
        }

        return new ProcessFrame
        {
            Processes = all,
            TotalCpu = snap.Cpu ?? 0,
            TotalMemoryPercent = snap.Memory ?? 0,
        };
    }

    private NetworkFrame BuildNetworkFrame()
    {
        var snapshot = _network.GetSnapshot();

        // nettop runs on its own slower timer. If the snapshot object hasn't
        // changed since the last tick, reuse the cached rates instead of
        // producing zeros from an identical delta.
        if (ReferenceEquals(snapshot, _prevNetSnapshot) && _cachedNetFrame is not null)
            return _cachedNetFrame;
        _prevNetSnapshot = snapshot;

        var now = DateTime.UtcNow;
        var dt = _prevNetTime != DateTime.MinValue
            ? (now - _prevNetTime).TotalSeconds
            : 0;
        _prevNetTime = now;

        var count = Math.Min(snapshot.Count, TopNetwork);
        var entries = new List<NetworkRateEntry>(count);
        if (dt > 0 && dt < 30)
        {
            for (int i = 0; i < count; i++)
            {
                var p = snapshot[i];
                double rateIn = 0, rateOut = 0;
                if (_prevNet.TryGetValue(p.Name, out var prev))
                {
                    var deltaIn = Math.Max(0, p.BytesIn - prev.bytesIn);
                    var deltaOut = Math.Max(0, p.BytesOut - prev.bytesOut);
                    rateIn = deltaIn / dt;
                    rateOut = deltaOut / dt;
                }
                entries.Add(new NetworkRateEntry
                {
                    Name = p.Name,
                    RateIn = rateIn,
                    RateOut = rateOut,
                });
            }
        }

        _prevNet.Clear();
        for (int i = 0; i < snapshot.Count; i++)
        {
            var p = snapshot[i];
            _prevNet[p.Name] = (p.BytesIn, p.BytesOut);
        }

        _cachedNetFrame = new NetworkFrame { Entries = entries };
        return _cachedNetFrame;
    }

    private ScreenTimeFrame BuildScreenTimeFrame()
    {
        return new ScreenTimeFrame
        {
            Focus = _screenTime.GetCurrentSession(),
            History = new List<AppUsage>(_screenTime.GetTodayUsage()),
        };
    }

    private bool ShouldBroadcastScreenTime(long nowTicks)
    {
        long lastTicks = Interlocked.Read(ref _lastScreenTimeBroadcastTicks);
        return lastTicks == 0 || nowTicks - lastTicks >= ScreenTimeBroadcastIntervalTicks;
    }
}
