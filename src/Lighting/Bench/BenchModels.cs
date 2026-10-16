using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Nexus.Service.Lighting.Bench;

public sealed class BenchFingerprintSample
{
    /// <summary>Effect clock the frame was rendered at, in milliseconds.</summary>
    public long AtMs { get; set; }

    /// <summary>Lowercase hex of the cell means, three bytes per cell.</summary>
    public string Hex { get; set; } = "";
}

public sealed class BenchLookAhead
{
    public int Frames { get; set; }
    public double TotalMs { get; set; }
    public double PerFrameMs { get; set; }
}

/// <summary>Batched against one-by-one readback of the same frames: whole-process CPU, wall time, and whether every pixel matched.</summary>
public sealed class BenchBatch
{
    public bool BatchActive { get; set; }
    public int Frames { get; set; }
    public int Rounds { get; set; }
    public double UnbatchedCpuMsPerFrame { get; set; }
    public double BatchedCpuMsPerFrame { get; set; }
    public double UnbatchedWallMsPerFrame { get; set; }
    public double BatchedWallMsPerFrame { get; set; }
    public bool Identical { get; set; }

    /// <summary>Two renders per tick (main effect plus a pinned look), one tick at a time.</summary>
    public double PairUnbatchedCpuMsPerTick { get; set; }
    public double PairBatchedCpuMsPerTick { get; set; }
    public bool PairIdentical { get; set; }
}

public sealed class BenchEffectResult
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }

    /// <summary>First frame minus a warm frame: compile and link plus first-use cost.</summary>
    public double CompileMs { get; set; }
    public double FirstFrameMs { get; set; }
    public int GlErrors { get; set; }
    public int Frames { get; set; }
    public int MissedDeadlines { get; set; }

    /// <summary>Start-to-start interval between consecutive frames.</summary>
    public Percentiles? FrameIntervalMs { get; set; }

    /// <summary>ShaderEffect.RenderFrame wall time: dispatch to the GL thread, draw, readback, flip.</summary>
    public Percentiles? RenderWallMs { get; set; }

    /// <summary>Device sampling plus publish wall time, as the engine tick does it.</summary>
    public Percentiles? SampleMs { get; set; }

    /// <summary>Render plus sample wall time per frame.</summary>
    public Percentiles? WorkMs { get; set; }

    /// <summary>CPU time of the render thread per frame.</summary>
    public Percentiles? RenderThreadCpuMs { get; set; }

    /// <summary>CPU time of the GL thread per frame, including one dispatch hop of measurement overhead.</summary>
    public Percentiles? GlThreadCpuMs { get; set; }

    /// <summary>Whole-process CPU per frame, averaged over the run.</summary>
    public double ProcessCpuMsPerFrame { get; set; }

    /// <summary>Shader renders per second actually done.</summary>
    public double RendersPerSecond { get; set; }

    /// <summary>Whole-process CPU per second of run.</summary>
    public double ProcessCpuMsPerSecond { get; set; }

    /// <summary>Warm ReadPixels of the idle target, measured after the timed run.</summary>
    public Percentiles? ReadbackProbeMs { get; set; }
    public long PrivateBytesBefore { get; set; }
    public long PrivateBytesAfter { get; set; }
    public List<BenchFingerprintSample> Fingerprints { get; set; } = new();
    public BenchLookAhead? LookAhead { get; set; }
    public BenchBatch? Batch { get; set; }
}

public sealed class BenchComboResult
{
    public string Backend { get; set; } = "";
    public string Adapter { get; set; } = "";

    /// <summary>ok, partial, skipped, failed, timeout, or mismatch.</summary>
    public string Status { get; set; } = "ok";
    public string? Detail { get; set; }
    public string? AdapterLuid { get; set; }
    public double InitMs { get; set; }
    public string? GlVendor { get; set; }
    public string? GlRenderer { get; set; }
    public string? GlVersion { get; set; }
    public long PrivateBytesStart { get; set; }
    public long PrivateBytesAfterInit { get; set; }
    public long PrivateBytesEnd { get; set; }
    public double DurationMs { get; set; }
    public List<BenchEffectResult> Effects { get; set; } = new();
    public BenchLedSpaceCombo? LedSpace { get; set; }
    public BenchD3dFlagSection? D3dFlag { get; set; }
}

