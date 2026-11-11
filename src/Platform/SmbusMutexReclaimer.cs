using System;
using System.Threading;

namespace Nexus.Service.Platform;

/// <summary>
/// Clears an abandoned system-wide SMBus mutex. A process killed while it holds the mutex leaves it abandoned, and
/// the next waiter that sees that state owns it. BlackSharp's WorldMutex.Lock treats the abandoned wait result as a
/// failed lock and never unlocks, so the service thread keeps the mutex until exit and every other SMBus user, the
/// OpenRGB child included, waits on it without a timeout. Taking and releasing it once on a thread of our own hands
/// it back in a clean state.
/// </summary>
internal static class SmbusMutexReclaimer
{
    internal const string MutexName = @"Global\Access_SMBUS.HTP.Method";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(1);

    internal enum Outcome
    {
        NotApplicable,
        NotPresent,
        Clean,
        Reclaimed,
        HeldByOther,
        Failed,
    }

    /// <summary>Windows only: the mutex belongs to the Windows SMBus driver stack.</summary>
    public static Outcome Reclaim(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Outcome.NotApplicable;
        }
        var outcome = Reclaim(MutexName, WaitTimeout);
        if (outcome == Outcome.Reclaimed)
        {
            ServiceLog.Warn($"[smbus] reclaimed an abandoned SMBus mutex ({reason})");
        }
        return outcome;
    }

    // Mutex ownership is per thread, so wait and release must happen on the same one, and not on a pool thread.
    internal static Outcome Reclaim(string name, TimeSpan waitTimeout)
    {
        var outcome = Outcome.Failed;
        var worker = new Thread(() => outcome = ReclaimOnThisThread(name, waitTimeout))
        {
            IsBackground = true,
            Name = "smbus-mutex-reclaim",
        };
        worker.Start();
        return worker.Join(JoinTimeout) ? outcome : Outcome.Failed;
    }

    private static Outcome ReclaimOnThisThread(string name, TimeSpan waitTimeout)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex))
            {
                return Outcome.NotPresent;
            }
            using (mutex)
            {
                var abandoned = false;
                try
                {
                    if (!mutex.WaitOne(waitTimeout))
                    {
                        return Outcome.HeldByOther;
                    }
                }
                catch (AbandonedMutexException)
                {
                    abandoned = true;
                }
                mutex.ReleaseMutex();
                return abandoned ? Outcome.Reclaimed : Outcome.Clean;
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[smbus] mutex reclaim failed: {ex.GetType().Name}: {ex.Message}");
            return Outcome.Failed;
        }
    }
}
