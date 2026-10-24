using System;
using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>One sweep of a Super I/O chip's sensor values, read once so every judgement uses the same instant.</summary>
internal sealed record SuperIoSnapshot(IReadOnlyList<float?> Controls, IReadOnlyList<float?> Fans, IReadOnlyList<float?> Voltages);

/// <summary>
/// Detects a Nuvoton Super I/O whose hardware-monitor window stopped decoding (all registers read ones:
/// every control full, every tach zero). Duty and tach alone also fit a real pump death with every header
/// at full, so the all-ones voltage decode is required too.
/// </summary>
internal sealed class SuperIoReadGuard
{
    internal const float FullDutyFloor = 99.5f;

    // A register reading all ones decodes to at least this floor on every Nuvoton decode path, and board scaling only raises it; a live chip always has an unscaled rail (Vcore) below it.
    internal const float AllOnesVoltageFloor = 2.03f;

    private readonly object _lock = new();
    private readonly HashSet<string> _spun = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalid = new(StringComparer.Ordinal);

    internal static bool IsAllOnesRead(SuperIoSnapshot s, bool fanSpunBefore)
    {
        if (!fanSpunBefore || s.Controls.Count < 2 || s.Fans.Count == 0 || s.Voltages.Count < 2)
        {
            return false;
        }
        foreach (var c in s.Controls)
        {
            if (c is not { } duty || duty < FullDutyFloor)
            {
                return false;
            }
        }
        foreach (var f in s.Fans)
        {
            if ((f ?? 0f) != 0f)
            {
                return false;
            }
        }
        foreach (var v in s.Voltages)
        {
            if (v is not { } volts || volts < AllOnesVoltageFloor)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Judges one chip from its snapshot, remembers that a fan has spun, and logs only on a state change. True while the chip reads as all-ones.</summary>
    internal bool Evaluate(string chipId, SuperIoSnapshot snapshot)
    {
        lock (_lock)
        {
            if (IsAllOnesRead(snapshot, _spun.Contains(chipId)))
            {
                if (_invalid.Add(chipId))
                {
                    ServiceLog.Warn($"[fan-control] super-io {chipId} reads all-0xFF, fan stall alerts suppressed for it");
                }
                return true;
            }
            if (_invalid.Remove(chipId))
            {
                ServiceLog.Info($"[fan-control] super-io {chipId} reads valid again");
            }
            foreach (var f in snapshot.Fans)
            {
                if (f is > 0f)
                {
                    _spun.Add(chipId);
                    break;
                }
            }
            return false;
        }
    }
}
