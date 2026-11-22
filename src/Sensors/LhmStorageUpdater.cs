using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Sensors;

/// <summary>
/// Runs storage drive updates off the hardware walk, one worker thread per drive, so a drive stuck in I/O retries
/// delays neither the fan-curve engine nor the other drives. A drive with an update still queued or running is not requested again.
/// A drive whose update runs past <c>overdueAfter</c> is reported once through <c>onOverdue</c>, and through <c>onRecovered</c> when it returns.
/// LibreHardwareMonitor reads the process-wide SMART cycle static only at the top of <c>StorageDevice.Update</c>, before any drive I/O,
/// so the static is set under a start gate that a job gives up when its update returns or after <c>startGrace</c>, whichever is first.
/// That keeps one drive's setting from reaching another's update without letting a stuck update hold the gate.
/// Free of LibreHardwareMonitor types so it tests on every platform.
/// </summary>
internal sealed class LhmStorageUpdater : IDisposable
{
    /// <summary>A queued update. Done is set when it finished, was dropped, or failed.</summary>
    internal sealed class Job
    {
        private readonly ManualResetEventSlim _done = new(false);

        internal bool Wait(TimeSpan timeout) => _done.Wait(timeout);

        internal bool IsDone => _done.IsSet;

        internal void Complete() => _done.Set();
    }

    private sealed class Worker
    {
        public Thread? Thread;
        public Job? Next;
        public Action? NextBefore;
        public Action? NextWork;
        public string NextName = "";
        public bool Running;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly TimeSpan _startGrace;
    private readonly TimeSpan _overdueAfter;
    private readonly Action<string, string, TimeSpan>? _onOverdue;
    private readonly Action<string, string>? _onRecovered;
    private bool _disposed;

    internal LhmStorageUpdater(
        TimeSpan startGrace,
        TimeSpan overdueAfter,
        Action<string, string, TimeSpan>? onOverdue = null,
        Action<string, string>? onRecovered = null)
    {
        _startGrace = startGrace;
        _overdueAfter = overdueAfter;
        _onOverdue = onOverdue;
        _onRecovered = onRecovered;
    }

    /// <summary>
    /// Queues an update for <paramref name="key"/>: <paramref name="before"/> runs under the start gate right before <paramref name="work"/>.
    /// Null means that key still has an update queued or running (or the updater is disposed), so this one was dropped.
    /// </summary>
    internal Job? Request(string key, string name, Action before, Action work)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }
            if (!_workers.TryGetValue(key, out var worker))
            {
                worker = new Worker();
                _workers[key] = worker;
            }
            if (worker.Next is not null || worker.Running)
            {
                return null;
            }
            var job = new Job();
            worker.Next = job;
            worker.NextBefore = before;
            worker.NextWork = work;
            worker.NextName = name;
            if (worker.Thread is null)
            {
                worker.Thread = new Thread(() => Run(key, worker)) { IsBackground = true, Name = "lhm-storage" };
                worker.Thread.Start();
            }
            Monitor.PulseAll(_gate);
            return job;
        }
    }

    internal bool IsBusy(string key)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(key, out var w) && (w.Next is not null || w.Running);
        }
    }

    /// <summary>Waits until <paramref name="key"/> has nothing queued or running. False means it was still busy after <paramref name="timeout"/>.</summary>
    internal bool WaitIdle(string key, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (_workers.TryGetValue(key, out var w) && (w.Next is not null || w.Running))
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return false;
                }
                Monitor.Wait(_gate, (int)left);
            }
            return true;
        }
    }

    private void Run(string key, Worker worker)
    {
        while (true)
        {
            Job job;
            Action before;
            Action work;
            string name;
            lock (_gate)
            {
                while (worker.Next is null)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    Monitor.Wait(_gate);
                }
                job = worker.Next;
                before = worker.NextBefore!;
                work = worker.NextWork!;
                name = worker.NextName;
                worker.Next = null;
                if (_disposed)
                {
                    job.Complete();
                    return;
                }
                worker.Running = true;
            }

            Execute(key, name, before, work);

            lock (_gate)
            {
                worker.Running = false;
                Monitor.PulseAll(_gate);
            }
            job.Complete();
        }
    }

    private void Execute(string key, string name, Action before, Action work)
    {
        _start.Wait();
        var released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                _start.Release();
            }
        }

        var overdue = 0;
        Timer? graceTimer = null;
        Timer? overdueTimer = null;
        try
        {
            before();
            graceTimer = new Timer(_ => Release(), null, _startGrace, Timeout.InfiniteTimeSpan);
            overdueTimer = new Timer(
                _ =>
                {
                    if (Interlocked.Exchange(ref overdue, 1) == 0)
                    {
                        _onOverdue?.Invoke(key, name, _overdueAfter);
                    }
                },
                null,
                _overdueAfter,
                Timeout.InfiniteTimeSpan);
            work();
        }
        catch
        {
            // The work logs its own failures; the worker must outlive one.
        }
        finally
        {
            graceTimer?.Dispose();
            overdueTimer?.Dispose();
            Release();
            if (Volatile.Read(ref overdue) != 0)
            {
                _onRecovered?.Invoke(key, name);
            }
        }
    }

    /// <summary>Stops accepting work, drops queued updates, and waits up to <paramref name="wait"/> for running ones, so the hardware can be closed after.</summary>
    internal bool Shutdown(TimeSpan wait)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        lock (_gate)
        {
            _disposed = true;
            foreach (var w in _workers.Values)
            {
                w.Next?.Complete();
                w.Next = null;
            }
            Monitor.PulseAll(_gate);
            while (true)
            {
                var anyRunning = false;
                foreach (var w in _workers.Values)
                {
                    anyRunning |= w.Running;
                }
                if (!anyRunning)
                {
                    return true;
                }
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return false;
                }
                Monitor.Wait(_gate, (int)left);
            }
        }
    }

    public void Dispose() => Shutdown(TimeSpan.Zero);
}
