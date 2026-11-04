using System;
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
    private const int JoinMs = 3000;
    private const int CapReadMs = 500;

    private readonly IVirtualMonitorHost _host;
    private readonly BulkPanelHub _hub;
    private readonly Func<byte[], bool> _push;
    private readonly Func<(bool Flip180, bool Mirror)> _orientation;
    private readonly Action _stateChanged;
    private readonly Func<int?>? _fpsCap;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _monitorLock = new();
    private readonly Thread _frames;
    private BulkPanelTouchReader? _touch;
    private IVirtualMonitor? _monitor;
    private volatile string _state = SecondaryMonitorStates.Starting;
    private volatile bool _finished;

    public SecondaryMonitorFeed(
        IVirtualMonitorHost host,
        BulkPanelHub hub,
        Func<byte[], bool> push,
        Func<(bool Flip180, bool Mirror)> orientation,
        Action stateChanged,
        Func<int?>? fpsCap = null)
    {
        _fpsCap = fpsCap;
        _host = host;
        _hub = hub;
        _push = push;
        _orientation = orientation;
        _stateChanged = stateChanged;
        _frames = new Thread(RunFrames) { IsBackground = true, Name = $"secondary-monitor-{hub.Driver.HandlerId}" };
    }

    public string State => _state;

    /// <summary>The frame thread has stopped: the monitor never came up or went away.</summary>
    public bool Finished => _finished;

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
        try
        {
            PumpFrames();
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[{_hub.Driver.HandlerId}] secondary monitor failed: {ex.GetType().Name}: {ex.Message}");
            SetState(SecondaryMonitorStates.Failed);
        }
        finally
        {
            _finished = true;
        }
    }

    private void PumpFrames()
    {
        int width = _hub.Width;
        int height = _hub.Height;
        var monitor = _host.Create(width, height, _hub.Driver.Dpi, _cts.Token, out var failure);
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
        if (_hub.Driver.HasTouch)
        {
            _touch = new BulkPanelTouchReader(_hub, width, height, _orientation, injection => SafeInject(monitor, injection), "secondary-monitor-touch", _cts.Token);
            _touch.Start();
        }

        var frame = new byte[width * height * 4];
        long interval = FrameInterval();
        long nextCapReadMs = Environment.TickCount64 + CapReadMs;
        long lastSent = 0;
        bool rejected = false;
        var retry = new byte[frame.Length];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!monitor.TryReadFrame(frame, FrameWaitMs, _cts.Token))
                {
                    if (!monitor.IsAlive)
                    {
                        ServiceLog.Warn($"[{_hub.Driver.HandlerId}] secondary monitor went away");
                        SetState(SecondaryMonitorStates.Failed);
                        break;
                    }
                    // A rejected frame is not offered again by a still desktop, so the last one is resent.
                    if (rejected && _push(retry))
                    {
                        rejected = false;
                        lastSent = Stopwatch.GetTimestamp();
                        SetState(SecondaryMonitorStates.Active);
                    }
                    continue;
                }
                // The record's cap is polled, not read per frame: reading it clones the panel record.
                if (Environment.TickCount64 >= nextCapReadMs)
                {
                    interval = FrameInterval();
                    nextCapReadMs = Environment.TickCount64 + CapReadMs;
                }
                long wait = lastSent + interval - Stopwatch.GetTimestamp();
                if (wait > 0 && _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency)))
                {
                    break;
                }
                lastSent = Stopwatch.GetTimestamp();
                rejected = !_push(frame);
                if (!rejected)
                {
                    SetState(SecondaryMonitorStates.Active);
                }
                else
                {
                    // The capture buffer can be partly overwritten by a read that fails, so the resend keeps its own copy.
                    Buffer.BlockCopy(frame, 0, retry, 0, frame.Length);
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

    private long FrameInterval()
    {
        int? cap = null;
        try { cap = _fpsCap?.Invoke(); }
        catch (Exception ex) { ServiceLog.Warn($"[{_hub.Driver.HandlerId}] fps cap read failed: {ex.GetType().Name}: {ex.Message}"); }
        return Stopwatch.Frequency / Math.Max(1, StreamFpsCap.Effective(_hub.Driver.Fps, cap));
    }

    // The helper pipe can break under us (logoff, helper restart); an escaped exception on
    // this raw thread would end the service.
    private bool SafeInject(IVirtualMonitor monitor, TouchContactTracker.Injection injection)
    {
        try
        {
            return monitor.InjectTouch(injection.PointerId, injection.Phase, injection.X, injection.Y);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{_hub.Driver.HandlerId}] touch injection failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_frames.IsAlive)
        {
            _frames.Join(JoinMs);
        }
        _touch?.Dispose();
        IVirtualMonitor? monitor;
        lock (_monitorLock)
        {
            monitor = _monitor;
            _monitor = null;
        }
        monitor?.Dispose();
    }
}
