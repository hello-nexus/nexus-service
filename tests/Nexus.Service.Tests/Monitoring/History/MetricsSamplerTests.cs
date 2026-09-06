using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// Drives MetricsSampler through its internal Tick(DateTime, CancellationToken)
/// seam - the dedicated-thread loop itself (Run, ReadyAsync wait) is only
/// covered by the StartAsync/StopAsync lifecycle test below. A
/// RecordingMetricsHistoryStore stands in for the real SQLite store so flush
/// cadence and prune cutoffs are assertable without touching disk.
/// </summary>
public class MetricsSamplerTests
{
    private sealed class StubSensors : ISensorProvider
    {
        public string GetCpuModel() => "Stub CPU";
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
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
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => "Stub Board";
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private sealed class SpyFpsProvider : IFpsProvider
    {
        public List<bool> DemandCalls { get; } = new();
        public double? CurrentFps { get; set; }

        public void SetDemand(string source, bool wanted) => DemandCalls.Add(wanted);

        public bool TryReadCurrentFps(out double fps)
        {
            if (CurrentFps is { } value)
            {
                fps = value;
                return true;
            }
            fps = 0;
            return false;
        }

        public HardwareComponent GetComponent() => new() { Id = "fps", Name = "FPS", Sensors = new List<HardwareSensor>() };
        public void Dispose() { }
    }

    private sealed class FakeScreenTimeProvider : IScreenTimeProvider
    {
        public FocusSession? Session { get; set; }
        public FocusSession? GetCurrentSession() => Session;
        public event Action? FocusChanged { add { } remove { } }
        public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
    }

    private sealed class StubMetricsSource : IMetricsSource
    {
        public int Calls { get; private set; }
        public double Cpu { get; set; } = 42;

        public Task<MetricSample> SampleAsync(long tsSec, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new MetricSample(
                tsSec, Cpu, 60, 1000, 500, 55, Array.Empty<GpuReading>(), Array.Empty<FanReading>()));
        }
    }

    private sealed class RecordingMetricsHistoryStore : IMetricsHistoryStore
    {
        private readonly List<string>? _sharedCallOrder;

        public RecordingMetricsHistoryStore(List<string>? sharedCallOrder = null) => _sharedCallOrder = sharedCallOrder;

        public List<(int Count, long? PruneCutoffSec)> AppendCalls { get; } = new();

        public void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec)
        {
            AppendCalls.Add((samples.Count, pruneCutoffSec));
            _sharedCallOrder?.Add("scalar");
        }

        public IReadOnlyList<MetricSample> Query(long fromSec, long toSec) => Array.Empty<MetricSample>();

