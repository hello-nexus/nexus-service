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

public enum SuperIoWriteGate { Allowed, Stale, Suspect }

/// <summary>
/// Decides when a Super I/O chip needs a re-open. A chip whose registers all read all-ones shows every tach at 0, every
/// PWM output at full duty, temperatures at -1 and voltages at the raw maximum. After a resume the stall plus that
/// signature triggers retries; otherwise a fan must have spun earlier. Budgets are per chip.
/// </summary>
public sealed class SuperIoRecoveryPolicy
{
    public const long RecoveryWindowMs = 10 * 60_000;
    public const long WindowSpacingMs = 30_000;
    public const long IdleSpacingMs = 5 * 60_000;
    public const int MaxAttempts = 3;
    private const float FullDuty = 99.5f;
    private const float VoltageTolerance = 0.02f;

    private sealed class ChipBudget
    {
        public int Attempts;
        public long? LastAttemptMs;
        public bool GaveUpReported;
    }

    private readonly object _lock = new();
    private readonly HashSet<string> _spun = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChipBudget> _budgets = new(StringComparer.Ordinal);
    private long _windowEndMs = long.MinValue;

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

    /// <summary>
    /// The all-ones register signature: every temperature reads -1 and every voltage sits at the raw maximum
    /// (2.04 or 4.08). Null when the chip exposes neither, so nothing can be said.
    /// </summary>
    public static bool? HasAllOnesSignature(IReadOnlyList<float?> temperatures, IReadOnlyList<float?> voltages)
    {
        if (temperatures.Count == 0 && voltages.Count == 0)
        {
            return null;
        }
        foreach (var t in temperatures)
        {
            if (t is not { } v)
            {
                return null;
            }
            if (v > -1f + 0.01f)
            {
                return false;
            }
        }
        foreach (var volt in voltages)
        {
            if (volt is not { } v)
            {
                return null;
            }
            if (Math.Abs(v - 2.04f) > VoltageTolerance && Math.Abs(v - 4.08f) > VoltageTolerance)
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsStalled(IReadOnlyList<float?> fanRpm, IReadOnlyList<float?> controlPercent) =>
        Classify(fanRpm, controlPercent) == SuperIoChipState.Stalled;

    /// <summary>A negative generation is not tied to the motherboard group and is always current.</summary>
    public static SuperIoWriteGate CheckWrite(int mappingGeneration, int currentGeneration, bool chipSuspect)
    {
        if (mappingGeneration >= 0 && mappingGeneration != currentGeneration)
        {
            return SuperIoWriteGate.Stale;
        }
        return chipSuspect ? SuperIoWriteGate.Suspect : SuperIoWriteGate.Allowed;
    }

    /// <summary>A resume opens the recovery window and clears every chip's retry budget.</summary>
    public void NoteResume(long nowMs)
    {
        lock (_lock)
        {
            _windowEndMs = nowMs + RecoveryWindowMs;
            _budgets.Clear();
        }
    }

    /// <summary>
    /// Suspect is true while a stalled chip is believed to be in the dead state: software writes to it would
    /// save that state as the BIOS default. A signature of false means the registers read normally, so the chip is
    /// never suspect; a null signature falls back to requiring that a fan spun earlier.
    /// Observing leaves the retry budget alone until CommitAttempt.
    /// </summary>
    public SuperIoDecision Observe(string chipId, SuperIoChipState state, bool? signature, long nowMs, out bool suspect)
    {
        lock (_lock)
        {
            suspect = false;
            if (state == SuperIoChipState.Sane)
            {
                _spun.Add(chipId);
                _budgets.Remove(chipId);
                return SuperIoDecision.None;
            }
            if (state != SuperIoChipState.Stalled || signature == false)
            {
                return SuperIoDecision.None;
            }
            var inWindow = nowMs < _windowEndMs;
            var eligible = _spun.Contains(chipId) || (inWindow && signature == true);
            if (!eligible)
            {
                return SuperIoDecision.None;
            }
            suspect = true;
            var budget = _budgets.GetValueOrDefault(chipId);
            if (budget is not null && budget.Attempts >= MaxAttempts)
            {
                if (budget.GaveUpReported)
                {
                    return SuperIoDecision.None;
                }
                budget.GaveUpReported = true;
                return SuperIoDecision.GaveUp;
            }
            var spacing = inWindow ? WindowSpacingMs : IdleSpacingMs;
            if (budget?.LastAttemptMs is { } last && nowMs - last < spacing)
            {
                return SuperIoDecision.None;
            }
            return SuperIoDecision.Reopen;
        }
    }

    /// <summary>Called only when the re-open actually started; a busy single-flight is not an attempt.</summary>
    public void CommitAttempt(string chipId, long nowMs)
    {
        lock (_lock)
        {
            if (!_budgets.TryGetValue(chipId, out var budget))
            {
                _budgets[chipId] = budget = new ChipBudget();
            }
            budget.Attempts++;
            budget.LastAttemptMs = nowMs;
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
