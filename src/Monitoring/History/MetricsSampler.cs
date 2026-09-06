using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Always-on sampler: reads one MetricSample per tick into MetricsSampleBuffer,
/// flushes the buffered tail to IMetricsHistoryStore every MetricsHistory.FlushSeconds
/// ticks. Runs on its own dedicated thread rather than the shared thread pool, so
/// the 1Hz tick and the periodic flush are never delayed by thread-pool contention
/// elsewhere in the process (queued work, pool starvation under load).
/// </summary>
public sealed class MetricsSampler : IHostedService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WarnThrottle = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    // Bounds the wait for ISensorProvider.ReadyAsync so a platform whose
    // hardware enumeration never signals ready still starts sampling.
    private static readonly TimeSpan StartupReadyTimeout = TimeSpan.FromSeconds(60);

    // Bounds StopAsync so a stuck flush can't hang service shutdown; the
    // thread is a background thread and is reclaimed by the process either way.
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly ISensorProvider _sensors;
    private readonly IMetricsSource _source;
    private readonly MetricsSampleBuffer _buffer;
    private readonly IMetricsHistoryStore _store;
    private readonly IAppUsageSource _appSource;
    private readonly AppSampleBuffer _appBuffer;
    private readonly IAppUsageHistoryStore _appStore;
    private readonly IFpsProvider _fps;
    private readonly IScreenTimeProvider _screenTime;
    private readonly IConfigStore _config;
    private readonly IMetricsSampleSink? _sink;

    private readonly CancellationTokenSource _stopCts = new();
    private Thread? _thread;
    private readonly FeatureGates _gates;

    private int _tickCount;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private DateTime _lastWarnUtc = DateTime.MinValue;
    private DateTime _lastSinkWarnUtc = DateTime.MinValue;
    // Tracks the Monitoring flag's previous tick so the disabled edge flushes
    // the pre-toggle tail exactly once instead of on every later tick.
    private bool _monitoringWasEnabled = true;

    // Demand source id this sampler holds on IFpsProvider - see
    // IFpsProvider.SetDemand's multi-source contract.
    private const string FpsDemandSource = "history";

    public MetricsSampler(
        ISensorProvider sensors, IMetricsSource source, MetricsSampleBuffer buffer,
        IMetricsHistoryStore store,
        IAppUsageSource appSource, AppSampleBuffer appBuffer, IAppUsageHistoryStore appStore,
        IFpsProvider fps, IScreenTimeProvider screenTime, IConfigStore config,
        FeatureGates? gates = null, IMetricsSampleSink? sink = null)
    {
        _sensors = sensors;
        _source = source;
        _buffer = buffer;
        _store = store;
        _appSource = appSource;
        _appBuffer = appBuffer;
        _appStore = appStore;
        _fps = fps;
        _screenTime = screenTime;
        _config = config;
        _gates = gates ?? FeatureGates.AllEnabled;
        _sink = sink;
    }

    // Test seam: set at the top of Run() from inside the dedicated thread, so
    // a lifecycle test can confirm the loop executed off the thread pool.
    internal string? RunningThreadName { get; private set; }
    internal bool? RunningThreadIsPoolThread { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "metrics-sampler",
            // AboveNormal keeps the 1Hz tick and periodic flush landing on
            // schedule when the machine is busy; not Highest, so it never
            // starves real-time work elsewhere (audio capture, the lighting
            // engine's own render thread).
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();
        _thread?.Join(StopJoinTimeout);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // The host disposes this twice on shutdown (hosted-service teardown,
        // then the provider); a second Cancel on the disposed source throws
        // ObjectDisposedException and takes the process down mid-stop.
        if (_disposed) return;
        _disposed = true;
        // Cancel + Join unconditionally rather than gating on
        // IsCancellationRequested: if StopAsync's own Join already timed out
        // (a slow flush still in flight), this is a second bounded wait
        // window rather than an immediate disposal of _stopCts while the
        // thread can still touch its Token.
        _stopCts.Cancel();
        _thread?.Join(StopJoinTimeout);
        _stopCts.Dispose();
    }

    private bool _disposed;

    private void Run()
    {
        RunningThreadName = Thread.CurrentThread.Name;
        RunningThreadIsPoolThread = Thread.CurrentThread.IsThreadPoolThread;

        try
        {
            // The startup-delay window holds enumeration back, so the timeout
            // has to clear it or sampling starts against unread sensors and
            // writes zero-valued history rows.
            _sensors.ReadyAsync(_stopCts.Token)
                .WaitAsync(StartupReadyTimeout + Nexus.Service.Lifecycle.StartupDelayGate.Configured, _stopCts.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TimeoutException)
        {
            // Sensor enumeration is still warming up; sample anyway on the
            // schedule below rather than block forever.
        }

        // Scheduling uses the monotonic Stopwatch clock, not DateTime, so a
        // backward wall-clock step (NTP correction, DST edge case) never
        // stalls the wait for hours - Tick still receives DateTime.UtcNow for
        // the persisted sample timestamp, unaffected by this choice.
        var intervalTicks = (long)(TickInterval.TotalSeconds * Stopwatch.Frequency);
        var nextDeadlineTicks = Stopwatch.GetTimestamp() + intervalTicks;
        while (true)
        {
            try
            {
                Tick(DateTime.UtcNow, _stopCts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                MaybeWarn(ex);
            }

            nextDeadlineTicks += intervalTicks;
            var remainingTicks = nextDeadlineTicks - Stopwatch.GetTimestamp();
            if (remainingTicks < 0)
            {
                // Starved past a whole interval: re-anchor instead of banking
                // debt into a burst of catch-up ticks.
                nextDeadlineTicks = Stopwatch.GetTimestamp() + intervalTicks;
                remainingTicks = intervalTicks;
            }

            // A real stop event with a computed timeout: wakes immediately on
            // Stop, otherwise paces the next tick onto the interval boundary.
            var remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
            if (_stopCts.Token.WaitHandle.WaitOne(remaining))
            {
                break;
            }
        }

        // Graceful stop: flush whatever the buffer holds, regardless of the
        // gate - any buffered sample was appended only while Monitoring was
        // on, and skipping this because the gate later flipped off would
        // drop it if shutdown lands before the next tick's own edge-flush.
        try
        {
            if (_buffer.PendingSnapshot().Count > 0 || _appBuffer.PendingSnapshot().Count > 0)
            {
                Flush(DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-sampler] final flush failed: {ex.Message}");
        }
    }

    internal async Task Tick(DateTime nowUtc, CancellationToken ct)
    {
        // Independent of the Monitoring feature gate below: FpsSessionRecorder
        // rides this same demand to capture game sessions, which must keep
        // working even with the Monitoring history pillar off.
        UpdateFpsDemand();

        if (!_gates.Monitoring)
        {
            // Enabled->disabled edge: persist the pre-toggle tail once, then
            // no-op every later tick until re-enabled. The thread keeps
            // running so re-enable resumes on the next tick with no restart.
            if (_monitoringWasEnabled)
            {
                Flush(nowUtc);
                _monitoringWasEnabled = false;
            }
            return;
        }
        _monitoringWasEnabled = true;

        var tsSec = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var sample = await _source.SampleAsync(tsSec, ct).ConfigureAwait(false);
        if (IsFpsTrackingEnabled() && _fps.TryReadCurrentFps(out var currentFps))
        {
            // A fresh reading can still round below 1 (the 50-present window
            // draining while a paused target redraws sparsely). Leave those a
            // gap, matching FpsSessionRecorder's IsValidFrameCount gate, rather
            // than writing a spurious 0 dip into the series.
            var fps = (int)Math.Round(Math.Max(0, currentFps));
            if (fps >= 1)
                sample = sample with { Fps = fps };
        }
        _buffer.Append(sample);
        try
        {
            _sink?.OnSample(sample, nowUtc);
        }
        catch (Exception ex)
        {
            // A throwing sink must not skip _tickCount++ below: that count
            // drives the app-sample interval and the periodic flush, so a
            // swallowed exception here would leave the buffer growing unbounded.
            if (nowUtc - _lastSinkWarnUtc >= WarnThrottle)
            {
                _lastSinkWarnUtc = nowUtc;
                ServiceLog.Warn($"[metrics-sampler] history-tail sink failed: {ex.Message}");
            }
        }
        _tickCount++;

        if (_tickCount % MetricsHistory.AppSampleIntervalSeconds == 0)
        {
            // Independent from the scalar source above: an app-sampling
            // failure must never suppress the flush check below, or a
            // broken per-app read would delay the core scalar flush too.
            try
            {
                _appBuffer.Append(new AppUsageTick(tsSec, _appSource.Sample()));
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[metrics-sampler] app sample failed: {ex.Message}");
            }
        }

        if (_tickCount % MetricsHistory.FlushSeconds == 0)
        {
            Flush(nowUtc);
        }
    }

    private void Flush(DateTime nowUtc)
    {
        var pending = _buffer.PendingSnapshot();
        var isHourly = nowUtc - _lastPruneUtc >= PruneInterval;
        long? pruneCutoffSec = isHourly
            ? new DateTimeOffset(nowUtc).ToUnixTimeSeconds() - MetricsHistory.RetentionDays * 86_400L
            : null;

        if (pending.Count > 0)
        {
            try
            {
                _store.Append(pending, pruneCutoffSec);
                _buffer.RemoveThrough(pending[^1].TsSec);
                if (isHourly)
                {
                    _lastPruneUtc = nowUtc;
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[metrics-sampler] flush failed: {ex.Message}");
                _buffer.TrimToRetentionCap(new DateTimeOffset(nowUtc).ToUnixTimeSeconds());
            }
        }

        FlushApps(nowUtc, pruneCutoffSec);
    }

    // Runs on the same flush cadence as the scalar path but independently:
    // its own try/catch, so a per-app store failure never blocks the scalar
    // RemoveThrough above (and vice versa). Called unconditionally (even
    // with an empty tail) so an hourly prune of the app tables still runs
    // on a tick where no app data happened to buffer.
    private void FlushApps(DateTime nowUtc, long? pruneCutoffSec)
    {
        var appPending = _appBuffer.PendingSnapshot();
        try
        {
            _appStore.Append(appPending, pruneCutoffSec);
            if (appPending.Count > 0)
            {
                _appBuffer.RemoveThrough(appPending[^1].TsSec);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[metrics-sampler] app flush failed: {ex.Message}");
            _appBuffer.TrimToRetentionCap(new DateTimeOffset(nowUtc).ToUnixTimeSeconds());
        }
    }

    // Holds the fps capture demand while the local-data switch is on and
    // something is focused; FpsSessionRecorder rides the same demand while a
    // catalog game holds focus rather than managing a second one.
    private void UpdateFpsDemand()
    {
        bool wanted;
        try
        {
            wanted = IsFpsTrackingEnabled() && _screenTime.GetCurrentSession() is not null;
        }
        catch
        {
            wanted = false;
        }
        _fps.SetDemand(FpsDemandSource, wanted);
    }

    private bool IsFpsTrackingEnabled()
    {
        try { return _config.Load().Fps?.TrackingEnabled ?? true; }
        catch { return true; }
    }

    private void MaybeWarn(Exception ex)
    {
        var now = DateTime.UtcNow;
        if (now - _lastWarnUtc < WarnThrottle)
        {
            return;
        }
        _lastWarnUtc = now;
        ServiceLog.Warn($"[metrics-sampler] tick failed: {ex.Message}");
    }
}
