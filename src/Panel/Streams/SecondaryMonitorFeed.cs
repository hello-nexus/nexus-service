using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Shows a Windows virtual monitor on a bulk-pipe panel: desktop frames go out through the
/// panel's own frame path, and touch read off the panel's IN pipe is injected back onto the
/// monitor. Creating the monitor can take seconds, so everything runs on its own threads.
/// </summary>
internal sealed class SecondaryMonitorFeed : IDisposable
{
    private const int FrameWaitMs = 1000;
    private const int InputReadMs = 250;
    private const int JoinMs = 3000;

    private readonly IVirtualMonitorHost _host;
    private readonly BulkPanelHub _hub;
    private readonly Func<byte[], bool> _push;
    private readonly Func<(bool Flip180, bool Mirror)> _orientation;
    private readonly Action _stateChanged;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _monitorLock = new();
    private readonly Thread _frames;
    private Thread? _touch;
    private IVirtualMonitor? _monitor;
    private volatile string _state = SecondaryMonitorStates.Starting;

    public SecondaryMonitorFeed(
        IVirtualMonitorHost host,
        BulkPanelHub hub,
        Func<byte[], bool> push,
        Func<(bool Flip180, bool Mirror)> orientation,
        Action stateChanged)
    {
        _host = host;
        _hub = hub;
        _push = push;
        _orientation = orientation;
        _stateChanged = stateChanged;
        _frames = new Thread(RunFrames) { IsBackground = true, Name = $"secondary-monitor-{hub.Driver.HandlerId}" };
    }

    public string State => _state;

    public void Start() => _frames.Start();

    private void SetState(string state)
    {
        if (_state == state || _cts.IsCancellationRequested)
        {
            return;
        }
        _state = state;
        ServiceLog.Info($"[{_hub.Driver.HandlerId}] secondary monitor {state}");
        _stateChanged();
    }

    private void RunFrames()
    {
        int width = _hub.Width;
        int height = _hub.Height;
        var monitor = _host.Create(width, height, _cts.Token, out var failure);
        if (monitor is null)
        {
            SetState(failure);
            return;
        }
        lock (_monitorLock)
        {
            // Dispose may have run while Create was still waiting on Windows.
            if (_cts.IsCancellationRequested)
            {
                monitor.Dispose();
                return;
            }
            _monitor = monitor;
        }
        _touch = new Thread(() => RunTouch(monitor, width, height)) { IsBackground = true, Name = $"secondary-monitor-touch-{_hub.Driver.HandlerId}" };
        _touch.Start();

        var frame = new byte[width * height * 4];
        long interval = Stopwatch.Frequency / Math.Max(1, _hub.Driver.Fps);
        long lastSent = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!monitor.TryReadFrame(frame, FrameWaitMs, _cts.Token))
                {
                    continue;
                }
                long wait = lastSent + interval - Stopwatch.GetTimestamp();
                if (wait > 0 && _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency)))
                {
                    break;
                }
                lastSent = Stopwatch.GetTimestamp();
                if (_push(frame))
                {
                    SetState(SecondaryMonitorStates.Active);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[{_hub.Driver.HandlerId}] secondary monitor frame failed: {ex.GetType().Name}: {ex.Message}");
                SetState(SecondaryMonitorStates.Failed);
                break;
            }
        }
    }

    private void RunTouch(IVirtualMonitor monitor, int width, int height)
    {
        var parser = new ZMatricesTouchParser();
        var reports = new List<ZMatricesTouchReport>();
        var tracker = new TouchContactTracker();
        var injections = new List<TouchContactTracker.Injection>();
        var buffer = new byte[64];
        bool logged = false;
        while (!_cts.IsCancellationRequested)
        {
            int read = _hub.ReadInput(buffer, InputReadMs);
            if (read < 0)
            {
                if (_cts.Token.WaitHandle.WaitOne(500))
                {
                    break;
                }
                continue;
            }
            if (read == 0)
            {
                continue;
            }
            reports.Clear();
            parser.Feed(buffer.AsSpan(0, read), reports);
            foreach (var report in reports)
            {
                bool mapped = ZMatricesTouchParser.TryMapToFrame(report, width, height, out int x, out int y);
                // The frame on the glass was turned for the mount; undo that to land on the desktop.
                var (flip180, mirror) = _orientation();
                if (flip180)
                {
                    (x, y) = (width - 1 - x, height - 1 - y);
                }
                if (mirror)
                {
                    x = width - 1 - x;
                }
                injections.Clear();
                tracker.Apply(report.TrackId, report.Phase, x, y, mapped, injections);
                foreach (var injection in injections)
                {
                    bool ok = monitor.InjectTouch(injection.PointerId, injection.Phase, injection.X, injection.Y);
                    if (!logged)
                    {
                        logged = true;
                        ServiceLog.Info($"[{_hub.Driver.HandlerId}] first touch {injection.Phase} at {injection.X},{injection.Y} injected={ok}");
                    }
                }
            }
        }
        injections.Clear();
        tracker.ReleaseAll(injections);
        foreach (var injection in injections)
        {
            monitor.InjectTouch(injection.PointerId, injection.Phase, injection.X, injection.Y);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_frames.IsAlive)
        {
            _frames.Join(JoinMs);
        }
        _touch?.Join(JoinMs);
        IVirtualMonitor? monitor;
        lock (_monitorLock)
        {
            monitor = _monitor;
            _monitor = null;
        }
        monitor?.Dispose();
    }
}
