using System.Collections.Concurrent;

namespace Nexus.Service.Lighting;

/// <summary>User-selectable per-device update caps. Null means no cap.</summary>
public static class DeviceRateCap
{
    public static readonly int[] AllowedHz = { 30, 25, 20, 15, 10, 5, 1 };

    public static bool IsValid(int? hz) => hz is null || Array.IndexOf(AllowedHz, hz.Value) >= 0;
}

/// <summary>
/// Per-key rate gate on deadlines: each pass advances the next due time by one
/// period and carries the remainder, so the average rate matches the cap even
/// when the caller ticks at a coarser, non-divisible interval. Holds only
/// deadlines: the caller keeps the frame, and a dropped frame stays unsent in
/// the caller's dedupe baseline, so its next tick offers it again.
/// </summary>
public sealed class PushRateLimiter<TKey> where TKey : notnull
{
    private readonly record struct Schedule(long DueMs, long LastPassMs, int PeriodMs);

    private readonly ConcurrentDictionary<TKey, Schedule> _schedules = new();

    public static int PeriodMs(int capHz) => 1000 / Math.Max(1, capHz);

    /// <summary>True when a push may go out at <paramref name="nowMs"/>; records the pass.</summary>
    public bool TryPass(TKey key, int capHz, long nowMs)
    {
        if (capHz <= 0)
        {
            return true;
        }
        var period = PeriodMs(capHz);
        // Early by up to a tenth of a period still counts, so timer jitter does not cost a whole tick.
        var slack = period / 10;
        if (!_schedules.TryGetValue(key, out var schedule))
        {
            _schedules[key] = new Schedule(nowMs + period, nowMs, period);
            return true;
        }
        // Raising the cap shortens the wait to the new period; lowering it leaves the due time alone.
        var due = period < schedule.PeriodMs
            ? Math.Min(schedule.DueMs, schedule.LastPassMs + period)
            : schedule.DueMs;
        if (nowMs + slack < due)
        {
            return false;
        }
        due += period;
        // More than a period late (stall): re-anchor instead of releasing a catch-up burst.
        if (nowMs > due)
        {
            due = nowMs + period;
        }
        _schedules[key] = new Schedule(due, nowMs, period);
        return true;
    }

    public void Reset() => _schedules.Clear();
}
