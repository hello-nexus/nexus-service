namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Remembers displays whose DDC probe keeps failing so Enumerate stops re-probing them every poll.
/// A failure is usually transient (a monitor still waking, bus contention), so after a failure a
/// display is probed again once per <see cref="FailureSpacing"/> window, and only after
/// <see cref="MaxAttempts"/> failures counted that far apart is it skipped until
/// <see cref="RetryInterval"/> passes or the attached set changes (<see cref="Clear"/>). A failure
/// only counts when the spacing has passed since the last counted one, so a burst from concurrent
/// or back-to-back polls during a short outage cannot trip the threshold.
/// </summary>
internal sealed class DdcNegativeProbeCache
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan FailureSpacing = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private readonly Dictionary<string, (int Failures, long LastMs)> _failures = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;

    public DdcNegativeProbeCache(Func<long>? nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    /// <summary>True when this poll should not probe the display: a failure was recorded inside the spacing window, or it has failed enough times and the retry interval has not passed.</summary>
    public bool ShouldSkip(string id)
    {
        lock (_lock)
        {
            if (!_failures.TryGetValue(id, out var e))
                return false;
            var sinceLast = _nowMs() - e.LastMs;
            // One probe per spacing window until the threshold, so polls inside it do not each probe.
            return sinceLast < (long)FailureSpacing.TotalMilliseconds
                || (e.Failures >= MaxAttempts && sinceLast < (long)RetryInterval.TotalMilliseconds);
        }
    }

    public void RecordFailure(string id)
    {
        lock (_lock)
        {
            var now = _nowMs();
            if (_failures.TryGetValue(id, out var e) && now - e.LastMs < (long)FailureSpacing.TotalMilliseconds)
                return;
            _failures[id] = (Math.Min(e.Failures + 1, MaxAttempts), now);
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
