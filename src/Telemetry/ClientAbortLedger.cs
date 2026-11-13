using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Request ids whose client had already left (RequestAborted fired) when the pipeline finished. Kestrel reports a
/// response cut short of its Content-Length after the app returns, with no route and no HttpContext; this lets the
/// logger tell a client that aborted a download from a handler that truncated a response.
/// </summary>
internal static class ClientAbortLedger
{
    private const int MaxEntries = 256;
    private static readonly long TtlTicks = Stopwatch.Frequency * 30;
    private static readonly ConcurrentDictionary<string, long> Aborted = new(StringComparer.Ordinal);

    internal static void Note(string traceId)
    {
        if (string.IsNullOrEmpty(traceId))
        {
            return;
        }
        var now = Stopwatch.GetTimestamp();
        if (Aborted.Count >= MaxEntries)
        {
            foreach (var (id, at) in Aborted)
            {
                if (now - at > TtlTicks)
                {
                    Aborted.TryRemove(id, out _);
                }
            }
            if (Aborted.Count >= MaxEntries)
            {
                return;
            }
        }
        Aborted[traceId] = now;
    }

    /// <summary>True once per noted id, within the time window.</summary>
    internal static bool Take(string? traceId) =>
        traceId is not null && Aborted.TryRemove(traceId, out var at) && Stopwatch.GetTimestamp() - at <= TtlTicks;
}
