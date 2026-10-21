#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Bench;

internal static partial class LightingBench
{
    private const int TargetHz = BenchCadence.Hz;
    private const int PeriodMs = BenchCadence.PeriodMs;
    private const int LookAheadFrames = 30;
    private const int ReadbackProbeReps = 20;
    private static readonly long[] FingerprintTicksMs = { 1000, 5000 };

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    private static ulong ThreadCycles()
    {
        QueryThreadCycleTime(GetCurrentThread(), out var c);
        return c;
    }

    private static long PrivateBytes()
    {
        using var p = Process.GetCurrentProcess();
        return p.PrivateMemorySize64;
    }

    /// <summary>Reads the GL thread's cycle counter and drains its error queue. Dispatched through
    /// the context so it runs on the thread that owns the work.</summary>
    private sealed class GlProbe
    {
        private readonly GpuContext _ctx;
        private readonly Action _run;
        public ulong Cycles;
        public int Errors;

        public GlProbe(GpuContext ctx)
        {
            _ctx = ctx;
            _run = Run;
        }

        public void Snapshot() => _ctx.Invoke(_run);

        private void Run()
        {
            Cycles = ThreadCycles();
            var gl = _ctx.Gl;
            for (var i = 0; i < 8; i++)
            {
                if (gl.GetError() == GLEnum.NoError)
                {
                    break;
                }
                Errors++;
            }
        }
    }

    /// <summary>Fixed-rate pacing on the same high-resolution timer the engine loop uses.</summary>
    private sealed class Pacer : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly HighResolutionTicker? _ticker;
        private long _next;

        public Pacer()
        {
            _ticker = HighResolutionTicker.TryCreate(_cts.Token);
        }

        public void Wait()
        {
            if (_ticker is not null)
            {
                _ticker.WaitForNextTick(PeriodMs);
                return;
            }
            var period = PeriodMs * Stopwatch.Frequency / 1000;
            var now = Stopwatch.GetTimestamp();
            _next = _next == 0 || now - _next > period ? now + period : _next + period;
            var waitMs = (int)((_next - now) * 1000 / Stopwatch.Frequency);
            if (waitMs > 0)
            {
                Thread.Sleep(waitMs);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _ticker?.Dispose();
            _cts.Dispose();
        }
    }

    public static int RunChild(string[] args)
    {
        var error = BenchOptions.TryParse(args, child: true, out var o);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
        var kind = o.Adapter switch
        {
            "integrated" => GpuAdapterKind.Integrated,
            "dedicated" => GpuAdapterKind.Dedicated,
            "warp" => GpuAdapterKind.Warp,
            _ => GpuAdapterKind.Auto,
        };
        GpuAdapterRequest.Set(kind, o.Luid);

        var result = new BenchComboResult
        {
            Backend = o.Backend,
            Adapter = o.Adapter,
            AdapterLuid = o.Luid,
            PrivateBytesStart = PrivateBytes(),
        };
        var total = Stopwatch.StartNew();
        try
        {
            RunChildCore(o, result);
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            result.Detail = $"{ex.GetType().Name}: {ex.Message}";
        }
        result.PrivateBytesEnd = PrivateBytes();
        result.DurationMs = total.Elapsed.TotalMilliseconds;
        SaveChild(o.OutPath, result);
        return result.Status is "ok" or "partial" ? 0 : 1;
    }