public sealed class BenchThreadCpu
{
    public string Module { get; set; } = "";
    public int ThreadId { get; set; }
    public double CpuMsPerSec { get; set; }
}

/// <summary>One scenario on one device configuration: cadence of the live ticks and where the CPU went.</summary>
public sealed class BenchD3dRun
{
    public string Effect { get; set; } = "";

    /// <summary>live, live+P0 windows, or live+P2 windows.</summary>
    public string Scenario { get; set; } = "";
    public bool PreventThreading { get; set; }
    public int Ticks { get; set; }
    public Percentiles? IntervalMs { get; set; }
    public Percentiles? WorkMs { get; set; }

    /// <summary>Time from a window starting to its last frame being sampled.</summary>
    public Percentiles? WindowMs { get; set; }
    public double ProcessCpuMsPerSec { get; set; }
    public double DriverCpuMsPerSec { get; set; }
    public List<BenchThreadCpu> Threads { get; set; } = new();
    public bool WindowBytesEqual { get; set; }
}

public sealed class BenchD3dFlagSection
{
    public string Extensions { get; set; } = "";
    public bool DeviceCreationAvailable { get; set; }
    public string? Detail { get; set; }
    public List<BenchD3dRun> Runs { get; set; } = new();
}

/// <summary>Cost of one 30-frame window on one path.</summary>
public sealed class BenchLsPath
{
    public double CpuMsPerWindow { get; set; }
    public double WallMsPerWindow { get; set; }
    public double? GpuMsPerWindow { get; set; }
}

/// <summary>Byte comparison of a path against the full-canvas path, per LED and per tap.</summary>
public sealed class BenchLsEquality
{
    public int Leds { get; set; }
    public int ExactLeds { get; set; }
    public int MaxChannelDelta { get; set; }
    public int Taps { get; set; }
    public int ExactTaps { get; set; }
    public int TapMaxChannelDelta { get; set; }
    public string? FirstDiff { get; set; }
}

public sealed class BenchLsPlacement
{
    public string Name { get; set; } = "";
    public int Taps { get; set; }
    public BenchLsPath? P1 { get; set; }
    public BenchLsPath? P2 { get; set; }
    public BenchLsEquality? EqP1 { get; set; }
    public BenchLsEquality? EqP2 { get; set; }
    public bool P1P2Identical { get; set; }
}

public sealed class BenchLsEffect
{
    public string Name { get; set; } = "";
    public bool Audio { get; set; }

    /// <summary>pointwise, needs-canvas, or error.</summary>
    public string Class { get; set; } = "";
    public string? Detail { get; set; }
    public List<string> Flags { get; set; } = new();
    public BenchLsPath? P0 { get; set; }
    public BenchLsPath? P0Batched { get; set; }
    public List<BenchLsPlacement> Placements { get; set; } = new();
}

public sealed class BenchLedSpaceCombo
{
    public string LayoutSource { get; set; } = "";
    public int Leds { get; set; }
    public int Window { get; set; }
    public string CanvasSize { get; set; } = "";
    public string GpuTimer { get; set; } = "";
    public bool BatchReadback { get; set; }
    public List<string> PlacementNames { get; set; } = new();
    public List<BenchLsEffect> Effects { get; set; } = new();
}

public sealed class BenchEffectDelta
{
    public string Name { get; set; } = "";
    public int MaxChannelDelta { get; set; }
    public double MeanChannelDelta { get; set; }
}

public sealed class BenchComparison
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public int WorstMaxChannelDelta { get; set; }
    public List<BenchEffectDelta> Effects { get; set; } = new();
}

public sealed class BenchMachine
{
    public string Os { get; set; } = "";
    public string Host { get; set; } = "";
    public int Cores { get; set; }
    public string User { get; set; } = "";
    public int SessionId { get; set; }
}

