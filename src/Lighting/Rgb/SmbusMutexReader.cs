using System;
using System.Threading;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>One OS mutex handle; acquire and release must stay on the calling thread.</summary>
internal interface ISmbusMutexHandle : IDisposable
{
    bool WaitOne(TimeSpan timeout);

    void Release();
}

/// <summary>
/// Reads the SMBus mutex the OpenRGB daemon's DIMM detection and our LHM SPD reads share. A wait that times out
/// means the mutex stayed held for the whole window; an acquire is released at once.
/// </summary>
internal static class SmbusMutexReader
{
    internal const string Name = "Global\\Access_SMBUS.HTP.Method";

    /// <summary>Waits up to <paramref name="timeout"/> on the real mutex: "held" on timeout, "free", "abandoned" (acquired from a dead owner and released), "absent", or "n/a" off Windows.</summary>
    internal static string Wait(TimeSpan timeout) =>
        OperatingSystem.IsWindows() ? Wait(OpenReal, timeout) : "n/a";

    internal static string Wait(Func<ISmbusMutexHandle?> open, TimeSpan timeout)
    {
        try
        {
            using var handle = open();
            if (handle is null)
            {
                return "absent";
            }
            try
            {
                if (!handle.WaitOne(timeout))
                {
                    return "held";
                }
            }
            catch (AbandonedMutexException)
            {
                handle.Release();
                return "abandoned";
            }
            handle.Release();
            return "free";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static ISmbusMutexHandle? OpenReal() =>
        Mutex.TryOpenExisting(Name, out var mutex) ? new RealHandle(mutex) : null;

    private sealed class RealHandle : ISmbusMutexHandle
    {
        private readonly Mutex _mutex;

        internal RealHandle(Mutex mutex) => _mutex = mutex;

        public bool WaitOne(TimeSpan timeout) => _mutex.WaitOne(timeout);

        public void Release() => _mutex.ReleaseMutex();

        public void Dispose() => _mutex.Dispose();
    }
}
