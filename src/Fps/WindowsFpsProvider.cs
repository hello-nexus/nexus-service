using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Fps;

public sealed class WindowsFpsProvider : IFpsProvider
{
    private const int FocusScanDelayMs = 250;
    private const int StaleFrameMs = 2000;
    private const float GaugeMaximumFps = 240f;
    private const float GaugeMaximumFrameMs = 50f;
    private const string SensorName = "FPS";
    private const string FrameTimeName = "Frame Time";
    private const double CleanExitBackoffSeconds = 5;
    private const double CleanExitBackoffCapSeconds = 300;
    private const int CleanExitStreakCap = 6;
    private const double HealthyRunSeconds = 60;
    private const double FailureBackoffSeconds = 10;
    private const int LogIntervalSeconds = 30;

    // Process-wide so two providers (or a restart inside one) can never mint
    // the same logger name; a stale task's stop by name then cannot reach a
    // newer capture's session.
    private static int s_sessionSeq;

    // The nexus-service runs as LocalSystem in Session 0, which has no
    // interactive desktop - GetForegroundWindow() from here always returns
    // nothing useful. The user-session helper polls foreground via
    // ScreenTimePoller and publishes the PID through IScreenTimeProvider,
    // so we read the target PID from there.
    private readonly IScreenTimeProvider _screenTime;
    private readonly IFpsTraceRunner _runner;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly object _transitionGate = new();
    private readonly FpsCalculator _calculator = new();
    private readonly HashSet<string> _demands = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _focusTask;
    private Task? _traceTask;
    private string? _sessionName;
    private int _targetPid;
    private string _targetName = "";
    private double _fps;
    private bool _hasValue;
    private DateTime _lastPresentUtc = DateTime.MinValue;
    private DateTime _nextStartAllowedUtc = DateTime.MinValue;
    private DateTime _lastErrorLoggedUtc = DateTime.MinValue;
    private DateTime _captureStartedUtc = DateTime.MinValue;
    private readonly double _cleanExitBaseSeconds;
    // Consecutive captures that ended on their own without delivering a
    // present or running a healthy stretch; doubles the restart backoff.
    private int _cleanExitStreak;

    public WindowsFpsProvider(IScreenTimeProvider screenTime)
        : this(screenTime, new EtwFpsTraceRunner(), CleanExitBackoffSeconds, TimeProvider.System)
    {
    }

