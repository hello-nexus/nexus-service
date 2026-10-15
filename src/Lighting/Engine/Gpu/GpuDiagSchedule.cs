using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Variants the diagnostic sequence cycles through; all are applied on the GL thread.</summary>
public enum GpuDiagMode
{
    Baseline,
    BaselineStart,
    Unbind,
    IdleUnbound,
    Swap,
    Finish,
    ExtraCtx,
    ExtraDeleted,
}

/// <summary>
/// Maps a stats window index to the mode that runs during it: warmup in
/// baseline, then each variant for one fixed block, then baseline again.
/// Pure, so the order and boundaries are unit-testable.
/// </summary>
public sealed class GpuDiagSchedule
{
    public static readonly GpuDiagMode[] DefaultOrder =
    {
        GpuDiagMode.Unbind,
        GpuDiagMode.IdleUnbound,
        GpuDiagMode.Finish,
        GpuDiagMode.ExtraCtx,
        GpuDiagMode.ExtraDeleted,
        GpuDiagMode.Swap,
        GpuDiagMode.Baseline,
    };

    private readonly GpuDiagMode[] _order;
    private readonly int _warmupWindows;
    private readonly int _blockWindows;

    public GpuDiagSchedule(int windowSeconds = 10, int warmupSeconds = 30, int blockSeconds = 40,
        GpuDiagMode[]? order = null)
    {
        if (windowSeconds <= 0 || warmupSeconds < 0 || blockSeconds < windowSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        }
        _order = order is { Length: > 0 } ? order : DefaultOrder;
        _warmupWindows = warmupSeconds / windowSeconds;
        _blockWindows = blockSeconds / windowSeconds;
    }

    public int TotalWindows => _warmupWindows + _blockWindows * _order.Length;

    public bool IsDone(int window) => window >= TotalWindows;

    public GpuDiagMode ModeFor(int window)
    {
        if (window < _warmupWindows)
        {
            return GpuDiagMode.BaselineStart;
        }
        if (IsDone(window))
        {
            return GpuDiagMode.Baseline;
        }
        return _order[(window - _warmupWindows) / _blockWindows];
    }

    /// <summary>True for the very first window and the first window after a mode change, whose numbers still carry the previous state.</summary>
    public bool IsSettleWindow(int window) =>
        window == 0 || (window >= _warmupWindows && !IsDone(window) && (window - _warmupWindows) % _blockWindows == 0);
}

/// <summary>One stats window, as the summary consumes it.</summary>
public sealed record GpuDiagWindow(
    GpuDiagMode Mode, bool Settle, int Frames, double ProcPctOfCore, double[] PhaseCpuMsPerSec, double Seconds = 10,
    int IcdThreads = 0, double IcdCpuMsPerSec = 0);

public sealed record GpuDiagModeSummary(
    GpuDiagMode Mode, int Windows, double MedianProcPctOfCore, double[] MedianPhaseCpuMsPerSec,
    double MedianProcCpuMsPerFrame = 0, double[]? MedianPhaseCpuMsPerFrame = null,
    double MedianIcdThreads = 0, double MedianIcdCpuMsPerSec = 0);

public static class GpuDiagSummary
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var sorted = new double[values.Count];
        for (var i = 0; i < sorted.Length; i++)
        {
            sorted[i] = values[i];
        }
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Median per mode over windows that are not settle windows, in first-seen mode order.</summary>
    public static List<GpuDiagModeSummary> Summarize(IReadOnlyList<GpuDiagWindow> windows, int phaseCount)
    {
        var result = new List<GpuDiagModeSummary>();
        var seen = new List<GpuDiagMode>();
        foreach (var w in windows)
        {
            if (!seen.Contains(w.Mode))
            {
                seen.Add(w.Mode);
            }
        }
        foreach (var mode in seen)
        {
            var proc = new List<double>();
            var procFrame = new List<double>();
            var icdThreads = new List<double>();
            var icdCpu = new List<double>();
            var phasesFrame = new List<double>[phaseCount];
            var phases = new List<double>[phaseCount];
            for (var p = 0; p < phaseCount; p++)
            {
                phases[p] = new List<double>();
                phasesFrame[p] = new List<double>();
            }
            foreach (var w in windows)
            {
                if (w.Mode != mode || w.Settle)
                {
                    continue;
                }
                proc.Add(w.ProcPctOfCore);
                icdThreads.Add(w.IcdThreads);
                icdCpu.Add(w.IcdCpuMsPerSec);
                var perFrame = w.Frames > 0 ? w.Seconds / w.Frames : 0;
                procFrame.Add(w.ProcPctOfCore / 100.0 * 1000.0 * perFrame);
                for (var p = 0; p < phaseCount && p < w.PhaseCpuMsPerSec.Length; p++)
                {
                    phases[p].Add(w.PhaseCpuMsPerSec[p]);
                    phasesFrame[p].Add(w.PhaseCpuMsPerSec[p] * perFrame);
                }
            }
            var med = new double[phaseCount];
            var medFrame = new double[phaseCount];
            for (var p = 0; p < phaseCount; p++)
            {
                med[p] = Median(phases[p]);
                medFrame[p] = Median(phasesFrame[p]);
            }
            result.Add(new GpuDiagModeSummary(mode, proc.Count, Median(proc), med, Median(procFrame), medFrame,
                Median(icdThreads), Median(icdCpu)));
        }
        return result;
    }
}
