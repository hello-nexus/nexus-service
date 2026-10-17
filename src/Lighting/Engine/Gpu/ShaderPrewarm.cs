using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// The work list and accounting for the one-shot warm-up of the built-in
/// shader programs: which source is compiled next, when to give up, and the
/// summary line. Effects that share a source are compiled once.
/// </summary>
internal sealed class ShaderPrewarmPlan
{
    /// <summary>Pause between programs, so the warm-up is a trickle behind live work.</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(150);

    /// <summary>Settling time after the live effect's program lands before the first program.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait for a live program (a static color never builds one) before starting anyway.</summary>
    public static readonly TimeSpan LiveProgramWait = TimeSpan.FromMinutes(2);

    /// <summary>A warm-up only pays off when the compiled programs persist and compiling does not block the GL thread.</summary>
    public static bool Worthwhile(bool isEs, bool isWarp, bool asyncCompile, bool programCacheActive) =>
        isEs && !isWarp && asyncCompile && programCacheActive;

    private readonly List<(string Key, string Source)> _items = new();
    private int _next;

    public ShaderPrewarmPlan(IEnumerable<(string Key, string Source)> effects)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in effects)
        {
            if (seen.Add(e.Source))
            {
                _items.Add(e);
            }
        }
    }

    public int Total => _items.Count;
    public int Compiled { get; private set; }
    public int Failed { get; private set; }
    public double TotalMs { get; private set; }
    public bool Finished => _next >= _items.Count;

    public bool TryNext(out string key, out string source)
    {
        if (_next >= _items.Count)
        {
            key = source = "";
            return false;
        }
        (key, source) = _items[_next++];
        return true;
    }

    public void Record(bool ok, double ms)
    {
        if (ok)
        {
            Compiled++;
        }
        else
        {
            Failed++;
        }
        TotalMs += ms;
    }

    /// <summary>A lost, replaced or disposed context ends the run: its programs belong to a generation that is gone.</summary>
    public static bool ShouldStop(int startGeneration, int currentGeneration, bool available) =>
        !available || startGeneration != currentGeneration;

    public string Summary(bool stoppedEarly) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"[gpu] prewarm {(stoppedEarly ? "stopped" : "done")}: {Compiled} of {Total} programs compiled, {Failed} failed, {TotalMs:0}ms total");
}

/// <summary>
/// Compiles the built-in shader effects once per process, one program at a
/// time on the background lane, so ANGLE's program cache is warm before the
/// lighting page asks for them. The thread ends with the last program and
/// leaves nothing behind that wakes later.
/// </summary>
internal static class ShaderPrewarm
{
    private static int _started;

    public static void Start(GpuContext gpu)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        new Thread(() => Run(gpu)) { IsBackground = true, Name = "nexus-gpu-prewarm", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private static void Run(GpuContext gpu)
    {
        try
        {
            if (!gpu.WaitForLateLanding(TimeSpan.FromMinutes(10))
                || !ShaderPrewarmPlan.Worthwhile(gpu.IsEs, gpu.IsWarp, gpu.UseAsyncCompile, gpu.ProgramCacheActive))
            {
                return;
            }
            // The live effect's own compile goes first; the warm-up begins once it is out of the way.
            gpu.WaitForLiveProgram(ShaderPrewarmPlan.LiveProgramWait);
            Thread.Sleep(ShaderPrewarmPlan.Settle);
            var generation = gpu.Generation;
            var effects = new List<(string, string)>();
            foreach (var key in ShaderLibrary.AllEffectKeys)
            {
                try
                { effects.Add((key, ShaderLibrary.Get(key))); }
                catch (Exception) { }
            }
            var plan = new ShaderPrewarmPlan(effects);
            var stopped = false;
            while (plan.TryNext(out var key, out var source))
            {
                if (ShaderPrewarmPlan.ShouldStop(generation, gpu.Generation, gpu.Available))
                {
                    stopped = true;
                    break;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ok = false;
                var effect = new ShaderEffect(key, gpu, source);
                try
                { ok = effect.Prewarm(); }
                catch (Exception ex) { GpuContext.Log($"[gpu/{key}] prewarm threw: {ex.Message}"); }
                finally { effect.Dispose(); }
                plan.Record(ok, sw.Elapsed.TotalMilliseconds);
                Thread.Sleep(ShaderPrewarmPlan.Spacing);
            }
            GpuContext.Log(plan.Summary(stopped));
        }
        catch (Exception ex) { GpuContext.Log($"[gpu] prewarm threw: {ex.Message}"); }
    }
}
