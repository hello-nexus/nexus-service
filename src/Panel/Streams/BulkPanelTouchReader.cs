using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Reads a bulk-pipe panel's touch pipe on its own thread and hands each contact, on the
/// upright frame of the given size, to a sink. Only one reader may run per panel: the pipe
/// has one consumer.
/// </summary>
internal sealed class BulkPanelTouchReader : IDisposable
{
    private const int InputReadMs = 250;
    private const int ReadFailBackoffMs = 500;
    private const int JoinMs = 3000;

    private readonly BulkPanelHub _hub;
    private readonly int _width;
    private readonly int _height;
    private readonly Func<(bool Flip180, bool Mirror)> _orientation;
    private readonly Func<TouchContactTracker.Injection, bool> _sink;
    private readonly CancellationTokenSource _cts;
    private readonly Thread _thread;
    // Thread exit and Dispose must both happen before the linked source can be released.
    private int _releasesLeft = 2;
    private int _started;
    private int _disposed;

    public BulkPanelTouchReader(BulkPanelHub hub, int width, int height, Func<(bool Flip180, bool Mirror)> orientation, Func<TouchContactTracker.Injection, bool> sink, string name, CancellationToken stop = default)
    {
        // A stop token covers a reader its owner never got to dispose.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _hub = hub;
        _width = width;
        _height = height;
        _orientation = orientation;
        _sink = sink;
        _thread = new Thread(RunGuarded) { IsBackground = true, Name = $"{name}-{hub.Driver.HandlerId}" };
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _started, 1) == 0)
        {
            _thread.Start();
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _releasesLeft) == 0)
        {
            _cts.Dispose();
        }
    }

    private void RunGuarded()
    {
        try
        {
            // A stalled read in a disposed reader holds the slot, so a successor never splits a report with it.
            _hub.WaitForInputReader(_cts.Token);
            try
            {
                Run();
            }
            finally
            {
                _hub.InputReaderGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[{_hub.Driver.HandlerId}] touch reader stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Release();
        }
    }

    private void Run()
    {
        if (_hub.Driver.CreateTouchDecoder() is not { } decoder)
        {
            return;
        }
        var reports = new List<PanelTouchReport>();
        var tracker = new TouchContactTracker();
        var injections = new List<TouchContactTracker.Injection>();
        var buffer = new byte[64];
        bool logged = false;
        ServiceLog.Info($"[{_hub.Driver.HandlerId}] touch reader on ({_thread.Name})");
        while (!_cts.IsCancellationRequested)
        {
            int read = _hub.ReadInput(buffer, InputReadMs);
            if (read < 0)
            {
                if (_cts.Token.WaitHandle.WaitOne(ReadFailBackoffMs))
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
            decoder.Decode(buffer.AsSpan(0, read), _width, _height, reports);
            foreach (var report in reports)
            {
                int x = report.X;
                int y = report.Y;
                // The frame on the glass was turned for the mount; undo that to land on the source.
                var (flip180, mirror) = _orientation();
                if (flip180)
                {
                    (x, y) = (_width - 1 - x, _height - 1 - y);
                }
                if (mirror)
                {
                    x = _width - 1 - x;
                }
                injections.Clear();
                tracker.Apply(report.TrackId, report.Phase, x, y, report.Mapped, injections);
                foreach (var injection in injections)
                {
                    bool ok = SafeSink(injection);
                    if (!logged)
                    {
                        logged = true;
                        ServiceLog.Info($"[{_hub.Driver.HandlerId}] first touch {injection.Phase} at {injection.X},{injection.Y} delivered={ok}");
                    }
                }
            }
        }
        injections.Clear();
        tracker.ReleaseAll(injections);
        foreach (var injection in injections)
        {
            SafeSink(injection);
        }
    }

    // An escaped exception on this raw thread would end the service.
    private bool SafeSink(TouchContactTracker.Injection injection)
    {
        try
        {
            return _sink(injection);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{_hub.Driver.HandlerId}] touch delivery failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Stops the thread; contacts still down are released through the sink first.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _cts.Cancel();
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            Release();
        }
        else if (_thread.IsAlive)
        {
            _thread.Join(JoinMs);
        }
        Release();
    }
}
