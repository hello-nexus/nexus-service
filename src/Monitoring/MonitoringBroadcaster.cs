using System;
using System.Collections.Concurrent;
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

    // A first subscriber to one of these gets it at once instead of at the next tick.
    private static readonly HashSet<string> TickTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "monitoring", "cpu", "gpu", "memory", "storage", "motherboard", "summary",
        "processes", "network", "fps", "extras",
    };
    private static readonly string[] ProcessTopics = { "monitoring", "processes" };
    // Bounds how long an early send is held for the process sample its subscribe triggered.
    private const int ProcessSampleWaitMs = 250;
    // Set while a process-bearing early send is held, so a finished sample wakes the loop.
    private volatile bool _processSendHeld;
    // Owned by the loop thread: process-bearing topics waiting for their sample.
    private HeldProcessSend? _held;

    private sealed class HeldProcessSend
    {
        public required HashSet<string> Topics { get; init; }
        public required long ProcessSeq { get; init; }
        public required long Until { get; init; }
    }
    // Counted, so a first subscriber that lands mid-tick still cuts the next wait short.
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly object _firstGate = new();
    private readonly HashSet<string> _firstSubscribed = new(StringComparer.OrdinalIgnoreCase);
    // ProcessMonitor.SampleSeq when a process-bearing topic got its first subscriber; -1 when none is pending.
    private long _processSeqAtFirst = -1;
    private readonly ConcurrentDictionary<string, long> _lastSentTicks = new(StringComparer.OrdinalIgnoreCase);

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
        _processes.Sampled += OnProcessSampled;
        _hub.RegisterSnapshotProvider("screentime", GetScreenTimeSnapshot);
        _hub.RegisterSnapshotProvider("volume", GetVolumeSnapshot);
    }

    private Task PublishTickTopicAsync(string topic, ReadOnlyMemory<byte> envelope)
    {
        _lastSentTicks[topic] = _timeProvider.GetUtcNow().UtcTicks;
        return _hub.BroadcastTopicAsync(topic, envelope);
    }

    /// <summary>
    /// Takes the topics that gained a first subscriber since the last call. With
    /// <paramref name="skipRecentlySent"/>, drops any sent within the last interval:
    /// a client that unsubscribes and resubscribes must not receive a duplicate sample.
    /// </summary>
    internal (HashSet<string> Topics, long ProcessSeq) TakeFirstSubscribed(bool skipRecentlySent)
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var floor = TimeSpan.FromMilliseconds(_intervalMs).Ticks;
        lock (_firstGate)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var topic in _firstSubscribed)
            {
                if (skipRecentlySent && _lastSentTicks.TryGetValue(topic, out var sent) && now - sent < floor)
                    continue;
                taken.Add(topic);
            }
            var processSeq = _processSeqAtFirst;
            _firstSubscribed.Clear();
            _processSeqAtFirst = -1;
            return (taken, processSeq);
        }
    }

    /// <summary>Sends newly subscribed topics ahead of the tick and returns the process-bearing ones to hold.</summary>
    internal async Task<HashSet<string>> SendEarlyAsync(HashSet<string> topics, CancellationToken ct)
    {
        var hold = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var topic in ProcessTopics)
        {
            if (topics.Remove(topic))
                hold.Add(topic);
        }
        if (topics.Count > 0)
            await Tick(ct, topics);
        return hold;
    }

    /// <summary>Sends held process-bearing topics once ProcessMonitor has sampled after <paramref name="processSeq"/>.</summary>
    internal async Task<bool> TrySendHeldAsync(HashSet<string> held, long processSeq, CancellationToken ct)
    {
        // The subscribe woke ProcessMonitor; before its sample the frame would carry a stale list.
        if (_processes.SampleSeq <= processSeq) return false;
        await Tick(ct, held);
        return true;
    }

    private void OnProcessSampled()
    {
        if (_processSendHeld) _wake.Release();
    }

    /// <summary>When the held process send lapses (Environment.TickCount64 ms), or null when none is held.</summary>
    internal long? HeldUntil => _held?.Until;

    /// <summary>
    /// One early pass at <paramref name="now"/> (Environment.TickCount64 ms): sends or drops a held
    /// process send, then sends topics that just gained their first subscriber.
    /// </summary>
    internal async Task EarlyPassAsync(long now, CancellationToken ct)
    {
        if (_held is { } held && (_processes.SampleSeq > held.ProcessSeq || now >= held.Until))
        {
            // Cleared before sending, so a send that throws is not retried in a loop.
            DropHeld();
            await TrySendHeldAsync(held.Topics, held.ProcessSeq, ct);
        }
        var (fresh, processSeq) = TakeFirstSubscribed(skipRecentlySent: true);
        if (fresh.Count == 0) return;
        var hold = await SendEarlyAsync(fresh, ct);
        if (hold.Count == 0 || processSeq < 0) return;
        if (_held is not null)
        {
            _held.Topics.UnionWith(hold);
            return;
        }
        _held = new HeldProcessSend { Topics = hold, ProcessSeq = processSeq, Until = Math.Max(now, Environment.TickCount64) + ProcessSampleWaitMs };
        _processSendHeld = true;
        // The sample may have landed before the flag was set.
        if (_processes.SampleSeq > processSeq) _wake.Release();
    }

    /// <summary>Forgets a held process send; the regular tick serves its topics.</summary>
    internal void DropHeld()
    {
        _held = null;
        _processSendHeld = false;
    }

    private ReadOnlyMemory<byte>? GetScreenTimeSnapshot()
    {
        var bytes = _screenTimeSnapshotBytes;
        // Not a ternary: null would convert through byte[] into an empty, non-null envelope.
        if (bytes is null) return null;
        return new ReadOnlyMemory<byte>(bytes);
    }

    private ReadOnlyMemory<byte>? GetVolumeSnapshot()
    {
        var bytes = _volumeSnapshotBytes;
        // Not a ternary: null would convert through byte[] into an empty, non-null envelope.
        if (bytes is null) return null;
        return new ReadOnlyMemory<byte>(bytes);
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
        _processes.Sampled -= OnProcessSampled;
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
            // The full tick serves every topic subscribed so far.
            TakeFirstSubscribed(skipRecentlySent: false);
            try
            {
                await Tick(stoppingToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[monitoring-broadcaster] cycle failed: {ex.Message}");
            }

            // Early sends carry only topics nobody else receives, so existing
            // subscribers keep exactly one frame per interval.
            var dueAt = Environment.TickCount64 + _intervalMs;
            try
            {
                while (Environment.TickCount64 < dueAt)
                {
                    var until = Math.Min(dueAt, HeldUntil ?? dueAt);
                    var left = until - Environment.TickCount64;
                    if (left > 0 && await WaitForNextTickAsync((int)left, stoppingToken))
                        while (_wake.Wait(0)) { }
                    try
                    {
                        await EarlyPassAsync(Environment.TickCount64, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        Console.Error.WriteLine($"[monitoring-broadcaster] early send failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            // Anything still held goes out with the regular tick.
            DropHeld();
        }
    }

    /// <summary>True when a first subscriber cut the wait short.</summary>
    internal Task<bool> WaitForNextTickAsync(int delayMs, CancellationToken stoppingToken) =>
        _wake.WaitAsync(delayMs, stoppingToken);

    /// <summary>One broadcast pass; <paramref name="only"/> limits it to those topics.</summary>
    internal async Task Tick(CancellationToken ct, IReadOnlySet<string>? only = null)
    {
        bool Send(string topic) => (only is null || only.Contains(topic)) && _hub.TopicHasSubscribers(topic);
        bool composite = Send("monitoring");
        // Not folded with composite: the summary component is never embedded in
        // MonitoringFrame, so a composite-only tick must not pay for building it.
        bool needSummary = Send("summary");
        // The summary set draws from the same cpu/gpu/memory sensors those components
        // read; folding needSummary in here means BuildSummaryComponent can reuse the
        // already-fetched lists below instead of triggering a second sensor read.
        bool needCpu = composite || Send("cpu") || needSummary;
        bool needGpu = composite || Send("gpu") || needSummary;
        bool needMemory = composite || Send("memory") || needSummary;
        bool needStorage = composite || Send("storage");
        bool needMotherboard = composite || Send("motherboard");
        bool needLhm = needCpu || needGpu || needMemory || needStorage || needMotherboard;
        bool needProcesses = composite || Send("processes");
        bool needGpuProcesses = Send("gpu-processes");
        bool needNetwork = composite || Send("network");
        bool needScreenTime = Send("screentime")
            && ShouldBroadcastScreenTime(_timeProvider.GetUtcNow().UtcTicks);
        bool needVolume = Send("volume");
        bool fpsLive = _hub.TopicHasSubscribers("fps");
        bool needFps = (only is null || only.Contains("fps")) && fpsLive;
        bool needExtras = Send("extras");

        if (needVolume)
        {
            await BroadcastVolumeIfChangedAsync();
        }

        _fps.SetDemand("monitoring", fpsLive);

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
            await PublishTickTopicAsync("monitoring", envelope);
        }

        // Per-domain sensor topics for clients subscribed beside the composite.
        if (needLhm)
        {
            if (cpuComponent is not null && Send("cpu"))
            {
                var env = WsEnvelope.Build("cpu", cpuComponent, AppJsonContext.Default.HardwareComponent);
                await PublishTickTopicAsync("cpu", env);
            }
            if (gpuComponents is not null && Send("gpu"))
            {
                var env = WsEnvelope.Build("gpu", gpuComponents, AppJsonContext.Default.ListHardwareComponent);
                await PublishTickTopicAsync("gpu", env);
            }
            if (memoryComponent is not null && Send("memory"))
            {
                var env = WsEnvelope.Build("memory", memoryComponent, AppJsonContext.Default.HardwareComponent);
                await PublishTickTopicAsync("memory", env);
            }
            if (storageComponents is not null && Send("storage"))
            {
                var env = WsEnvelope.Build("storage", storageComponents,
                    AppJsonContext.Default.IReadOnlyDictionaryStringStorageComponent);
                await PublishTickTopicAsync("storage", env);
            }
            if (motherboardComponent is not null && Send("motherboard"))
            {
                var env = WsEnvelope.Build("motherboard", motherboardComponent, AppJsonContext.Default.HardwareComponent);
                await PublishTickTopicAsync("motherboard", env);
            }
            if (summaryComponent is not null && Send("summary"))
            {
                var env = WsEnvelope.Build("summary", summaryComponent, AppJsonContext.Default.HardwareComponent);
                await PublishTickTopicAsync("summary", env);
            }
        }

        if (needProcesses && processFrame != null && Send("processes"))
        {
            var env = WsEnvelope.Build("processes", processFrame, AppJsonContext.Default.ProcessFrame);
            await PublishTickTopicAsync("processes", env);
        }
        if (Send("gpu-processes"))
        {
            var gpuFrame = new GpuProcessFrame
            {
                Processes = new List<GpuProcessEntry>(_gpuProcesses.GetSnapshot()),
            };
            var env = WsEnvelope.Build("gpu-processes", gpuFrame, AppJsonContext.Default.GpuProcessFrame);
            await PublishTickTopicAsync("gpu-processes", env);
        }
        if (needNetwork && networkFrame != null && Send("network"))
        {
            var env = WsEnvelope.Build("network", networkFrame, AppJsonContext.Default.NetworkFrame);
            await PublishTickTopicAsync("network", env);
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
            await PublishTickTopicAsync("fps", env);
        }
        // Detailed-tab extras: broadcast only when subscribed; the composite
        // frame omits them so other pages don't get data they don't render.
        if (needExtras && extras is not null)
        {
            var env = WsEnvelope.Build("extras", extras, AppJsonContext.Default.SensorExtras);
            await PublishTickTopicAsync("extras", env);
        }
    }

    private void OnTopicFirstSubscriber(string topic)
    {
        if (TickTopics.Contains(topic))
        {
            lock (_firstGate)
            {
                _firstSubscribed.Add(topic);
                if (_processSeqAtFirst < 0 && Array.Exists(ProcessTopics, t => string.Equals(t, topic, StringComparison.OrdinalIgnoreCase)))
                {
                    _processSeqAtFirst = _processes.SampleSeq;
                }
            }
            _wake.Release();
        }
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
