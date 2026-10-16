using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Nexus.Service.Lighting.Bench;

/// <summary>Human table for the LED-space probe section.</summary>
internal static class BenchLedSpaceReport
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string Format(BenchRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LED-space probe  {run.StartedUtc}  v{run.ServiceVersion}  {run.Machine.Host} session {run.Machine.SessionId}");
        foreach (var c in run.Results)
        {
            sb.AppendLine();
            sb.AppendLine($"{BenchReport.Label(c)}  {c.Status}  {(c.Status is "ok" or "partial" ? $"{c.GlVendor} | {c.GlRenderer} | {c.GlVersion}" : c.Detail)}");
            if (c.LedSpace is { } ls)
            {
                AppendCombo(sb, ls);
            }
        }
        return sb.ToString();
    }

    public static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(static v => v).ToArray();
        return a.Length == 0 ? 0 : BenchStats.Percentile(a, 0.5);
    }

    public static double P95(IEnumerable<double> values)
    {
        var a = values.OrderBy(static v => v).ToArray();
        return a.Length == 0 ? 0 : BenchStats.Percentile(a, 0.95);
    }

    private static string F(double v) => v.ToString("0.00", Ci);

    private static void AppendCombo(StringBuilder sb, BenchLedSpaceCombo ls)
    {
        sb.AppendLine($"layout: {ls.LayoutSource}, {ls.Leds} LEDs, canvas {ls.CanvasSize}, window {ls.Window} frames, gpu timer: {ls.GpuTimer}, readback batching: {ls.BatchReadback}");
        var ok = ls.Effects.Where(static e => e.Class == "pointwise").ToList();
        sb.AppendLine($"effects {ls.Effects.Count}: pointwise {ok.Count}, needs-canvas {ls.Effects.Count(static e => e.Class == "needs-canvas")}, error {ls.Effects.Count(static e => e.Class == "error")}");
        sb.AppendLine();
        sb.AppendLine("CPU ms per window (median / p95 across pointwise effects), wall ms, gpu ms");
        sb.AppendLine("path                      cpu med   cpu p95   wall med  wall p95  gpu med");
        Line(sb, "P0 full canvas", ok.Select(static e => e.P0));
        Line(sb, "P0b full canvas batched", ok.Select(static e => e.P0Batched));
        for (var pi = 0; pi < ls.PlacementNames.Count; pi++)
        {
            var idx = pi;
            Line(sb, $"P1 {ls.PlacementNames[pi]}", ok.Select(e => idx < e.Placements.Count ? e.Placements[idx].P1 : null));
            Line(sb, $"P2 {ls.PlacementNames[pi]}", ok.Select(e => idx < e.Placements.Count ? e.Placements[idx].P2 : null));
        }
        sb.AppendLine();
        sb.AppendLine("ratio of CPU to P0 (median across pointwise effects)");
        for (var pi = 0; pi < ls.PlacementNames.Count; pi++)
        {
            var idx = pi;
            var r1 = ok.Where(e => e.P0 is not null && idx < e.Placements.Count && e.Placements[idx].P1 is not null)
                .Select(e => e.Placements[idx].P1!.CpuMsPerWindow / Math.Max(1e-9, e.P0!.CpuMsPerWindow));
            var r2 = ok.Where(e => e.P0 is not null && idx < e.Placements.Count && e.Placements[idx].P2 is not null)
                .Select(e => e.Placements[idx].P2!.CpuMsPerWindow / Math.Max(1e-9, e.P0!.CpuMsPerWindow));
            sb.AppendLine($"  {ls.PlacementNames[pi],-24} P1 {F(Median(r1))}  P2 {F(Median(r2))}");
        }
        sb.AppendLine();
        sb.AppendLine("equality against P0 (LED level, then tap level)");
        for (var pi = 0; pi < ls.PlacementNames.Count; pi++)
        {
            var idx = pi;
            foreach (var path in new[] { "P1", "P2" })
            {
                var eqs = ok.Where(e => idx < e.Placements.Count)
                    .Select(e => path == "P1" ? e.Placements[idx].EqP1 : e.Placements[idx].EqP2)
                    .Where(static q => q is not null).Select(static q => q!).ToList();
                if (eqs.Count == 0)
                {
                    continue;
                }
                var allExact = eqs.Count(static q => q.ExactLeds == q.Leds);
                sb.AppendLine(
                    $"  {ls.PlacementNames[pi],-24} {path}: effects fully exact {allExact}/{eqs.Count}, "
                    + $"exact LEDs {eqs.Sum(static q => (long)q.ExactLeds)}/{eqs.Sum(static q => (long)q.Leds)}, max channel delta {eqs.Max(static q => q.MaxChannelDelta)}, "
                    + $"exact taps {eqs.Sum(static q => (long)q.ExactTaps)}/{eqs.Sum(static q => (long)q.Taps)}, tap max delta {eqs.Max(static q => q.TapMaxChannelDelta)}");
            }
        }
        sb.AppendLine();
        var notExact = ls.Effects.Where(static e => e.Placements.Any(static p => p.EqP2 is { } q && q.ExactLeds != q.Leds)).ToList();
        if (notExact.Count > 0)
        {
            sb.AppendLine("effects with any LED difference on P2:");
            foreach (var e in notExact)
            {
                foreach (var p in e.Placements.Where(static p => p.EqP2 is { } q && q.ExactLeds != q.Leds))
                {
                    var q = p.EqP2!;
                    sb.AppendLine($"  {e.Name,-18} {p.Name,-22} exact {q.ExactLeds}/{q.Leds} max {q.MaxChannelDelta} tap max {q.TapMaxChannelDelta} {q.FirstDiff}");
                }
            }
            sb.AppendLine();
        }
        var bad = ls.Effects.Where(static e => e.Class != "pointwise").ToList();
        if (bad.Count > 0)
        {
            sb.AppendLine("not pointwise or failed:");
            foreach (var e in bad)
            {
                sb.AppendLine($"  {e.Name,-18} {e.Class,-12} {string.Join(',', e.Flags)} {e.Detail}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("per effect, CPU ms per window (first placement for P1 and P2)");
        sb.AppendLine("effect             class      P0      P0b     P1      P2      P2/P0  wall P0  wall P2  gpu P0  gpu P2  exact P2");
        foreach (var e in ls.Effects)
        {
            var p = e.Placements.Count > 0 ? e.Placements[0] : null;
            var ratio = e.P0 is not null && p?.P2 is not null ? p.P2.CpuMsPerWindow / Math.Max(1e-9, e.P0.CpuMsPerWindow) : double.NaN;
            sb.AppendLine(
                $"{e.Name,-18} {e.Class,-10} {Cell(e.P0?.CpuMsPerWindow)} {Cell(e.P0Batched?.CpuMsPerWindow)} {Cell(p?.P1?.CpuMsPerWindow)} {Cell(p?.P2?.CpuMsPerWindow)} "
                + $"{Cell(ratio)} {Cell(e.P0?.WallMsPerWindow)}  {Cell(p?.P2?.WallMsPerWindow)}  {Cell(e.P0?.GpuMsPerWindow)}  {Cell(p?.P2?.GpuMsPerWindow)}  "
                + (p?.EqP2 is { } q2 ? $"{q2.ExactLeds}/{q2.Leds}" : "-"));
        }
    }

    private static string Cell(double? v) => (v is null || double.IsNaN(v.Value) ? "-" : F(v.Value)).PadRight(7);

    private static void Line(StringBuilder sb, string label, IEnumerable<BenchLsPath?> paths)
    {
        var l = paths.Where(static p => p is not null).Select(static p => p!).ToList();
        if (l.Count == 0)
        {
            return;
        }
        var gpu = l.Where(static p => p.GpuMsPerWindow is not null).Select(static p => p.GpuMsPerWindow!.Value).ToList();
        sb.AppendLine(
            $"{label,-25} {F(Median(l.Select(static p => p.CpuMsPerWindow))),-9} {F(P95(l.Select(static p => p.CpuMsPerWindow))),-9} "
            + $"{F(Median(l.Select(static p => p.WallMsPerWindow))),-9} {F(P95(l.Select(static p => p.WallMsPerWindow))),-9} {(gpu.Count == 0 ? "-" : F(Median(gpu)))}");
    }
}