public sealed class BenchRun
{
    public int Schema { get; set; } = 1;
    public string StartedUtc { get; set; } = "";
    public string ServiceVersion { get; set; } = "";
    public int Seconds { get; set; }
    public int TargetHz { get; set; } = BenchCadence.Hz;
    public BenchMachine Machine { get; set; } = new();
    public List<BenchComboResult> Results { get; set; } = new();
    public List<BenchComparison> Comparisons { get; set; } = new();
}

/// <summary>Journal of a registry value the bench changed, so a killed run is undone by the next one.</summary>
public sealed class BenchRestoreJournal
{
    public bool System { get; set; }
    public string ValueName { get; set; } = "";
    public bool HadValue { get; set; }
    public string? Previous { get; set; }

    /// <summary>What the bench wrote; the restore only applies while the registry still holds it.</summary>
    public string Written { get; set; } = "";
}

/// <summary>The one frame cadence every bench stage uses.</summary>
internal static class BenchCadence
{
    public const int Hz = 30;
    public const int PeriodMs = 1000 / Hz;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(BenchRun))]
[JsonSerializable(typeof(BenchComboResult))]
[JsonSerializable(typeof(BenchRestoreJournal))]
internal partial class BenchJsonContext : JsonSerializerContext;

/// <summary>Parsed command line for the parent (<c>--lighting-bench</c>) and its child.</summary>
internal sealed class BenchOptions
{
    public static readonly string[] KnownBackends = { "angle", "wgl" };
    public static readonly string[] KnownAdapters = { "auto", "integrated", "dedicated", "warp" };

    public string OutPath { get; set; } = "";
    public string[] Backends { get; set; } = { "angle", "wgl" };
    public string[] Adapters { get; set; } = { "integrated", "dedicated", "warp" };
    public int Seconds { get; set; } = 10;
    public string[]? Effects { get; set; }

    /// <summary>Empty for the standard run, or the name of a probe section that replaces it.</summary>
    public string Section { get; set; } = "";

    // Child only.
    public string Backend { get; set; } = "";
    public string Adapter { get; set; } = "auto";
    public string? Luid { get; set; }
    public string? AdapterDescription { get; set; }

    /// <summary>Parses args whose first element is the mode flag. Returns an error text or null.</summary>
    public static string? TryParse(string[] args, bool child, out BenchOptions options)
    {
        options = new BenchOptions();
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return "usage: --lighting-bench <out.json> [--backends angle,wgl] [--adapters integrated,dedicated,warp] [--seconds N] [--effects a,b] [--section ledspace|d3dflag]";
        }
        options.OutPath = args[1];
        for (var i = 2; i < args.Length; i++)
        {
            var key = args[i].ToLowerInvariant();
            if (i + 1 >= args.Length)
            {
                return $"missing value for {args[i]}";
            }
            var val = args[++i];
            switch (key)
            {
                case "--backends" when !child:
                    options.Backends = Split(val);
                    break;
                case "--adapters" when !child:
                    options.Adapters = Split(val);
                    break;
                case "--backend" when child:
                    options.Backend = val.ToLowerInvariant();
                    break;
                case "--adapter" when child:
                    options.Adapter = val.ToLowerInvariant();
                    break;
                case "--luid" when child:
                    options.Luid = val;
                    break;
                case "--adapter-name" when child:
                    options.AdapterDescription = val;
                    break;
                case "--effects":
                    options.Effects = Split(val);
                    break;
                case "--section":
                    options.Section = val.ToLowerInvariant();
                    if (options.Section is not ("ledspace" or "d3dflag"))
                    {
                        return "--section must be ledspace or d3dflag";
                    }
                    break;
                case "--seconds":
                    if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var s) || s < 1 || s > 600)
                    {
                        return "--seconds must be a whole number of seconds";
                    }
                    options.Seconds = s;
                    break;
                default:
                    return $"unknown argument {args[i - 1]}";
            }
        }
        var backends = child ? new[] { options.Backend } : options.Backends;
        var adapters = child ? new[] { options.Adapter } : options.Adapters;
        foreach (var b in backends)
        {
            if (!KnownBackends.Contains(b))
            {
                return $"unknown backend '{b}'";
            }
        }
        foreach (var a in adapters)
        {
            if (!KnownAdapters.Contains(a))
            {
                return $"unknown adapter '{a}'";
            }
        }
        if (backends.Length == 0 || adapters.Length == 0)
        {
            return "empty backend or adapter list";
        }
        return null;
    }

    private static string[] Split(string v) =>
        v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static s => s.ToLowerInvariant()).Distinct().ToArray();
}

