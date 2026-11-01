using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Peripherals.IoSafety;

/// <summary>Teardown rules for an async operation that overran its time bound.</summary>
internal static class PendingIo
{
    private const int WarnAtCount = 16;
    private static readonly ConcurrentQueue<IDisposable> Stranded = new();

    /// <summary>Resources kept alive because an operation never finished; they live until the process exits.</summary>
    internal static int StrandedCount => Stranded.Count;

    /// <summary>
    /// Cancels <paramref name="op"/> (the token the operation was started with) and waits up to
    /// <paramref name="settleMs"/> for the operation to finish, whatever its outcome. The fault is read so it
    /// never surfaces as unobserved. False means the operation is still pending.
    /// </summary>
    internal static bool CancelAndSettle(Task io, CancellationTokenSource op, int settleMs)
    {
        try
        {
            op.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            io.Wait(settleMs);
        }
        catch (AggregateException)
        {
            // Faulted or cancelled: finished either way.
        }
        _ = io.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return io.IsCompleted;
    }

    /// <summary>
    /// Disposes <paramref name="resource"/> only when no operation is pending on it. Otherwise it is kept alive, and
    /// released as soon as <paramref name="stuckOperation"/> finishes, when that task is known: freeing it earlier frees
    /// memory the kernel may still complete into. Entries are never dropped to meet a limit for the same reason, so a
    /// warning marks a count that keeps growing.
    /// </summary>
    internal static bool Retire(IDisposable resource, bool noOperationPending, Task? stuckOperation = null)
    {
        if (noOperationPending)
        {
            resource.Dispose();
            return true;
        }
        Stranded.Enqueue(resource);
        var count = Stranded.Count;
        if (count >= WarnAtCount)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[io] {count} handles are held open by operations that never finished");
        }
        stuckOperation?.ContinueWith(_ => Release(resource), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return false;
    }

    private static void Release(IDisposable resource)
    {
        // The queue is only a root; take every entry out and put the others back.
        var keep = new System.Collections.Generic.List<IDisposable>();
        while (Stranded.TryDequeue(out var held))
        {
            if (!ReferenceEquals(held, resource))
            {
                keep.Add(held);
            }
        }
        foreach (var held in keep)
        {
            Stranded.Enqueue(held);
        }
        try
        {
            resource.Dispose();
        }
        catch
        {
            // Already closed by a racing teardown.
        }
    }
}
