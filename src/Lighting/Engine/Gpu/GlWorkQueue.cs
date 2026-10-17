using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>One queued GL job: the work, its lane, and when it was queued.</summary>
internal readonly record struct GlJob(Action Work, bool Background, long EnqueuedTicks);

/// <summary>
/// Two-lane work queue for the single GL thread. Live frames (the engine's
/// main canvas, pins, look-ahead) go in the live lane and are taken first;
/// thumbnails, validation and benchmark kernels go in the background lane. A
/// backlog of background jobs delays a live frame by at most the one job
/// already running. To keep background callers (who block without a timeout)
/// from starving when live jobs never let up, one background job runs after
/// every <see cref="MaxLiveStreak"/> consecutive live jobs. After
/// <see cref="CompleteAdding"/> a later Add throws InvalidOperationException
/// and Take drains what is queued, then throws.
/// </summary>
internal sealed class GlWorkQueue
{
    private readonly object _gate = new();
    private readonly Queue<GlJob> _live = new();
    private readonly Queue<GlJob> _background = new();
    private readonly List<GlJob> _deferred = new();
    private readonly List<long> _deferredDue = new();
    private bool _completed;
    private int _liveStreak;

    /// <summary>Consecutive live jobs after which a waiting background job runs.</summary>
    public const int MaxLiveStreak = 8;

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    public void Add(Action work, bool background)
    {
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("queue completed");
            }
            (background ? _background : _live).Enqueue(new GlJob(work, background, System.Diagnostics.Stopwatch.GetTimestamp()));
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>
    /// Queue a background job that becomes runnable after <paramref name="delay"/>.
    /// Used to poll something that is still running without holding the thread.
    /// </summary>
    public void AddDeferred(Action work, TimeSpan delay)
    {
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("queue completed");
            }
            var due = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
            _deferred.Add(new GlJob(work, true, due));
            _deferredDue.Add(due);
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Block for the next job. Throws InvalidOperationException once completed and empty.</summary>
    public GlJob Take()
    {
        lock (_gate)
        {
            while (true)
            {
                if (TryDequeueLocked(out var job))
                {
                    return job;
                }
                if (_completed)
                {
                    throw new InvalidOperationException("queue completed");
                }
                if (_deferred.Count == 0)
                {
                    Monitor.Wait(_gate);
                    continue;
                }
                var earliest = long.MaxValue;
                foreach (var due in _deferredDue)
                {
                    earliest = Math.Min(earliest, due);
                }
                var waitMs = (earliest - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(Math.Max(0.5, waitMs)));
            }
        }
    }

    public bool TryTake(out GlJob job)
    {
        lock (_gate)
        {
            return TryDequeueLocked(out job);
        }
    }

    // Live first, except a background job is due after MaxLiveStreak consecutive live ones.
    private bool TryDequeueLocked(out GlJob job)
    {
        PromoteDeferredLocked();
        if (_live.Count > 0 && !(_background.Count > 0 && _liveStreak >= MaxLiveStreak))
        {
            if (_liveStreak < MaxLiveStreak)
            {
                _liveStreak++;
            }
            job = _live.Dequeue();
            return true;
        }
        if (_background.Count > 0)
        {
            _liveStreak = 0;
            job = _background.Dequeue();
            return true;
        }
        job = default;
        return false;
    }

    // A completed queue releases every deferred job at once so its waiter is not parked on a delay nobody will serve.
    private void PromoteDeferredLocked()
    {
        if (_deferred.Count == 0)
        {
            return;
        }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < _deferred.Count;)
        {
            if (_completed || _deferredDue[i] <= now)
            {
                _background.Enqueue(_deferred[i]);
                _deferred.RemoveAt(i);
                _deferredDue.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    public void CompleteAdding()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }
}

/// <summary>
/// Accumulates queue-wait and run time per lane and reports a one-line window
/// summary, so a backlog of background jobs shows up in nexus-gpu.log with the
/// worst live-frame wait it caused.
/// </summary>
internal sealed class GlQueueStats
{
    private readonly long _windowTicks;
    private long _windowStart;
    private int _liveJobs, _bgJobs;
    private double _liveWaitMax, _bgWaitMax, _bgRunTotal, _bgRunMax;

    public GlQueueStats(TimeSpan window)
    {
        _windowTicks = (long)(window.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Record a finished job; returns a summary line when a window that saw background work closed, else null.</summary>
    public string? Record(in GlJob job, long startTicks, long endTicks)
    {
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        var waitMs = (startTicks - job.EnqueuedTicks) * 1000.0 / freq;
        var runMs = (endTicks - startTicks) * 1000.0 / freq;
        if (job.Background)
        {
            _bgJobs++;
            _bgWaitMax = Math.Max(_bgWaitMax, waitMs);
            _bgRunTotal += runMs;
            _bgRunMax = Math.Max(_bgRunMax, runMs);
        }
        else
        {
            _liveJobs++;
            _liveWaitMax = Math.Max(_liveWaitMax, waitMs);
        }
        if (endTicks - _windowStart < _windowTicks)
        {
            return null;
        }
        string? line = null;
        if (_bgJobs > 0)
        {
            line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[gpu] queue window: live jobs {_liveJobs} max wait {_liveWaitMax:0.0}ms; background jobs {_bgJobs} max wait {_bgWaitMax:0}ms, run total {_bgRunTotal:0}ms max {_bgRunMax:0.0}ms");
        }
        _windowStart = endTicks;
        _liveJobs = _bgJobs = 0;
        _liveWaitMax = _bgWaitMax = _bgRunTotal = _bgRunMax = 0;
        return line;
    }
}
