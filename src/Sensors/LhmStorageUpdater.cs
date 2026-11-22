using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Sensors;

/// <summary>
/// Runs storage drive updates on one background thread so a drive stuck in I/O retries cannot hold the hardware walk
/// that the fan-curve engine and the freshness check depend on. A drive with an update still queued or running is not requested again.
/// One thread keeps the process-wide SMART cycle static each job sets valid for the job that follows it.
/// Free of LibreHardwareMonitor types so it tests on every platform.
/// </summary>
internal sealed class LhmStorageUpdater : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<(string Key, Action Work)> _queue = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private Thread? _thread;
    private bool _disposed;

    /// <summary>Queues <paramref name="work"/> for <paramref name="key"/>. False means that key still has an update queued or running, so this one was dropped.</summary>
    internal bool Request(string key, Action work)
    {
        lock (_gate)
        {
            if (_disposed || !_pending.Add(key))
            {
                return false;
            }
            _queue.Enqueue((key, work));
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "lhm-storage" };
                _thread.Start();
            }
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>Waits until nothing is queued or running. False means <paramref name="timeout"/> passed first.</summary>
    internal bool WaitIdle(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (_pending.Count > 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0 || _disposed)
                {
                    return false;
                }
                Monitor.Wait(_gate, (int)left);
            }
            return true;
        }
    }

    internal bool IsBusy(string key)
    {
        lock (_gate)
        {
            return _pending.Contains(key);
        }
    }

    private void Run()
    {
        while (true)
        {
            (string Key, Action Work) job;
            lock (_gate)
            {
                while (_queue.Count == 0)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    Monitor.Wait(_gate);
                }
                job = _queue.Dequeue();
            }
            try
            {
                job.Work();
            }
            catch
            {
                // The work logs its own failures; the worker must outlive one.
            }
            finally
            {
                lock (_gate)
                {
                    _pending.Remove(job.Key);
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }
}