    internal WindowsFpsProvider(
        IScreenTimeProvider screenTime,
        IFpsTraceRunner runner,
        double cleanExitBaseSeconds,
        TimeProvider time)
    {
        _screenTime = screenTime;
        _runner = runner;
        _cleanExitBaseSeconds = cleanExitBaseSeconds;
        _time = time;
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    internal static string NextSessionName() =>
        EtwFpsTraceRunner.SessionPrefix + Environment.ProcessId + "-" + Interlocked.Increment(ref s_sessionSeq);

    /// <summary>Adds or removes <paramref name="source"/> from the demand set and starts
    /// or stops capture to match. Call every tick with a fixed source id (idempotent
    /// against the current state) rather than once on an edge, so two independent
    /// callers (monitoring subscribers, a Tryx overlay) never fight over one flag.</summary>
    public void SetDemand(string source, bool wanted)
    {
        lock (_gate)
        {
            if (wanted) _demands.Add(source);
            else _demands.Remove(source);
        }

        // Serialize the transition and re-read the demand set inside it: when a clear
        // and an add race, the last caller through _transitionGate acts on the final
        // set (StartCapture/StopCapture are idempotent), so a capture a live source
        // still wants is never torn down by a stale decision. Lock order is always
        // _transitionGate then _gate; the mutate above never holds _transitionGate.
        lock (_transitionGate)
        {
            bool want;
            lock (_gate) { want = _demands.Count > 0; }
            if (want)
            {
                // Demand is re-asserted every tick, so a capture whose trace or
                // focus task ended on its own is noticed here and restarted
                // once its backoff passes.
                RecoverIfTasksEnded();
                StartCapture();
            }
            else
            {
                StopCapture();
            }
        }
    }

    private void StartCapture()
    {
        CancellationTokenSource cts;
        string sessionName;
        lock (_gate)
        {
            if (_cts is not null || Now() < _nextStartAllowedUtc)
                return;

            cts = new CancellationTokenSource();
            _cts = cts;
            sessionName = NextSessionName();
            _sessionName = sessionName;
            _captureStartedUtc = Now();
            ResetTargetLocked();
        }

        var focusTask = Task.Run(() => FocusLoopAsync(cts.Token), cts.Token);
        var traceTask = Task.Run(() => TraceLoop(cts, sessionName));
        lock (_gate)
        {
            if (ReferenceEquals(_cts, cts))
            {
                _focusTask = focusTask;
                _traceTask = traceTask;
            }
        }
    }

    private void StopCapture()
    {
        CancellationTokenSource? cts;
        Task? focusTask;
        Task? traceTask;
        string? sessionName;
        lock (_gate)
        {
            cts = _cts;
            if (cts is null)
                return;

            _cts = null;
            focusTask = _focusTask;
            traceTask = _traceTask;
            _focusTask = null;
            _traceTask = null;
            sessionName = _sessionName;
            _sessionName = null;
            ResetTargetLocked();
        }

        try { cts.Cancel(); } catch { }
        if (sessionName is not null)
            _runner.Stop(sessionName);

        _ = Task.WhenAll(IgnoreFaults(focusTask), IgnoreFaults(traceTask))
            .ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    // Tears a capture down when its trace or focus task finished without a
    // stop request. TraceLoop reports its own early exits; this catches any
    // path that ended a task without going through FailCapture.
    private void RecoverIfTasksEnded()
    {
        CancellationTokenSource? cts;
        bool ended;
        lock (_gate)
        {
            cts = _cts;
            ended = cts is not null
                && ((_traceTask?.IsCompleted ?? false) || (_focusTask?.IsCompleted ?? false));
        }

        if (ended)
            FailCapture(cts!, "capture task ended unexpectedly", cleanExit: true);
    }

    public HardwareComponent GetComponent()
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                return new HardwareComponent
                {
                    Id = "fps",
                    Name = SensorName,
                    Sensors = new List<HardwareSensor>(),
                };
            }

            var parentName = string.IsNullOrWhiteSpace(_targetName)
                ? SensorName
                : $"{SensorName} ({_targetName})";
            var isFresh = _hasValue
                && (Now() - _lastPresentUtc).TotalMilliseconds <= StaleFrameMs;
            var fpsValue = isFresh ? (float)Math.Max(0, _fps) : 0f;
            var fpsFormatted = isFresh ? $"{fpsValue:F0}" : "-";
            var frameMs = isFresh && fpsValue > 0 ? 1000f / fpsValue : 0f;
            var frameMsFormatted = isFresh && fpsValue > 0 ? $"{frameMs:F1} ms" : "-";
            var parent = new SensorParent { Id = "fps", Name = parentName };

            return new HardwareComponent
            {
                Id = "fps",
                Name = parentName,
                Sensors = new List<HardwareSensor>
                {
                    new()
                    {
                        Id = "fps/current",
                        Name = SensorName,
                        Type = "Framerate",
                        Value = fpsValue,
                        Min = 0,
                        Max = fpsValue,
                        Average = fpsValue,
                        Usage = fpsValue,
                        TheoreticalMaximum = GaugeMaximumFps,
                        Units = "fps",
                        Formatted = fpsFormatted,
                        FormattedMin = "0",
                        FormattedMax = $"{GaugeMaximumFps:F0}",
                        FormattedAverage = fpsFormatted,
                        FormattedUsage = fpsFormatted,
                        Parent = parent,
                    },
                    new()
                    {
                        Id = "fps/frame-time",
                        Name = FrameTimeName,
                        Type = "FrameTime",
                        Value = frameMs,
                        Min = 0,
                        Max = frameMs,
                        Average = frameMs,
                        Usage = frameMs,
                        TheoreticalMaximum = GaugeMaximumFrameMs,
                        Units = "ms",
                        Formatted = frameMsFormatted,
                        FormattedMin = "0 ms",
                        FormattedMax = $"{GaugeMaximumFrameMs:F0} ms",
                        FormattedAverage = frameMsFormatted,
                        FormattedUsage = frameMsFormatted,
                        Parent = parent,
                    },
                },
            };
        }
    }

    public void Dispose() => StopCapture();

    private async Task FocusLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var target = GetForegroundTargetFromHelper();
                lock (_gate)
                {
                    if (_cts is null || token.IsCancellationRequested)
                        return;

                    if (_targetPid != target.Pid)
                    {
                        _targetPid = target.Pid;
                        _targetName = target.Name;
                        _calculator.Reset();
                        _fps = 0;
                        _hasValue = false;
                        _lastPresentUtc = DateTime.MinValue;
                    }
                }

                await Task.Delay(FocusScanDelayMs, token);
            }
        }
        catch (TaskCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogLimited($"[fps] foreground scan failed: {ex.Message}");
        }
    }

    private void TraceLoop(CancellationTokenSource cts, string sessionName)
    {
        var token = cts.Token;
        try
        {
            if (token.IsCancellationRequested)
                return;

            _runner.SweepStale(sessionName);

            _runner.Run(sessionName, (pid, ticks) => OnPresent(cts, pid, ticks), token);

            // Run only returns on its own when the logger was stopped from
            // outside (logman, another tool, the kernel). Capture is dead but
            // _cts is still set, so without this nothing would ever restart it.
            if (!token.IsCancellationRequested)
                FailCapture(cts, "ETW trace ended unexpectedly", cleanExit: true);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            FailCapture(cts, ex.Message, cleanExit: false, permanent: ex is UnauthorizedAccessException);
        }
        finally
        {
            // The name is unique to this capture, so this can only ever stop
            // this task's own logger.
            _runner.Stop(sessionName);
            lock (_gate)
            {
                if (_sessionName == sessionName)
                    _sessionName = null;
            }
        }
    }

    private void OnPresent(CancellationTokenSource cts, int pid, long ticks)
    {
        var targetPid = Volatile.Read(ref _targetPid);
        if (targetPid <= 0 || pid != targetPid)
            return;

        lock (_gate)
        {
            if (!ReferenceEquals(_cts, cts) || pid != _targetPid)
                return;

            _cleanExitStreak = 0;
            _fps = _calculator.AddFrameTicks(ticks);
            _hasValue = _calculator.Count >= 2;
            _lastPresentUtc = Now();
        }
    }

    // Reads the same fields GetComponent()'s fps/current sensor reads, so
    // the stored history series and the live sensor always agree.
    public bool TryReadCurrentFps(out double fps)
    {
        lock (_gate)
        {
            var isFresh = _cts is not null && _hasValue
                && (Now() - _lastPresentUtc).TotalMilliseconds <= StaleFrameMs;
            if (isFresh)
            {
                fps = _fps;
                return true;
            }
        }
        fps = 0;
        return false;
    }

    // Only the capture that is still current may fail it: a stale task's late
    // error must not tear down a newer capture. Access denied backs off
    // permanently; a clean exit doubles its backoff per
    // consecutive occurrence up to a cap, so a logger that keeps ending
    // immediately is not recreated every few seconds forever.
    private void FailCapture(CancellationTokenSource cts, string reason, bool cleanExit, bool permanent = false)
    {
        string? sessionName;
        lock (_gate)
        {
            if (!ReferenceEquals(_cts, cts))
                return;

            _cts = null;
            _focusTask = null;
            _traceTask = null;
            sessionName = _sessionName;
            _sessionName = null;
            double backoffSeconds = FailureBackoffSeconds;
            if (cleanExit)
            {
                if ((Now() - _captureStartedUtc).TotalSeconds >= HealthyRunSeconds)
                {
                    _cleanExitStreak = 0;
                }

                backoffSeconds = Math.Min(
                    CleanExitBackoffCapSeconds,
                    _cleanExitBaseSeconds * (1 << Math.Min(_cleanExitStreak, CleanExitStreakCap)));
                _cleanExitStreak++;
            }

            _nextStartAllowedUtc = permanent
                ? DateTime.MaxValue
                : Now().AddSeconds(backoffSeconds);
            ResetTargetLocked();
        }

        try { cts.Cancel(); } catch { }
        if (sessionName is not null)
            _runner.Stop(sessionName);

        LogLimited($"[fps] ETW capture unavailable: {reason}");
    }

    // One shared limiter for every [fps] error line, so a persistently
    // failing environment logs at most once per interval.
    private void LogLimited(string line)
    {
        lock (_gate)
        {
            var now = Now();
            if ((now - _lastErrorLoggedUtc).TotalSeconds < LogIntervalSeconds)
                return;

            _lastErrorLoggedUtc = now;
        }

        Console.Error.WriteLine(line);
    }

    private void ResetTargetLocked()
    {
        _targetPid = 0;
        _targetName = "";
        _fps = 0;
        _hasValue = false;
        _lastPresentUtc = DateTime.MinValue;
        _calculator.Reset();
    }

    private static async Task IgnoreFaults(Task? task)
    {
        if (task is null)
            return;

        try { await task.ConfigureAwait(false); }
        catch { }
    }

    private (int Pid, string Name) GetForegroundTargetFromHelper()
    {
        FocusSession? session;
        try { session = _screenTime.GetCurrentSession(); }
        catch { return (0, ""); }

        if (session is null) return (0, "");
        if (!int.TryParse(session.Id, out var pid) || pid <= 0) return (0, "");
        return (pid, session.Name ?? "");
    }
}