/// <summary>Merging child results into one run, backend comparisons, and the human table.</summary>
internal static class BenchReport
{
    public static string Label(BenchComboResult c) => $"{c.Backend}/{c.Adapter}";

    /// <summary>Attaches the comparisons: every pair that shares an adapter class across backends, and
    /// every pair that shares a backend across adapter classes.</summary>
    public static BenchRun Merge(BenchRun run, IEnumerable<BenchComboResult> combos)
    {
        run.Results = combos.ToList();
        run.Comparisons = new List<BenchComparison>();
        var usable = run.Results.Where(static c => c.Effects.Any(static e => e.Ok)).ToList();
        for (var i = 0; i < usable.Count; i++)
        {
            for (var j = i + 1; j < usable.Count; j++)
            {
                var a = usable[i];
                var b = usable[j];
                var sameAdapter = a.Adapter == b.Adapter && a.Backend != b.Backend;
                var sameBackend = a.Backend == b.Backend && a.Adapter != b.Adapter;
                if (!sameAdapter && !sameBackend)
                {
                    continue;
                }
                var cmp = Compare(a, b);
                if (cmp.Effects.Count > 0)
                {
                    run.Comparisons.Add(cmp);
                }
            }
        }
        return run;
    }

    /// <summary>Per-effect largest channel delta between two combos, over fingerprint times both hold.</summary>
    public static BenchComparison Compare(BenchComboResult a, BenchComboResult b)
    {
        var cmp = new BenchComparison { A = Label(a), B = Label(b) };
        var bByName = b.Effects.Where(static e => e.Ok).ToDictionary(static e => e.Name, StringComparer.Ordinal);
        foreach (var ea in a.Effects.Where(static e => e.Ok))
        {
            if (!bByName.TryGetValue(ea.Name, out var eb))
            {
                continue;
            }
            var max = 0;
            double meanSum = 0;
            var n = 0;
            foreach (var fa in ea.Fingerprints)
            {
                var fb = eb.Fingerprints.FirstOrDefault(f => f.AtMs == fa.AtMs);
                if (fb is null)
                {
                    continue;
                }
                var ba = Convert.FromHexString(fa.Hex);
                var bb = Convert.FromHexString(fb.Hex);
                if (ba.Length != bb.Length)
                {
                    continue;
                }
                max = Math.Max(max, BenchFingerprint.MaxChannelDelta(ba, bb));
                meanSum += BenchFingerprint.MeanChannelDelta(ba, bb);
                n++;
            }
            if (n == 0)
            {
                continue;
            }
            cmp.Effects.Add(new BenchEffectDelta { Name = ea.Name, MaxChannelDelta = max, MeanChannelDelta = meanSum / n });
        }
        cmp.WorstMaxChannelDelta = cmp.Effects.Count == 0 ? 0 : cmp.Effects.Max(static e => e.MaxChannelDelta);
        return cmp;
    }

