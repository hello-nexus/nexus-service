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

/// <summary>
/// Decides when a Super I/O chip has gone dark: every tach reads zero while every PWM output reads full duty.
/// A chip whose registers all read 0xFF looks exactly like this, and only a re-open recovers it.
/// </summary>
public sealed class SuperIoStallWatch
{
    public const long MinIntervalMs = 5 * 60_000;
    private const float FullDuty = 99.5f;

    private readonly object _lock = new();
    private readonly HashSet<string> _spun = new(StringComparer.Ordinal);
    private long? _lastReopenMs;

    public static bool IsStalled(IReadOnlyList<float?> fanRpm, IReadOnlyList<float?> controlPercent)
    {
        if (fanRpm.Count == 0 || controlPercent.Count == 0)
        {
            return false;
        }
        foreach (var rpm in fanRpm)
        {
            if (rpm is > 0f)
            {
                return false;
            }
        }
        foreach (var duty in controlPercent)
        {
            if (duty is not { } d || d < FullDuty)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True when the chip is stalled, one of its fans spun earlier, and the rate limit allows an attempt; a true result counts as the attempt.</summary>
    public bool ShouldReopen(string chipId, IReadOnlyList<float?> fanRpm, IReadOnlyList<float?> controlPercent, long nowMs)
    {
        lock (_lock)
        {
            foreach (var rpm in fanRpm)
            {
                if (rpm is > 0f)
                {
                    _spun.Add(chipId);
                    return false;
                }
            }
            if (!IsStalled(fanRpm, controlPercent) || !_spun.Contains(chipId))
            {
                return false;
            }
            if (_lastReopenMs is { } last && nowMs - last < MinIntervalMs)
            {
                return false;
            }
            _lastReopenMs = nowMs;
            return true;
        }
    }

    /// <summary>A re-open that did not come from the stall check still starts the rate-limit window.</summary>
    public void NoteReopen(long nowMs)
    {
        lock (_lock)
        {
            _lastReopenMs = nowMs;
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
