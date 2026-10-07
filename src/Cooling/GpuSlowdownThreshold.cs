using System;
using System.Collections.Generic;

namespace Nexus.Service.Cooling;

/// <summary>
/// An NVIDIA GPU's slowdown temperature threshold, platform-split at compile time: Windows
/// reads it through the nvml.dll loader in <c>NvmlInterop</c>, Linux through <c>Nvml</c>, any
/// other platform has none. A success and a "not supported" answer are cached for good; a
/// transient failure (the driver not ready at boot) is retried after a backoff, and the first
/// failure is logged once.
/// </summary>
internal static class GpuSlowdownThreshold
{
    /// <summary>One attempt: a value, "not supported" (permanent), or a transient failure.</summary>
    internal readonly record struct Read(int? Value, bool Permanent)
    {
        public static Read Ok(int value) => new(value, false);
        public static readonly Read NotSupported = new(null, true);
        public static readonly Read Transient = new(null, false);
    }

    internal const long RetryBackoffMs = 5 * 60_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, (Read Read, long RetryAtMs)> Cache = new();

    public static int? Get(int gpu) => Resolve(gpu, () => ReadPlatform(gpu), Environment.TickCount64, Cache);

    private static Read ReadPlatform(int gpu)
    {
#if WINDOWS
        return Nexus.Service.Diagnostics.Gpu.NvmlInterop.ReadSlowdownThreshold(gpu);
#elif LINUX
        return Nvml.ReadSlowdown(gpu);
#else
        return Read.NotSupported;
#endif
    }

    internal static int? Resolve(int gpu, Func<Read> read, long nowMs, Dictionary<int, (Read Read, long RetryAtMs)> cache)
    {
        lock (Gate)
        {
            var seen = cache.TryGetValue(gpu, out var entry);
            if (seen && (entry.Read.Value is not null || entry.Read.Permanent || nowMs < entry.RetryAtMs))
            {
                return entry.Read.Value;
            }
            var result = read();
            cache[gpu] = (result, nowMs + RetryBackoffMs);
            if (!seen && result.Value is null)
            {
                Console.Error.WriteLine($"[thermal-guard] NVML slowdown threshold unavailable for GPU {gpu} ({(result.Permanent ? "not supported" : "will retry")})");
            }
            return result.Value;
        }
    }
}