    public static string FormatTable(BenchRun run)
    {
        if (run.Results.Any(static c => c.D3dFlag is not null))
        {
            return BenchLedSpaceReport.FormatD3d(run);
        }
        if (run.Results.Any(static c => c.LedSpace is not null))
        {
            return BenchLedSpaceReport.Format(run);
        }
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine($"Lighting bench  {run.StartedUtc}  v{run.ServiceVersion}  {run.Seconds}s/effect @ {run.TargetHz} Hz  {run.Machine.Host} session {run.Machine.SessionId}");
        sb.AppendLine();
        sb.AppendLine("Combos");
        sb.AppendLine(Row(new[] { "combo", "status", "init ms", "effects", "fail", "work p95 med", "work p99 max", "cpu/frame med", "renderer" }, ComboWidths));
        foreach (var c in run.Results)
        {
            var ok = c.Effects.Where(static e => e.Ok && e.WorkMs is not null).ToList();
            var work95 = ok.Select(static e => e.WorkMs!.P95).OrderBy(static v => v).ToArray();
            var cpu = ok.Select(static e => e.ProcessCpuMsPerFrame).OrderBy(static v => v).ToArray();
            sb.AppendLine(Row(new[]
            {
                Label(c), c.Status, c.InitMs.ToString("0", ci), c.Effects.Count.ToString(ci),
                c.Effects.Count(static e => !e.Ok).ToString(ci),
                work95.Length == 0 ? "-" : BenchStats.Percentile(work95, 0.5).ToString("0.00", ci),
                ok.Count == 0 ? "-" : ok.Max(static e => e.WorkMs!.P99).ToString("0.00", ci),
                cpu.Length == 0 ? "-" : BenchStats.Percentile(cpu, 0.5).ToString("0.00", ci),
                c.Status == "ok" || c.Status == "partial" ? (c.GlRenderer ?? "") : (c.Detail ?? ""),
            }, ComboWidths));
        }
        foreach (var c in run.Results.Where(static c => c.Effects.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine($"{Label(c)}  {c.GlVendor} | {c.GlRenderer} | {c.GlVersion}  init {c.InitMs.ToString("0", ci)} ms");
            sb.AppendLine(Row(new[] { "effect", "compile", "p50", "p95", "p99", "max", "render p95", "sample p95", "cpu/frame", "glerr", "miss", "ahead/f", "fps", "cpu ms/s" }, EffectWidths));
            foreach (var e in c.Effects)
            {
                if (!e.Ok || e.WorkMs is null || e.RenderWallMs is null || e.SampleMs is null)
                {
                    sb.AppendLine(Row(new[] { e.Name, "FAILED", e.Error ?? "" }, new[] { 18, 8, 60 }));
                    continue;
                }
                sb.AppendLine(Row(new[]
                {
                    e.Name, e.CompileMs.ToString("0.0", ci),
                    e.WorkMs.P50.ToString("0.00", ci), e.WorkMs.P95.ToString("0.00", ci),
                    e.WorkMs.P99.ToString("0.00", ci), e.WorkMs.Max.ToString("0.00", ci),
                    e.RenderWallMs.P95.ToString("0.00", ci), e.SampleMs.P95.ToString("0.00", ci),
                    e.ProcessCpuMsPerFrame.ToString("0.00", ci), e.GlErrors.ToString(ci),
                    e.MissedDeadlines.ToString(ci),
                    e.LookAhead is null ? "-" : e.LookAhead.PerFrameMs.ToString("0.00", ci),
                    e.RendersPerSecond.ToString("0.0", ci), e.ProcessCpuMsPerSecond.ToString("0.0", ci),
                }, EffectWidths));
            }
        }
        foreach (var cmp in run.Comparisons)
        {
            sb.AppendLine();
            sb.AppendLine($"Output delta {cmp.A} vs {cmp.B}  worst max channel delta {cmp.WorstMaxChannelDelta.ToString(ci)} (0..255), {cmp.Effects.Count.ToString(ci)} effects");
            foreach (var d in cmp.Effects.OrderByDescending(static d => d.MaxChannelDelta).Take(10))
            {
                sb.AppendLine($"  {d.Name,-18} max {d.MaxChannelDelta,3}  mean {d.MeanChannelDelta.ToString("0.00", ci)}");
            }
        }
        return sb.ToString();
    }

    private static readonly int[] ComboWidths = { 18, 9, 8, 8, 5, 13, 13, 14, 40 };
    private static readonly int[] EffectWidths = { 18, 8, 7, 7, 7, 7, 11, 11, 10, 6, 5, 8, 6, 9 };

    private static string Row(string[] cells, int[] widths)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            var w = i < widths.Length ? widths[i] : 0;
            sb.Append(i == cells.Length - 1 ? cells[i] : cells[i].PadRight(w) + " ");
        }
        return sb.ToString();
    }
}
