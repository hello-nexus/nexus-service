using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Peripherals.IoSafety;

/// <summary>Teardown rules for an async operation that overran its time bound.</summary>
internal static class PendingIo
{
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

    /// <summary>Disposes <paramref name="resource"/> only when no operation is pending on it; otherwise keeps it alive instead of freeing memory the kernel may still complete into.</summary>
    internal static bool Retire(IDisposable resource, bool noOperationPending)
    {
        if (noOperationPending)
        {
            resource.Dispose();
            return true;
        }
        Stranded.Enqueue(resource);
        return false;
    }
}