    private static void SaveChild(string path, BenchComboResult result)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(result, BenchJsonContext.Default.BenchComboResult));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not write {path}: {ex.Message}");
        }
    }

    private static void RunChildCore(BenchOptions o, BenchComboResult result)
    {
        using var ctx = new GpuContext(160, 90)
        {
            InitTimeout = TimeSpan.FromSeconds(60),
            BackendOverride = o.Backend,
            // The first frame is timed as compile plus link and must come back rendered.
            AsyncCompile = false,
        };
        var initSw = Stopwatch.StartNew();
        lock (ctx.Lock)
        {
            ctx.EnsureInitializedLocked();
        }
        var ready = ctx.WaitForInit(ctx.InitTimeout);
        result.InitMs = initSw.Elapsed.TotalMilliseconds;
        result.PrivateBytesAfterInit = PrivateBytes();
        if (!ready)
        {
            result.Status = ctx.Failed ? "failed" : "timeout";
            result.Detail = "context init did not produce a context (see nexus-gpu.log)";
            return;
        }

        ctx.Invoke(() =>
        {
            var gl = ctx.Gl;
            result.GlVendor = gl.GetStringS(StringName.Vendor);
            result.GlRenderer = gl.GetStringS(StringName.Renderer);
            result.GlVersion = gl.GetStringS(StringName.Version);
        });
        Console.WriteLine($"context: {result.GlVendor} | {result.GlRenderer} | {result.GlVersion} ({result.InitMs:0} ms)");

        // An unknown backend name falls through to the default one, so a build that lacks the
        // requested backend would otherwise report the default's numbers under the wrong label.
        var isAngle = ((result.GlRenderer ?? "") + (result.GlVersion ?? "")).Contains("ANGLE", StringComparison.OrdinalIgnoreCase);
        if (isAngle != string.Equals(o.Backend, "angle", StringComparison.Ordinal))
        {
            result.Status = "mismatch";
            result.Detail = $"requested {o.Backend} but the context reports '{result.GlRenderer}' / '{result.GlVersion}'";
            return;
        }

        if (o.AdapterDescription is not null
            && (result.GlRenderer ?? "").IndexOf(o.AdapterDescription, StringComparison.OrdinalIgnoreCase) < 0)
        {
            result.Status = "mismatch";
            result.Detail = $"requested adapter '{o.AdapterDescription}' but the context reports '{result.GlRenderer}'";
            return;
        }

        if (o.Section == "ledspace")
        {
            RunLedSpace(o, ctx, result);
            return;
        }
        if (o.Section == "gate")
        {
            RunGateBench(o, ctx, result);
            return;
        }
        if (o.Section == "d3dflag")
        {
            RunD3dFlag(o, ctx, result);
            return;
        }

        var names = new List<string>((IEnumerable<string>?)o.Effects ?? ShaderLibrary.AllEffectKeys);
        var cyclesPerMs = CalibrateCyclesPerMs();
        using var engine = new LightingEngine();
        var probe = new GlProbe(ctx);
        result.Status = "partial";
        for (var i = 0; i < names.Count; i++)
        {
            Console.WriteLine($"effect {i + 1}/{names.Count} {names[i]}");
            result.Effects.Add(RunEffect(names[i], o.Seconds, ctx, engine, probe, cyclesPerMs));
            SaveChild(o.OutPath, result);
        }
        result.Status = "ok";
    }

    private static double CalibrateCyclesPerMs()
    {
        double best = 0;
        for (var i = 0; i < 3; i++)
        {
            var c0 = ThreadCycles();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < 30)
            {
                Thread.SpinWait(200);
            }
            var c1 = ThreadCycles();
            best = Math.Max(best, (c1 - c0) / sw.Elapsed.TotalMilliseconds);
        }
        return best > 0 ? best : 1;
    }

    private static BenchEffectResult RunEffect(
        string name, int seconds, GpuContext ctx, LightingEngine engine, GlProbe probe, double cyclesPerMs)
    {
        var r = new BenchEffectResult { Name = name };
        ShaderEffect? effect = null;
        try
        {
            if (!ShaderLibrary.IsKnownKey(name))
            {
                r.Error = "unknown effect key";
                return r;
            }
            var frag = ShaderLibrary.Get(name);
            effect = new ShaderEffect(name, ctx, frag);
            var canvas = new CanvasBuffer(ctx.Width, ctx.Height);
            var devices = BenchDevices.Create();

            probe.Snapshot();
            probe.Errors = 0;
            r.PrivateBytesBefore = PrivateBytes();

            // First frame carries compile and link; a warm frame follows to separate the two.
            var first = Stopwatch.StartNew();
            effect.RenderFrame(canvas, 0);
            r.FirstFrameMs = first.Elapsed.TotalMilliseconds;
            if (effect.HasFailed)
            {
                r.Error = ShaderEffect.ValidateFragment(ctx, frag) ?? "render failed (see nexus-gpu.log)";
                return r;
            }
            var warmMs = 0.0;
            foreach (var at in FingerprintTicksMs)
            {
                var w = Stopwatch.StartNew();
                effect.RenderFrame(canvas, at);
                if (warmMs == 0)
                {
                    warmMs = w.Elapsed.TotalMilliseconds;
                }
                r.Fingerprints.Add(new BenchFingerprintSample
                {
                    AtMs = at,
                    Hex = Convert.ToHexString(BenchFingerprint.Compute(canvas.Pixels, canvas.Width, canvas.Height)).ToLowerInvariant(),
                });
            }
            r.CompileMs = Math.Max(0, r.FirstFrameMs - warmMs);

            TimedRun(r, effect, canvas, devices, engine, ctx, probe, seconds, cyclesPerMs);
            ReadbackProbe(r, ctx);
            LookAhead(r, effect, engine, ctx);
            r.Batch = BatchComparison(effect, ctx, name);
            probe.Snapshot();
            r.GlErrors = probe.Errors;
            r.Ok = !effect.HasFailed;
            if (!r.Ok)
            {
                r.Error = "render failed during the run (see nexus-gpu.log)";
            }
            r.PrivateBytesAfter = PrivateBytes();
        }
        catch (Exception ex)
        {
            r.Ok = false;
            r.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            effect?.Dispose();
        }
        return r;
    }

    private static void TimedRun(
        BenchEffectResult r, ShaderEffect effect, CanvasBuffer canvas, DeviceFrame[] devices,
        LightingEngine engine, GpuContext ctx, GlProbe probe, int seconds, double cyclesPerMs)
    {
        var n = seconds * TargetHz;
        var interval = new double[Math.Max(0, n - 1)];
        var render = new List<double>(n);
        var sample = new double[n];
        var work = new double[n];
        var rcpu = new List<double>(n);
        var gcpu = new List<double>(n);
        var toMs = 1000.0 / Stopwatch.Frequency;
        var missed = 0;
        var renders = 0;
        long prevStart = 0;
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        probe.Snapshot();
        var glPrev = probe.Cycles;
        using var pacer = new Pacer();
        for (var i = 0; i < n; i++)
        {
            pacer.Wait();
            var t0 = Stopwatch.GetTimestamp();
            var c0 = ThreadCycles();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            effect.RenderFrame(canvas, nowMs);
            renders++;
            var t1 = Stopwatch.GetTimestamp();
            engine.SampleForBench(devices, canvas);
            foreach (var d in devices)
            {
                d.Publish();
            }
            var t2 = Stopwatch.GetTimestamp();
            var c1 = ThreadCycles();

            probe.Snapshot();
            if (i > 0)
            {
                interval[i - 1] = (t0 - prevStart) * toMs;
            }
            prevStart = t0;
            sample[i] = (t2 - t1) * toMs;
            work[i] = (t2 - t0) * toMs;
            if (work[i] > PeriodMs)
            {
                missed++;
            }
            render.Add((t1 - t0) * toMs);
            rcpu.Add((c1 - c0) / cyclesPerMs);
            gcpu.Add((probe.Cycles - glPrev) / cyclesPerMs);
            glPrev = probe.Cycles;
        }
        proc.Refresh();
        r.ProcessCpuMsPerFrame = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / Math.Max(1, n);
        r.Frames = n;
        r.RendersPerSecond = renders / (double)Math.Max(1, seconds);
        r.ProcessCpuMsPerSecond = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / Math.Max(1, seconds);
        r.MissedDeadlines = missed;
        r.FrameIntervalMs = BenchStats.Summarize(interval);
        r.RenderWallMs = BenchStats.Summarize(render.ToArray());
        r.SampleMs = BenchStats.Summarize(sample);
        r.WorkMs = BenchStats.Summarize(work);
        r.RenderThreadCpuMs = BenchStats.Summarize(rcpu.ToArray());
        r.GlThreadCpuMs = BenchStats.Summarize(gcpu.ToArray());
    }

    private static void ReadbackProbe(BenchEffectResult r, GpuContext ctx)
    {
        var times = new double[ReadbackProbeReps];
        ctx.Invoke(() =>
        {
            var gl = ctx.Gl;
            var buf = new byte[ctx.Width * ctx.Height * 3];
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, ctx.Fbo);
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            unsafe
            {
                fixed (byte* p = buf)
                {
                    for (var i = 0; i < times.Length; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        gl.ReadPixels(0, 0, (uint)ctx.Width, (uint)ctx.Height, PixelFormat.Rgb, PixelType.UnsignedByte, p);
                        times[i] = sw.Elapsed.TotalMilliseconds;
                    }
                }
            }
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        });
        r.ReadbackProbeMs = BenchStats.Summarize(times);
    }

    /// <summary>Back-to-back render and sample of a window of future frames, the work a look-ahead
    /// request does across several ticks.</summary>
    private static void LookAhead(BenchEffectResult r, ShaderEffect effect, LightingEngine engine, GpuContext ctx)
    {
        var canvas = new CanvasBuffer(ctx.Width, ctx.Height);
        var shadow = BenchDevices.Create();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < LookAheadFrames; i++)
        {
            effect.RenderFrame(canvas, start + (long)i * PeriodMs);
            engine.SampleForBench(shadow, canvas);
            foreach (var d in shadow)
            {
                d.Publish();
            }
        }
        var total = sw.Elapsed.TotalMilliseconds;
        r.LookAhead = new BenchLookAhead { Frames = LookAheadFrames, TotalMs = total, PerFrameMs = total / LookAheadFrames };
    }

    private const int BatchRounds = 100;
    private const int BatchSize = 8;

    private static string PixelHash(CanvasBuffer c) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(c.Pixels));

    /// <summary>The same look-ahead window and the same two-render tick, read back one by one and in batches the way the engine does.</summary>
    private static BenchBatch BatchComparison(ShaderEffect effect, GpuContext ctx, string name)
    {
        var b = new BenchBatch { Frames = LookAheadFrames, Rounds = BatchRounds };
        var canvas = new CanvasBuffer(ctx.Width, ctx.Height);
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var proc = Process.GetCurrentProcess();
        var expected = new string[LookAheadFrames];

        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        for (var round = 0; round < BatchRounds; round++)
        {
            for (var i = 0; i < LookAheadFrames; i++)
            {
                effect.RenderFrame(canvas, start + (long)i * PeriodMs);
                if (round == 0)
                {
                    expected[i] = PixelHash(canvas);
                }
            }
        }
        b.UnbatchedWallMsPerFrame = sw.Elapsed.TotalMilliseconds / (BatchRounds * LookAheadFrames);
        proc.Refresh();
        b.UnbatchedCpuMsPerFrame = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / (BatchRounds * LookAheadFrames);

        var identical = true;
        var active = false;
        proc.Refresh();
        cpu0 = proc.TotalProcessorTime;
        sw.Restart();
        for (var round = 0; round < BatchRounds; round++)
        {
            for (var at = 0; at < LookAheadFrames; at += BatchSize)
            {
                var n = Math.Min(BatchSize, LookAheadFrames - at);
                using var batch = ReadbackBatch.TryBegin(ctx);
                active |= batch is not null;
                for (var k = 0; k < n; k++)
                {
                    batch?.Group = k;
                    effect.RenderFrame(canvas, start + (long)(at + k) * PeriodMs);
                    if (batch is null && round == 0)
                    {
                        identical &= expected[at + k] == PixelHash(canvas);
                    }
                }
                for (var k = 0; k < n && batch is not null; k++)
                {
                    batch.Apply(k);
                    if (round == 0)
                    {
                        identical &= expected[at + k] == PixelHash(canvas);
                    }
                }
            }
        }
        b.BatchedWallMsPerFrame = sw.Elapsed.TotalMilliseconds / (BatchRounds * LookAheadFrames);
        proc.Refresh();
        b.BatchedCpuMsPerFrame = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / (BatchRounds * LookAheadFrames);
        b.BatchActive = active;
        b.Identical = identical;

        using var second = new ShaderEffect(name, ctx, ShaderLibrary.Get(name));
        var c2 = new CanvasBuffer(ctx.Width, ctx.Height);
        second.RenderFrame(c2, 0);
        var ticks = BatchRounds * LookAheadFrames;
        proc.Refresh();
        cpu0 = proc.TotalProcessorTime;
        for (var i = 0; i < ticks; i++)
        {
            effect.RenderFrame(canvas, start + (long)i * PeriodMs);
            second.RenderFrame(c2, start + (long)i * PeriodMs + 500);
        }
        var h1 = PixelHash(canvas) + PixelHash(c2);
        proc.Refresh();
        b.PairUnbatchedCpuMsPerTick = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / ticks;
        cpu0 = proc.TotalProcessorTime;
        for (var i = 0; i < ticks; i++)
        {
            using var batch = ReadbackBatch.TryBegin(ctx);
            effect.RenderFrame(canvas, start + (long)i * PeriodMs);
            second.RenderFrame(c2, start + (long)i * PeriodMs + 500);
        }
        var h2 = PixelHash(canvas) + PixelHash(c2);
        proc.Refresh();
        b.PairBatchedCpuMsPerTick = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / ticks;
        b.PairIdentical = h1 == h2;
        return b;
    }
}
#endif
