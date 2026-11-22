using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Sensors;

/// <summary>
/// Runs storage drive work off the hardware walk, one worker thread per drive object, so a drive stuck in I/O retries
/// delays neither the fan-curve engine nor the other drives. Workers are keyed by the hardware object, never its identifier:
/// LibreHardwareMonitor can hand several drives the same identifier. A drive with an update still queued or running is not requested again.
/// A drive whose update runs past <c>overdueAfter</c> is reported once through <c>onOverdue</c>, and through <c>onRecovered</c> when it returns;
/// the two never run out of order for one update.
/// Reads of a drive's LHM objects (<see cref="RequestRead"/>) go through the same per-drive queue, so they never overlap that drive's updates.
/// LibreHardwareMonitor reads the process-wide SMART cycle static only at the top of <c>StorageDevice.Update</c>, before any drive I/O,
/// so the static is set under a start gate that an update gives up when it returns or after <c>startGrace</c>, whichever is first.
/// The gate cannot be held through the whole update: that would serialize every drive behind a stuck one.
/// Free of LibreHardwareMonitor types so it tests on every platform.
/// </summary>
internal sealed class LhmStorageUpdater : IDisposable
{
    /// <summary>A queued item. Done is set when it finished, was dropped, or failed.</summary>
    internal sealed class Job
    {
        private readonly ManualResetEventSlim _done = new(false);

        internal bool Wait(TimeSpan timeout) => _done.Wait(timeout);

        internal bool IsDone => _done.IsSet;

        internal void Complete() => _done.Set();
    }

    private sealed class Item
    {
        public required Job Job;
        public required Action Work;
        public Action? Before;
        public string Id = "";
        public string Name = "";
        public bool IsRead;
    }

    private sealed class Worker
    {
        public Thread? Thread;
        public readonly Queue<Item> Queue = new();
        public bool Running;
        public bool Removed;
        public bool UpdateActive;
        public bool ReadActive;
    }

    // One per update: the timer callback and the completion decide under this lock, so a late callback cannot follow the recovery.
    private sealed class OverdueState
    {
        public bool Done;
        public bool Overdue;
    }

    private readonly object _gate = new();
    private readonly Dictionary<object, Worker> _workers = new(ReferenceEqualityComparer.Instance);
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
    /// Queues an update for the drive <paramref name="key"/>: <paramref name="before"/> runs under the start gate right before <paramref name="work"/>.
    /// Null means that drive still has an update queued or running (or the updater is disposed), so this one was dropped.
    /// </summary>
    internal Job? Request(object key, string id, string name, Action before, Action work) =>
        Enqueue(key, w => w.UpdateActive, w => w.UpdateActive = true, new Item { Job = new Job(), Work = work, Before = before, Id = id, Name = name });

    /// <summary>
    /// Queues a read of the drive's LHM objects behind whatever that drive is doing. Null means a read for it is already queued or running (or the updater is disposed).
    /// </summary>
    internal Job? RequestRead(object key, Action work) =>
        Enqueue(key, w => w.ReadActive, w => w.ReadActive = true, new Item { Job = new Job(), Work = work, IsRead = true });

    private Job? Enqueue(object key, Func<Worker, bool> active, Action<Worker> mark, Item item)
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
            if (active(worker))
            {
                return null;
            }
            mark(worker);
            worker.Queue.Enqueue(item);
            if (worker.Thread is null)
            {
                worker.Thread = new Thread(() => Run(worker)) { IsBackground = true, Name = "lhm-storage" };
                worker.Thread.Start();
            }
            Monitor.PulseAll(_gate);
            return item.Job;
        }
    }

    internal bool IsBusy(object key)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(key, out var w) && (w.Queue.Count > 0 || w.Running);
        }
    }

    /// <summary>Drops the workers of drives no longer in <paramref name="present"/> (rescan, hardware removed), so a reused slot starts clean.</summary>
    internal void Retain(ICollection<object> present)
    {
        lock (_gate)
        {
            List<object>? gone = null;
            foreach (var key in _workers.Keys)
            {
                if (!present.Contains(key))
                {
                    (gone ??= new()).Add(key);
                }
            }
            if (gone is null)
            {
                return;
            }
            foreach (var key in gone)
            {
                var worker = _workers[key];
                _workers.Remove(key);
                worker.Removed = true;
                while (worker.Queue.Count > 0)
                {
                    worker.Queue.Dequeue().Job.Complete();
                }
            }
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Waits until <paramref name="key"/> has nothing queued or running. False means it was still busy after <paramref name="timeout"/>.</summary>
    internal bool WaitIdle(object key, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (_workers.TryGetValue(key, out var w) && (w.Queue.Count > 0 || w.Running))
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

    private void Run(Worker worker)
    {
        while (true)
        {
            Item item;
            lock (_gate)
            {
                while (worker.Queue.Count == 0)
                {
                    if (_disposed || worker.Removed)
                    {
                        return;
                    }
                    Monitor.Wait(_gate);
                }
                item = worker.Queue.Dequeue();
                if (_disposed)
                {
                    item.Job.Complete();
                    return;
                }
                worker.Running = true;
            }

            if (item.IsRead)
            {
                try
                {
                    item.Work();
                }
                catch
                {
                    // The caller handles its own failures; the worker must outlive one.
                }
            }
            else
            {
                Execute(item);
            }

            lock (_gate)
            {
                worker.Running = false;
                if (item.IsRead)
                {
                    worker.ReadActive = false;
                }
                else
                {
                    worker.UpdateActive = false;
                }
                Monitor.PulseAll(_gate);
            }
            item.Job.Complete();
        }
    }

    private void Execute(Item item)
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

        var state = new OverdueState();
        Timer? graceTimer = null;
        Timer? overdueTimer = null;
        try
        {
            item.Before?.Invoke();
            graceTimer = new Timer(_ => Release(), null, _startGrace, Timeout.InfiniteTimeSpan);
            overdueTimer = new Timer(
                _ =>
                {
                    lock (state)
                    {
                        if (state.Done || state.Overdue)
                        {
                            return;
                        }
                        state.Overdue = true;
                        _onOverdue?.Invoke(item.Id, item.Name, _overdueAfter);
                    }
                },
                null,
                _overdueAfter,
                Timeout.InfiniteTimeSpan);
            item.Work();
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
            lock (state)
            {
                state.Done = true;
                if (state.Overdue)
                {
                    _onRecovered?.Invoke(item.Id, item.Name);
                }
            }
        }
    }

    /// <summary>Stops accepting work, drops queued items, and waits up to <paramref name="wait"/> for running ones, so the hardware can be closed after.</summary>
    internal bool Shutdown(TimeSpan wait)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        lock (_gate)
        {
            _disposed = true;
            foreach (var w in _workers.Values)
            {
                while (w.Queue.Count > 0)
                {
                    w.Queue.Dequeue().Job.Complete();
                }
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
