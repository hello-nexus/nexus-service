using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nexus.Service.Sensors;

/// <summary>Re-opens the motherboard Super I/O when firmware has left the chip unreadable after sleep.</summary>
public interface ISuperIoRecovery
{
    /// <summary>Raised after the motherboard group has been rebuilt; sensor objects taken before it are stale.</summary>
    event Action? Reopened;

    /// <summary>Returns at once; the re-open runs later on a worker thread.</summary>
    void OnSystemResumed();
}

/// <summary>One sensor reading of a Super I/O chip. Kind is T, V, F or C (temperature, voltage, fan, control).</summary>
public readonly record struct SuperIoSample(char Kind, int Index, float? Value);

public enum SuperIoChipState { Unknown, Sane, Stalled }

public enum SuperIoDecision { None, Reopen, GaveUp }

/// <summary>
/// Decides when a Super I/O chip needs a re-open. A chip whose registers all read 0xFF shows every tach at 0 and every
/// PWM output at full duty. After a resume the stall alone triggers retries; otherwise a fan must have spun earlier.
/// </summary>
public sealed class SuperIoRecoveryPolicy
{
    public const long RecoveryWindowMs = 10 * 60_000;
    public const long WindowSpacingMs = 30_000;
    public const long IdleSpacingMs = 5 * 60_000;
    public const int MaxAttempts = 3;
    private const float FullDuty = 99.5f;

    private readonly object _lock = new();
    private readonly HashSet<string> _spun = new(StringComparer.Ordinal);
    private long _windowEndMs = long.MinValue;
    private long? _lastAttemptMs;
    private int _attempts;
    private bool _gaveUpReported;

    public static SuperIoChipState Classify(IReadOnlyList<float?> fanRpm, IReadOnlyList<float?> controlPercent)
    {
        if (fanRpm.Count == 0 || controlPercent.Count == 0)
        {
            return SuperIoChipState.Unknown;
        }
        foreach (var rpm in fanRpm)
        {
            if (rpm is > 0f)
            {
                return SuperIoChipState.Sane;
            }
        }
        foreach (var duty in controlPercent)
        {
            if (duty is not { } d)
            {
                return SuperIoChipState.Unknown;
            }
            if (d < FullDuty)
            {
                return SuperIoChipState.Sane;
            }
        }
        return SuperIoChipState.Stalled;
    }

    /// <summary>A negative generation is not tied to the motherboard group and is always current.</summary>
    public static bool MayWrite(int mappingGeneration, int currentGeneration, bool chipSuspect) =>
        (mappingGeneration < 0 || mappingGeneration == currentGeneration) && !chipSuspect;

    public static bool IsStalled(IReadOnlyList<float?> fanRpm, IReadOnlyList<float?> controlPercent) =>
        Classify(fanRpm, controlPercent) == SuperIoChipState.Stalled;

    /// <summary>A resume opens the recovery window and clears the retry budget.</summary>
    public void NoteResume(long nowMs)
    {
        lock (_lock)
        {
            _windowEndMs = nowMs + RecoveryWindowMs;
            _attempts = 0;
            _lastAttemptMs = null;
            _gaveUpReported = false;
        }
    }

    /// <summary>
    /// Suspect is true while a stalled chip is believed to be in the dead state: software writes to it would
    /// save that state as the BIOS default. Observing has no side effect on the retry budget until CommitAttempt.
    /// </summary>
    public SuperIoDecision Observe(string chipId, SuperIoChipState state, long nowMs, out bool suspect)
    {
        lock (_lock)
        {
            suspect = false;
            if (state == SuperIoChipState.Sane)
            {
                _spun.Add(chipId);
                _attempts = 0;
                _gaveUpReported = false;
                return SuperIoDecision.None;
            }
            if (state != SuperIoChipState.Stalled)
            {
                return SuperIoDecision.None;
            }
            var inWindow = nowMs < _windowEndMs;
            if (!inWindow && !_spun.Contains(chipId))
            {
                return SuperIoDecision.None;
            }
            suspect = true;
            if (_attempts >= MaxAttempts)
            {
                if (_gaveUpReported)
                {
                    return SuperIoDecision.None;
                }
                _gaveUpReported = true;
                return SuperIoDecision.GaveUp;
            }
            var spacing = inWindow ? WindowSpacingMs : IdleSpacingMs;
            if (_lastAttemptMs is { } last && nowMs - last < spacing)
            {
                return SuperIoDecision.None;
            }
            return SuperIoDecision.Reopen;
        }
    }

    /// <summary>Called only when the re-open actually started; a busy single-flight is not an attempt.</summary>
    public void CommitAttempt(long nowMs)
    {
        lock (_lock)
        {
            _attempts++;
            _lastAttemptMs = nowMs;
        }
    }

    public static string Format(string chipId, IEnumerable<SuperIoSample> samples)
    {
        var sb = new StringBuilder(chipId);
        foreach (var s in samples)
        {
            sb.Append(' ').Append(s.Kind).Append(s.Index).Append('=');
            sb.Append(s.Value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "null");
        }
        return sb.ToString();
    }
}
