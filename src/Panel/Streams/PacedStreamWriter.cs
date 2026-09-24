using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Dedicated per-session thread that clocks frames onto the device transport
/// at the display rate. The device player renders on arrival, so the wire IS
/// the display clock: encoder bursts sent as-is show as speed wobble, and any
/// mid-GOP gap smears until the next IDR. Pacing decisions live in
/// <see cref="PacingPolicy"/>; this class only keeps time and writes.
/// </summary>
public sealed class PacedStreamWriter : IDisposable
{
    private readonly StreamSession _session;
    private readonly int _fps;
    private readonly int _batchFrames;
    private readonly Action<IStreamedPanelTransport, Exception> _onTransportFault;
    private readonly object _transportLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private IStreamedPanelTransport? _transport;
    private long _lastWriteStallLogTicks;
    private long _lastStatsLogTicks;
    private (long Enqueued, long Sent, long Dropped, long Trims, int Depth, long MaxIngestGapMs) _lastStats;
    private volatile bool _disposed;

    public PacedStreamWriter(StreamSession session, Action<IStreamedPanelTransport, Exception> onTransportFault)
    {
        _session = session;
        _fps = Math.Clamp(session.Info.Profile.Fps, 1, 240);
        _batchFrames = session.Info.Profile.EffectiveWriteBatchFrames;
        _onTransportFault = onTransportFault;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"stream-writer-{session.Info.Serial}",
        };
        _thread.Start();
    }

    /// <summary>Swap in a (re)opened transport; arms the session's IDR resync latch.</summary>
    public void SetTransport(IStreamedPanelTransport transport)
    {
        lock (_transportLock) _transport = transport;
        _session.RequireIdrResync();
        _wake.Set();
    }

    public void ClearTransport()
    {
        lock (_transportLock) _transport = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        var exited = Thread.CurrentThread == _thread || _thread.Join(2000);
        // A thread that outlived the join (a transport write still inside
        // its bounded timeout) may touch _wake after this returns; leak the
        // handle rather than hand it a disposed one.
        if (exited) _wake.Dispose();
    }

    private void Run()
    {
        // Windows rounds Thread.Sleep to the ~15ms scheduler quantum by
        // default, which jitters the tick visibly; request finer timer
        // resolution while this thread lives.
        if (OperatingSystem.IsWindows()) TimeBeginPeriod(1);
        try
        {
            // One tick per batch: fps stays the frame rate on the wire while
            // batched frames share a single write (and round trip).
            var interval = Stopwatch.Frequency * _batchFrames / _fps;
            var deadline = Stopwatch.GetTimestamp() + interval;
            while (!_disposed)
            {
                IStreamedPanelTransport? transport;
                lock (_transportLock) transport = _transport;
                if (transport is null || !transport.IsOpen)
                {
                    _wake.WaitOne(100);
                    deadline = Stopwatch.GetTimestamp() + interval;
                    continue;
                }

                WaitUntil(deadline);
                var now = Stopwatch.GetTimestamp();
                if (_lastStatsLogTicks == 0) _lastStatsLogTicks = now;
                if (now - _lastStatsLogTicks > Stopwatch.Frequency * 30)
                {
                    var stats = _session.StatsSnapshot();
                    var secs = (now - _lastStatsLogTicks) / (double)Stopwatch.Frequency;
                    ServiceLog.Info(
                        $"[streamed-panel] stats serial={_session.Info.Serial} in={(stats.Enqueued - _lastStats.Enqueued) / secs:F1}fps " +
                        $"out={(stats.Sent - _lastStats.Sent) / secs:F1}fps depth={stats.Depth} " +
                        $"dropped={stats.Dropped - _lastStats.Dropped} trims={stats.Trims - _lastStats.Trims} " +
                        $"maxInGap={stats.MaxIngestGapMs}ms");
                    _lastStats = stats;
                    _lastStatsLogTicks = now;
                }
                // A late tick (blocked write, empty stretch) must not bank
                // debt that later bursts the wire; re-anchor instead. What
                // the re-anchor leaves queued drains through the pacing
                // policy's bounded catch-up rather than a same-tick flush.
                if (now - deadline > Stopwatch.Frequency / 10) deadline = now;
                deadline += interval;

                var frames = _session.DequeueForTick();
                if (frames.Count == 0) continue;
                try
                {
                    SendTick(transport, frames);
                }
                finally
                {
                    // This tick owns the dequeued frames whether or not the write
                    // took them, so the pooled payloads go back here and nowhere else.
                    foreach (var frame in frames) frame.Release();
                }
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows()) TimeEndPeriod(1);
        }
    }

    private void SendTick(IStreamedPanelTransport transport, IReadOnlyList<StreamFrame> frames)
    {
        if (_batchFrames > 1 && frames.Count > 1)
        {
            // The batch shares one write so the transport pays one
            // round trip for all of it.
            var total = 0;
            var anyIdr = false;
            foreach (var frame in frames)
            {
                total += frame.Bytes.Length;
                anyIdr |= frame.IsIdr;
            }
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
            try
            {
                var offset = 0;
                foreach (var frame in frames)
                {
                    frame.Bytes.CopyTo(buffer.AsSpan(offset));
                    offset += frame.Bytes.Length;
                }
                TryWrite(transport, buffer.AsSpan(0, total), anyIdr);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            foreach (var frame in frames)
            {
                if (!TryWrite(transport, frame.Bytes, frame.IsIdr)) break;
            }
        }
    }

    private bool TryWrite(IStreamedPanelTransport transport, ReadOnlySpan<byte> payload, bool isIdr)
    {
        try
        {
            var writeStart = Stopwatch.GetTimestamp();
            transport.Write(payload);
            // A blocked write is downstream backpressure (socket buffer, adb
            // forwarding, device fifo); on a render-on-arrival device every
            // stall shows on glass as a time snap when the backlog flushes.
            var writeMs = (Stopwatch.GetTimestamp() - writeStart) * 1000 / Stopwatch.Frequency;
            if (writeMs > 50 && Stopwatch.GetTimestamp() - _lastWriteStallLogTicks > Stopwatch.Frequency)
            {
                _lastWriteStallLogTicks = Stopwatch.GetTimestamp();
                ServiceLog.Warn($"[streamed-panel] write stalled {writeMs}ms serial={_session.Info.Serial} ({payload.Length}b{(isIdr ? " idr" : "")})");
            }
            return true;
        }
        catch (Exception ex)
        {
            ClearTransport();
            _onTransportFault(transport, ex);
            return false;
        }
    }

    private void WaitUntil(long deadline)
    {
        // Sleeps are capped so _disposed is observed promptly: a batched
        // tick interval can far exceed Dispose's thread-join timeout.
        while (!_disposed)
        {
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            var ms = remaining * 1000 / Stopwatch.Frequency;
            if (ms >= 2) Thread.Sleep((int)Math.Min(ms - 1, 50));
            else if (ms >= 1) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);
}
