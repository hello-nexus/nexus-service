namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Remembers displays whose DDC probe keeps failing so Enumerate stops re-probing them every poll.
/// A failure is usually transient (a monitor still waking, bus contention), so a display is probed
/// freely for its first <see cref="MaxAttempts"/> failures; after that it is skipped until
/// <see cref="RetryInterval"/> passes or the attached set changes (<see cref="Clear"/>).
/// </summary>
internal sealed class DdcNegativeProbeCache
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private readonly Dictionary<string, (int Failures, long LastMs)> _failures = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;

    public DdcNegativeProbeCache(Func<long>? nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    /// <summary>True when the display has failed enough probes recently that this poll should not probe it.</summary>
    public bool ShouldSkip(string id)
    {
        lock (_lock)
        {
            return _failures.TryGetValue(id, out var e)
                && e.Failures >= MaxAttempts
                && _nowMs() - e.LastMs < (long)RetryInterval.TotalMilliseconds;
        }
    }

    public void RecordFailure(string id)
    {
        lock (_lock)
        {
            _failures.TryGetValue(id, out var e);
            _failures[id] = (Math.Min(e.Failures + 1, MaxAttempts), _nowMs());
        }
    }

    public void RecordSuccess(string id)
    {
        lock (_lock)
        {
            _failures.Remove(id);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _failures.Clear();
        }
    }
}
