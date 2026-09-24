using System;
using System.Collections.Generic;

namespace Nexus.Service.Panel.Streams;

public enum StreamSessionState
{
    WaitingForIngest,
    Live,
    Resync,
    Closed,
}

/// <summary>
/// One live stream instance: the frame queue between the ingest reader and
/// the paced writer, plus the resync latch. SessionIds are boot-scoped and
/// re-minted on config change or re-attach, so a stale overlay can never
/// write into a new session.
/// </summary>
public sealed class StreamSession
{
    private readonly object _lock = new();
    private readonly Queue<StreamFrame> _queue = new();
    private readonly int _maxQueuedFrames;
    private long _enqueued;
    private long _sent;
    private long _dropped;
    private long _trims;
    private long _maxIngestGapMs;
    private bool _waitingForIdr = true;
    private bool _ingestBound;
    private bool _transportUp;
    private bool _closed;

    public StreamSession(string sessionId, StreamedPanelDeviceInfo info, string panelDeviceId)
    {
        SessionId = sessionId;
        Info = info;
        PanelDeviceId = panelDeviceId;
        // Enough queue to absorb a transport blip while resuming near-live;
        // when even the newest GOP exceeds it, resync from the next IDR.
        // Raw frames are every one a keyframe and megabytes each, and the device
        // is the ceiling: queueing them buys nothing but latency, so hold a single
        // frame of slack and let the trim keep the newest.
        _maxQueuedFrames = info.Profile.Codec == StreamCodec.RawBgra
            ? 2
            : Math.Max(30, info.Profile.Fps * 2);
    }

    public string SessionId { get; }
    public StreamedPanelDeviceInfo Info { get; }
    public string PanelDeviceId { get; }

    public StreamSessionState State
    {
        get
        {
            lock (_lock)
            {
                if (_closed) return StreamSessionState.Closed;
                if (!_transportUp) return StreamSessionState.Resync;
                return _ingestBound ? StreamSessionState.Live : StreamSessionState.WaitingForIngest;
            }
        }
    }

    public bool Closed
    {
        get { lock (_lock) return _closed; }
    }

    public void Enqueue(StreamFrame frame)
    {
        lock (_lock)
        {
            if (_closed) { frame.Release(); return; }
            _enqueued++;
            _queue.Enqueue(frame);
            if (_queue.Count > _maxQueuedFrames)
                TrimToNewestIdrLocked();
        }
    }

    /// <summary>
    /// Applies one pacing tick: drops a resync prefix (only ever ending at an
    /// IDR), then dequeues the frames to put on the wire this tick.
    /// </summary>
    public IReadOnlyList<StreamFrame> DequeueForTick()
    {
        lock (_lock)
        {
            var decision = PacingPolicy.Decide(
                _queue.Count, FramesUntilIdrLocked(), _waitingForIdr,
                Info.Profile.EffectiveWriteBatchFrames);
            for (var i = 0; i < decision.DropCount; i++) _queue.Dequeue().Release();
            _dropped += decision.DropCount;
            if (decision.ClearWaitingForIdr) _waitingForIdr = false;
            if (decision.SendCount == 0) return Array.Empty<StreamFrame>();
            var send = new List<StreamFrame>(decision.SendCount);
            for (var i = 0; i < decision.SendCount && _queue.Count > 0; i++)
                send.Add(_queue.Dequeue());
            _sent += send.Count;
            return send;
        }
    }

    /// <summary>Transport (re)opened: nothing may hit the wire before an IDR.</summary>
    public void RequireIdrResync()
    {
        lock (_lock) _waitingForIdr = true;
    }

    /// <summary>
    /// New ingest connection: the previous encoder's tail is a dead stream,
    /// and a fresh encoder always opens with SPS/PPS+IDR.
    /// </summary>
    public void ResetForNewIngest()
    {
        lock (_lock)
        {
            _dropped += _queue.Count;
            ClearLocked();
            _waitingForIdr = true;
            _ingestBound = true;
        }
    }

    public void SetIngestBound(bool bound)
    {
        lock (_lock) _ingestBound = bound;
    }

    public void SetTransportUp(bool up)
    {
        lock (_lock) _transportUp = up;
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            ClearLocked();
        }
    }

    /// <summary>Empties the queue, returning every pooled payload as it goes.</summary>
    private void ClearLocked()
    {
        while (_queue.Count > 0) _queue.Dequeue().Release();
    }

    internal int QueueDepthForTest
    {
        get { lock (_lock) return _queue.Count; }
    }

    /// <summary>The ingest reader reports each inter-frame arrival gap;
    /// change-driven capture makes gaps routine on slow content, so only the
    /// window maximum is surfaced (in the stats line), never per-frame.</summary>
    public void RecordIngestGap(long gapMs)
    {
        lock (_lock)
        {
            if (gapMs > _maxIngestGapMs) _maxIngestGapMs = gapMs;
        }
    }

    /// <summary>Cumulative flow counters; deltas between reads give the
    /// interval's in/out rates, which expose a producer/consumer rate gap
    /// the individual warn lines cannot. Dropped includes trimmed frames,
    /// so enqueued - sent - dropped reconciles with depth. MaxIngestGapMs
    /// is the maximum since the previous snapshot (reset on read).</summary>
    public (long Enqueued, long Sent, long Dropped, long Trims, int Depth, long MaxIngestGapMs) StatsSnapshot()
    {
        lock (_lock)
        {
            var maxGap = _maxIngestGapMs;
            _maxIngestGapMs = 0;
            return (_enqueued, _sent, _dropped, _trims, _queue.Count, maxGap);
        }
    }

    private int FramesUntilIdrLocked()
    {
        var i = 0;
        foreach (var frame in _queue)
        {
            if (frame.IsIdr) return i;
            i++;
        }
        return -1;
    }

    // Keeps only the suffix starting at the newest IDR so the decoder can
    // resume without a mid-GOP gap. When there is no IDR, or the newest GOP
    // itself already exceeds the cap (IDR stuck at the head), holding it
    // would grow unbounded; clear and resync from the next IDR instead.
    private void TrimToNewestIdrLocked()
    {
        _trims++;
        var frames = _queue.ToArray();
        var newestIdr = -1;
        for (var i = frames.Length - 1; i >= 0; i--)
        {
            if (frames[i].IsIdr) { newestIdr = i; break; }
        }
        _queue.Clear();
        if (newestIdr < 0 || frames.Length - newestIdr > _maxQueuedFrames)
        {
            _dropped += frames.Length;
            _waitingForIdr = true;
            foreach (var frame in frames) frame.Release();
            return;
        }
        _dropped += newestIdr;
        for (var i = 0; i < newestIdr; i++) frames[i].Release();
        for (var i = newestIdr; i < frames.Length; i++)
            _queue.Enqueue(frames[i]);
    }
}