        public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds) =>
            Array.Empty<ScalarDecimatedSlot>();

        public IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds) =>
            Array.Empty<GpuDecimatedSlot>();

        public IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs) =>
            Array.Empty<TemperatureBucketRow>();

        public IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds) =>
            Array.Empty<FanDecimatedSlot>();

        public IReadOnlyList<ComponentTempDecimatedSlot> QueryComponentTempDecimated(long fromSec, long toSec, int stepSeconds) =>
            Array.Empty<ComponentTempDecimatedSlot>();

        public int ResetAll() => 0;
        public int BlankFpsSeries() => 0;

        public void Dispose() { }
    }

    private sealed class StubAppUsageSource : IAppUsageSource
    {
        public int Calls { get; private set; }
        public IReadOnlyList<AppMetricSample> NextSample { get; set; } =
            new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 10, null) }) };

        public IReadOnlyList<AppMetricSample> Sample()
        {
            Calls++;
            return NextSample;
        }
    }

    private sealed class ThrowingAppUsageSource : IAppUsageSource
    {
        public IReadOnlyList<AppMetricSample> Sample() => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingAppUsageHistoryStore : IAppUsageHistoryStore
    {
        private readonly List<string>? _sharedCallOrder;

        public RecordingAppUsageHistoryStore(List<string>? sharedCallOrder = null) => _sharedCallOrder = sharedCallOrder;

        public List<(int TickCount, long? PruneCutoffSec)> AppendCalls { get; } = new();

        public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec)
        {
            AppendCalls.Add((ticks.Count, pruneCutoffSec));
            _sharedCallOrder?.Add("app");
        }

        public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps) =>
            Array.Empty<AppWindowStat>();

        public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec) =>
            Array.Empty<AppRawPoint>();

        public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec) =>
            Array.Empty<long>();

        public long? QueryFirstSeen(string appName) => null;
    }

    private sealed class RecordingSampleSink : IMetricsSampleSink
    {
        public List<(MetricSample Sample, DateTime NowUtc)> Calls { get; } = new();
        public void OnSample(MetricSample sample, DateTime nowUtc) => Calls.Add((sample, nowUtc));
    }

    private static MetricsSampler CreateSampler(
        StubMetricsSource source, RecordingMetricsHistoryStore store, MetricsSampleBuffer? buffer = null,
        IAppUsageSource? appSource = null, AppSampleBuffer? appBuffer = null, IAppUsageHistoryStore? appStore = null,
        FeatureGates? gates = null, IFpsProvider? fps = null, IScreenTimeProvider? screenTime = null, IConfigStore? config = null,
        IMetricsSampleSink? sink = null) =>
        new(new StubSensors(), source, buffer ?? new MetricsSampleBuffer(), store,
            appSource ?? new StubAppUsageSource(), appBuffer ?? new AppSampleBuffer(), appStore ?? new RecordingAppUsageHistoryStore(),
            fps ?? new StubFpsProvider(), screenTime ?? new StubScreenTimeProvider(), config ?? new FakeConfigStore(),
            gates, sink);

    [Fact]
    public async Task Tick_AppendsOneSampleToTheBuffer_PerCall()
    {
        var source = new StubMetricsSource();
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(source, new RecordingMetricsHistoryStore(), buffer);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.Single(buffer.PendingSnapshot());
    }

    [Fact]
    public async Task Tick_NoSinkRegistered_StillAppendsToTheBuffer()
    {
        var source = new StubMetricsSource();
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(source, new RecordingMetricsHistoryStore(), buffer);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Single(buffer.PendingSnapshot());
    }

    [Fact]
    public async Task Tick_WithSinkRegistered_InvokesOnSample_WithTheAppendedSampleAndTickTime()
    {
        var source = new StubMetricsSource();
        var buffer = new MetricsSampleBuffer();
        var sink = new RecordingSampleSink();
        var sampler = CreateSampler(source, new RecordingMetricsHistoryStore(), buffer, sink: sink);
        var now = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc);

        await sampler.Tick(now, CancellationToken.None);

        var call = Assert.Single(sink.Calls);
        Assert.Equal(buffer.PendingSnapshot()[0], call.Sample);
        Assert.Equal(now, call.NowUtc);
    }

    [Fact]
    public async Task Tick_HoldsFpsDemand_WhenTrackingEnabledAndSomethingIsFocused()
    {
        var fps = new SpyFpsProvider();
        var screenTime = new FakeScreenTimeProvider { Session = new FocusSession { Id = "1234", Name = "game.exe" } };
        var sampler = CreateSampler(new StubMetricsSource(), new RecordingMetricsHistoryStore(), fps: fps, screenTime: screenTime);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Equal(new[] { true }, fps.DemandCalls);
    }

    [Fact]
    public async Task Tick_ReleasesFpsDemand_WhenNothingIsFocused()
    {
        var fps = new SpyFpsProvider();
        var screenTime = new FakeScreenTimeProvider { Session = null };
        var sampler = CreateSampler(new StubMetricsSource(), new RecordingMetricsHistoryStore(), fps: fps, screenTime: screenTime);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Equal(new[] { false }, fps.DemandCalls);
    }

    [Fact]
    public async Task Tick_ReleasesFpsDemand_WhenTrackingDisabled_EvenWhileFocused()
    {
        var fps = new SpyFpsProvider();
        var screenTime = new FakeScreenTimeProvider { Session = new FocusSession { Id = "1234", Name = "game.exe" } };
        var config = new FakeConfigStore();
        config.Update(s => s.Fps.TrackingEnabled = false);
        var sampler = CreateSampler(new StubMetricsSource(), new RecordingMetricsHistoryStore(), fps: fps, screenTime: screenTime, config: config);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Equal(new[] { false }, fps.DemandCalls);
    }

    [Fact]
    public async Task Tick_HoldsFpsDemand_EvenWhenMonitoringGateIsOff()
    {
        // FpsSessionRecorder rides this same demand and must keep working
        // with the Monitoring history pillar off.
        var fps = new SpyFpsProvider();
        var screenTime = new FakeScreenTimeProvider { Session = new FocusSession { Id = "1234", Name = "game.exe" } };
        var monitoringOffConfig = new FakeConfigStore();
        monitoringOffConfig.Update(s => s.Features.Monitoring = false);
        var sampler = CreateSampler(
            new StubMetricsSource(), new RecordingMetricsHistoryStore(), fps: fps, screenTime: screenTime,
            gates: new FeatureGates(monitoringOffConfig));

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Equal(new[] { true }, fps.DemandCalls);
    }

    [Fact]
    public async Task Tick_SamplesTheCurrentFps_IntoTheSameTicksSample()
    {
        var fps = new SpyFpsProvider { CurrentFps = 144 };
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(new StubMetricsSource(), new RecordingMetricsHistoryStore(), buffer, fps: fps);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc), CancellationToken.None);

        var sample = Assert.Single(buffer.PendingSnapshot());
        Assert.Equal(144, sample.Fps);
    }

    [Fact]
    public async Task Tick_FreshButSubOneFps_LeavesTheSampleAsAGap()
    {
        var fps = new SpyFpsProvider { CurrentFps = 0.4 };
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(new StubMetricsSource(), new RecordingMetricsHistoryStore(), buffer, fps: fps);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc), CancellationToken.None);

        var sample = Assert.Single(buffer.PendingSnapshot());
        Assert.Null(sample.Fps);
    }

    [Fact]
    public async Task Tick_NoFreshFps_LeavesTheSampleAsAGap()
    {
        var fps = new SpyFpsProvider { CurrentFps = null };
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(new StubMetricsSource(), new RecordingMetricsHistoryStore(), buffer, fps: fps);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc), CancellationToken.None);

        var sample = Assert.Single(buffer.PendingSnapshot());
        Assert.Null(sample.Fps);
    }

    [Fact]
    public async Task Tick_TrackingDisabled_LeavesTheSampleAsAGap_EvenWithAFreshFps()
    {
        var fps = new SpyFpsProvider { CurrentFps = 144 };
        var config = new FakeConfigStore();
        config.Update(s => s.Fps.TrackingEnabled = false);
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(new StubMetricsSource(), new RecordingMetricsHistoryStore(), buffer, fps: fps, config: config);

        await sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc), CancellationToken.None);

        var sample = Assert.Single(buffer.PendingSnapshot());
        Assert.Null(sample.Fps);
    }

    [Fact]
    public async Task Tick_DoesNotFlush_BeforeTheFlushIntervalIsReached()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds - 1; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        Assert.Empty(store.AppendCalls);
    }

    [Fact]
    public async Task Tick_FlushesTheWholeBuffer_OnTheFlushIntervalTick()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var buffer = new MetricsSampleBuffer();
        var sampler = CreateSampler(source, store, buffer);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        var call = Assert.Single(store.AppendCalls);
        Assert.Equal(MetricsHistory.FlushSeconds, call.Count);
        Assert.Empty(buffer.PendingSnapshot()); // RemoveThrough ran after the successful flush
    }

    [Fact]
    public async Task Tick_FlushesAgain_AfterTheSecondFlushInterval()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds * 2; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        Assert.Equal(2, store.AppendCalls.Count);
        Assert.All(store.AppendCalls, c => Assert.Equal(MetricsHistory.FlushSeconds, c.Count));
    }

    [Fact]
    public async Task Tick_RequestsAPrune_OnTheFirstFlush()
    {
        // _lastPruneUtc starts at DateTime.MinValue, so the very first flush
        // is always "over an hour" since the last prune - retention is
        // enforced immediately on a fresh start rather than waiting a full
        // hour after boot.
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        var call = Assert.Single(store.AppendCalls);
        Assert.NotNull(call.PruneCutoffSec);
        var expectedCutoff = new DateTimeOffset(start.AddSeconds(MetricsHistory.FlushSeconds - 1)).ToUnixTimeSeconds()
            - MetricsHistory.RetentionDays * 86_400L;
        Assert.Equal(expectedCutoff, call.PruneCutoffSec);
    }

    [Fact]
    public async Task Tick_DoesNotRequestASecondPrune_WithinTheSameHour()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds * 2; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        Assert.Equal(2, store.AppendCalls.Count);
        Assert.NotNull(store.AppendCalls[0].PruneCutoffSec);
        Assert.Null(store.AppendCalls[1].PruneCutoffSec);
    }

    [Fact]
    public async Task Tick_DoesNotThrow_WhenTheSourceFailsEntirely()
    {
        var source = new ThrowingMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var buffer = new MetricsSampleBuffer();
        var sampler = new MetricsSampler(new StubSensors(), source, buffer, store,
            new StubAppUsageSource(), new AppSampleBuffer(), new RecordingAppUsageHistoryStore(),
            new StubFpsProvider(), new StubScreenTimeProvider(), new FakeConfigStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sampler.Tick(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None));

        // internal Tick propagates a source failure to its caller (the
        // BackgroundService loop's own try/catch is what applies the
        // once-a-minute warn throttle, not Tick itself).
        Assert.Empty(buffer.PendingSnapshot());
    }

    [Fact]
    public async Task Tick_SamplesApps_OnlyEveryAppSampleInterval()
    {
        var source = new StubMetricsSource();
        var appSource = new StubAppUsageSource();
        var appBuffer = new AppSampleBuffer();
        var sampler = CreateSampler(source, new RecordingMetricsHistoryStore(), appSource: appSource, appBuffer: appBuffer);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.AppSampleIntervalSeconds - 1; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }
        Assert.Equal(0, appSource.Calls);

        await sampler.Tick(start.AddSeconds(MetricsHistory.AppSampleIntervalSeconds - 1), CancellationToken.None);
        Assert.Equal(1, appSource.Calls);
        Assert.Single(appBuffer.PendingSnapshot());
    }

    [Fact]
    public async Task Tick_FlushesEveryAppTickSinceTheLastFlush_OnTheFlushIntervalTick()
    {
        var source = new StubMetricsSource();
        var appStore = new RecordingAppUsageHistoryStore();
        var appBuffer = new AppSampleBuffer();
        var sampler = CreateSampler(source, new RecordingMetricsHistoryStore(), appBuffer: appBuffer, appStore: appStore);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        var expectedAppTicks = MetricsHistory.FlushSeconds / MetricsHistory.AppSampleIntervalSeconds;
        var call = Assert.Single(appStore.AppendCalls);
        Assert.Equal(expectedAppTicks, call.TickCount);
        Assert.Empty(appBuffer.PendingSnapshot()); // RemoveThrough ran after the successful flush
    }

    [Fact]
    public async Task Tick_DoesNotThrow_WhenTheAppSourceFails_AndScalarFlushStillRuns()
    {
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store, appSource: new ThrowingAppUsageSource());
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        // A broken app source must never suppress the core scalar flush.
        var call = Assert.Single(store.AppendCalls);
        Assert.Equal(MetricsHistory.FlushSeconds, call.Count);
    }

    [Fact]
    public async Task Tick_FlushesTheScalarStore_BeforeTheAppStore_OnEveryFlush()
    {
        // Load-bearing for SqliteMetricsHistoryStore.InsertAppRows: an
        // app_gpu_seconds row resolves its gpu surrogate key from _gpuKeys,
        // which the scalar Append populates for the tick's GPUs. If the app
        // store flushed first, a GPU seen only this flush would have no
        // surrogate key yet and its app rows would be silently dropped.
        var callOrder = new List<string>();
        var source = new StubMetricsSource();
        var store = new RecordingMetricsHistoryStore(callOrder);
        var appStore = new RecordingAppUsageHistoryStore(callOrder);
        var sampler = CreateSampler(source, store, appStore: appStore);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        Assert.Equal(new[] { "scalar", "app" }, callOrder);
    }

    [Fact]
    public async Task StartStop_RunsTheLoopOnADedicatedThread_AndFlushesOnStop()
    {
        var source = new StubMetricsSource();
        var buffer = new MetricsSampleBuffer();
        var store = new RecordingMetricsHistoryStore();
        var sampler = CreateSampler(source, store, buffer);

        await sampler.StartAsync(CancellationToken.None);
        try
        {
            // Poll for the first tick rather than assuming timing: the tick
            // interval is a fixed 1s in production, so this is bounded well
            // above that instead of racing it.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (source.Calls == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.True(source.Calls > 0);
            Assert.Equal("metrics-sampler", sampler.RunningThreadName);
            Assert.False(sampler.RunningThreadIsPoolThread);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
        }

        // StopAsync joined the thread after its graceful final flush ran, so
        // whatever the buffer held at stop time landed in the store already.
        Assert.NotEmpty(store.AppendCalls);
        Assert.Empty(buffer.PendingSnapshot());
    }

    private sealed class ThrowingMetricsSource : IMetricsSource
    {
        public Task<MetricSample> SampleAsync(long tsSec, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Tick_GateOff_AppendsNothing()
    {
        var source = new StubMetricsSource();
        var buffer = new MetricsSampleBuffer();
        var store = new RecordingMetricsHistoryStore();
        var configStore = new Nexus.Service.Tests.InMemoryConfigStore();
        configStore.Update(s => s.Features.Monitoring = false);
        var sampler = CreateSampler(source, store, buffer, gates: new FeatureGates(configStore));
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < MetricsHistory.FlushSeconds * 2; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }

        Assert.Equal(0, source.Calls);
        Assert.Empty(buffer.PendingSnapshot());
        Assert.Empty(store.AppendCalls);
    }

    [Fact]
    public async Task Tick_GateOff_FlushesThePreToggleTailExactlyOnce()
    {
        var source = new StubMetricsSource();
        var buffer = new MetricsSampleBuffer();
        var store = new RecordingMetricsHistoryStore();
        var configStore = new Nexus.Service.Tests.InMemoryConfigStore();
        var sampler = CreateSampler(source, store, buffer, gates: new FeatureGates(configStore));
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // A few enabled ticks buffer samples without reaching a flush boundary.
        for (var i = 0; i < 3; i++)
        {
            await sampler.Tick(start.AddSeconds(i), CancellationToken.None);
        }
        Assert.Empty(store.AppendCalls);

        configStore.Update(s => s.Features.Monitoring = false);
        await sampler.Tick(start.AddSeconds(3), CancellationToken.None);
        var call = Assert.Single(store.AppendCalls);
        Assert.Equal(3, call.Count);
        Assert.Empty(buffer.PendingSnapshot());

        // A later disabled tick must not flush again (nothing buffered since).
        await sampler.Tick(start.AddSeconds(4), CancellationToken.None);
        Assert.Single(store.AppendCalls);
    }
}
