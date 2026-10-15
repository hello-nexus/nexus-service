using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Render-path variants the diagnostic A/B sequence cycles through.</summary>
public enum GpuDiagMode
{
    Baseline,
    AsyncReadback,
    SleepWait,
    BgraReadback,
    NoReadback,
    NoRender,
}

/// <summary>
/// Maps a stats window index to the mode that runs during it: warmup in
/// baseline, then each mode for a fixed block, the whole list repeated, then
/// baseline for good. Pure, so the order and boundaries are unit-testable.
/// </summary>
public sealed class GpuDiagSchedule
{
    public static readonly GpuDiagMode[] DefaultOrder =
    {
        GpuDiagMode.Baseline,
        GpuDiagMode.NoRender,
        GpuDiagMode.NoReadback,
        GpuDiagMode.SleepWait,
        GpuDiagMode.AsyncReadback,
        GpuDiagMode.BgraReadback,
    };

    private readonly GpuDiagMode[] _order;
    private readonly int _warmupWindows;
    private readonly int _blockWindows;
    private readonly int _cycles;

    public GpuDiagSchedule(int windowSeconds = 10, int warmupSeconds = 120, int blockSeconds = 90,
        int cycles = 2, GpuDiagMode[]? order = null)
    {
        if (windowSeconds <= 0 || warmupSeconds < 0 || blockSeconds < windowSeconds || cycles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        }
        _order = order is { Length: > 0 } ? order : DefaultOrder;
        _warmupWindows = warmupSeconds / windowSeconds;
        _blockWindows = blockSeconds / windowSeconds;
        _cycles = cycles;
    }

    public int TotalWindows => _warmupWindows + _blockWindows * _order.Length * _cycles;

    public bool IsDone(int window) => window >= TotalWindows;

    public bool IsWarmup(int window) => window < _warmupWindows;

    public GpuDiagMode ModeFor(int window)
    {
        if (window < _warmupWindows || IsDone(window))
        {
            return GpuDiagMode.Baseline;
        }
        var block = (window - _warmupWindows) / _blockWindows;
        return _order[block % _order.Length];
    }

    /// <summary>True for the first window after a mode change, whose numbers still carry the previous mode's pipeline state.</summary>
    public bool IsSettleWindow(int window) =>
        window >= _warmupWindows && !IsDone(window) && (window - _warmupWindows) % _blockWindows == 0;
}

/// <summary>One stats window, as the summary consumes it.</summary>
public sealed record GpuDiagWindow(
    GpuDiagMode Mode, bool Settle, int Frames, double ProcPctOfCore, double[] PhaseCpuMsPerSec, double Seconds = 10);

public sealed record GpuDiagModeSummary(
    GpuDiagMode Mode, int Windows, double MedianProcPctOfCore, double[] MedianPhaseCpuMsPerSec,
    double MedianProcCpuMsPerFrame = 0, double[]? MedianPhaseCpuMsPerFrame = null);

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

    /// <summary>Median per mode over windows that rendered frames and are not settle windows, in first-seen mode order.</summary>
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
            var phasesFrame = new List<double>[phaseCount];
            var phases = new List<double>[phaseCount];
            for (var p = 0; p < phaseCount; p++)
            {
                phases[p] = new List<double>();
                phasesFrame[p] = new List<double>();
            }
            foreach (var w in windows)
            {
                if (w.Mode != mode || w.Settle || w.Frames <= 0)
                {
                    continue;
                }
                proc.Add(w.ProcPctOfCore);
                procFrame.Add(w.ProcPctOfCore / 100.0 * w.Seconds * 1000.0 / w.Frames);
                for (var p = 0; p < phaseCount && p < w.PhaseCpuMsPerSec.Length; p++)
                {
                    phases[p].Add(w.PhaseCpuMsPerSec[p]);
                    phasesFrame[p].Add(w.PhaseCpuMsPerSec[p] * w.Seconds / w.Frames);
                }
            }
            var med = new double[phaseCount];
            var medFrame = new double[phaseCount];
            for (var p = 0; p < phaseCount; p++)
            {
                med[p] = Median(phases[p]);
                medFrame[p] = Median(phasesFrame[p]);
            }
            result.Add(new GpuDiagModeSummary(mode, proc.Count, Median(proc), med, Median(procFrame), medFrame));
        }
        return result;
    }
}
