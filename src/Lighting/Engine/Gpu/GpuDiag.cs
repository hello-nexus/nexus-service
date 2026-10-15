using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Throwaway diagnostic: per-phase CPU accounting for the shader render path, GL ICD
/// thread attribution, and an automatic sequence of GL context lifecycle variants
/// applied on the GL thread. Starts on the first shader frame, stops after the
/// sequence, and always leaves the render path in baseline with the main context current.
/// NEXUS_GPU_DIAG=0 disables everything, NEXUS_GPU_DIAG_SEQUENCE=0 keeps the
/// accounting but never leaves baseline.
/// </summary>
public static class GpuDiag
{
    public enum Phase
    {
        EngineRender, EnginePins, EngineSample, EnginePublish, EngineBroadcast, EngineAhead,
        GlSetup, GlUniforms, GlDraw, GlSync, GlRead, GlPost,
    }

    public enum FrameContext { Other, Main, Pin, Ahead }

    private static readonly string[] PhaseNames =
    {
        "E.render", "E.pins", "E.sample", "E.publish", "E.broadcast", "E.ahead",
        "G.setup", "G.uniforms", "G.draw", "G.sync", "G.read", "G.post",
    };

    private const int PhaseCount = 12;
    private const int FirstGlPhase = (int)Phase.GlSetup;
    private const int WindowSeconds = 10;
    private const string Tag = "[gpu-diag]";

    private static readonly long[] Cycles = new long[PhaseCount];
    private static readonly long[] WallTicks = new long[PhaseCount];
    private static readonly long[] Counts = new long[PhaseCount];
    private static readonly long[] FrameCounts = new long[4];

    private static readonly object StateLock = new();
    private static readonly List<TrackedThread> Tracked = new();
    private static int _started;
    private static volatile bool _enabled;
    private static volatile bool _aborted;
    private static string? _abortReason;
    private static int _mode;
    private static GpuContext? _glCtx;
#if WINDOWS
    private static volatile bool _swapUnsupported;
    private static int _rebindFailLogs;
#endif
#pragma warning disable IDE0044
    private static long _swapStartTicks = 0;
#pragma warning restore IDE0044
    private static readonly List<double> SwapMs = new();

    // GL thread only: the diagnostic unbound the main context and it has not been rebound yet.
    private static bool _unbound;
#if WINDOWS
    private static IntPtr _extraRc;
#endif
    private static readonly Dictionary<uint, (long Created, string Name)> ModuleCache = new();

    [ThreadStatic]
    private static int t_registeredKinds;

    [ThreadStatic]
    private static FrameContext t_context;

    public static bool Enabled => _enabled;

    public static GpuDiagMode Mode => (GpuDiagMode)Volatile.Read(ref _mode);

    /// <summary>The calling thread's frame context; only the engine loop's own main-canvas render sets Main.</summary>
    public static FrameContext CallerContext => t_context;

    /// <summary>The variant a frame of this context renders with; only main-canvas frames leave baseline.</summary>
    public static GpuDiagMode ModeFor(FrameContext ctx) =>
        _enabled && ctx == FrameContext.Main ? Mode : GpuDiagMode.Baseline;

    /// <summary>GL thread, before every queued job: rebinds the main context if the diagnostic unbound it.</summary>
    /// <summary>False when the rebind failed: the job must be skipped, and the next job retries.</summary>
    public static bool BeforeJob(GpuContext ctx)
    {
        if (!_unbound)
        {
            return true;
        }
#if WINDOWS
        var (dc, rc) = ctx.GlHandles;
        if (dc != 0 && !WinWglContext.MakeCurrent(dc, rc))
        {
            if (_rebindFailLogs++ < 3)
            {
                GpuContext.Log($"{Tag} rebind of the main context failed (Win32 {Marshal.GetLastWin32Error()}); job skipped");
            }
            ForceBaseline("rebind failed");
            return false;
        }
#endif
        _unbound = false;
        return true;
    }

    /// <summary>GL thread, after every queued job: leaves the context not current in the unbound variants.</summary>
    public static void AfterJob(GpuContext ctx)
    {
        if (!_enabled)
        {
            return;
        }
        var m = Mode;
        if (m is not (GpuDiagMode.Unbind or GpuDiagMode.IdleUnbound))
        {
            return;
        }
#if WINDOWS
        var (dc, _) = ctx.GlHandles;
        if (dc != 0 && WinWglContext.MakeCurrent(0, 0))
        {
            _unbound = true;
        }
#endif
    }

    /// <summary>GL thread, end of a main-canvas frame: the present or sync variants.</summary>
    public static void AfterMainFrame(GL gl, FrameContext frameCtx, GpuContext ctx)
    {
        if (!_enabled || frameCtx != FrameContext.Main)
        {
            return;
        }
        var m = Mode;
        if (m is not (GpuDiagMode.Swap or GpuDiagMode.Finish))
        {
            return;
        }
        try
        {
            if (m == GpuDiagMode.Finish)
            {
                gl.Finish();
                return;
            }
            gl.Flush();
#if WINDOWS
            var (dc, _) = ctx.GlHandles;
            if (dc != 0 && !_swapUnsupported)
            {
                var t0 = Stopwatch.GetTimestamp();
                Volatile.Write(ref _swapStartTicks, t0);
                var ok = WinWglContext.Swap(dc);
                Volatile.Write(ref _swapStartTicks, 0);
                lock (SwapMs)
                {
                    SwapMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                }
                if (!ok)
                {
                    ForceBaseline($"SwapBuffers failed (Win32 {Marshal.GetLastWin32Error()})");
                }
            }
#endif
        }
        catch (Exception ex)
        {
            ForceBaseline($"{ToName(m)}: {ex.Message}");
        }
    }

    public static FrameContext SetContext(FrameContext ctx)
    {
        var prev = t_context;
        t_context = ctx;
        return prev;
    }

    public static void CountFrame(FrameContext ctx)
    {
        if (_enabled)
        {
            Interlocked.Increment(ref FrameCounts[(int)ctx]);
        }
    }

    public readonly struct Scope
    {
        private readonly int _phase;
        private readonly long _c0;
        private readonly long _w0;

        internal Scope(int phase)
        {
            _phase = phase;
            _c0 = phase < 0 ? 0 : GpuDiagNative.ThreadCycles();
            _w0 = phase < 0 ? 0 : Stopwatch.GetTimestamp();
        }

        public void End()
        {
            if (_phase < 0)
            {
                return;
            }
            var c = GpuDiagNative.ThreadCycles() - _c0;
            var w = Stopwatch.GetTimestamp() - _w0;
            Interlocked.Add(ref Cycles[_phase], c);
            Interlocked.Add(ref WallTicks[_phase], w);
            Interlocked.Increment(ref Counts[_phase]);
        }
    }

    public static Scope Begin(Phase phase)
    {
        if (!_enabled)
        {
            return new Scope(-1);
        }
        EnsureRegistered((int)phase >= FirstGlPhase ? 1 : 0);
        return new Scope((int)phase);
    }

    /// <summary>Called on the GL thread once per frame; the first call starts the diagnostic.</summary>
    public static void OnGlFrame(GL gl, string? renderer, GpuContext ctx)
    {
        if (Volatile.Read(ref _started) != 0 || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        if (EnvOff("NEXUS_GPU_DIAG"))
        {
            return;
        }
        try
        {
            var vendor = gl.GetStringS(StringName.Vendor);
            var version = gl.GetStringS(StringName.Version);
            gl.GetInteger(GetPName.MajorVersion, out int major);
            gl.GetInteger(GetPName.MinorVersion, out int minor);
            var (dc, rc) = ctx.GlHandles;
            GpuContext.Log($"{Tag} GL_VENDOR='{vendor}' GL_RENDERER='{renderer}' GL_VERSION='{version}' "
                + $"context={major}.{minor} backend={(dc != 0 ? "wgl" : "other, wgl variants are no-ops")} "
                + $"gl-thread={GpuDiagNative.GetCurrentThreadId()} logical-cpus={Environment.ProcessorCount}");
            _glCtx = ctx;
            _enabled = true;
            var thread = new Thread(() => Run(renderer ?? "?")) { IsBackground = true, Name = "nexus-gpu-diag" };
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Start();
        }
        catch (Exception ex)
        {
            _enabled = false;
            GpuContext.Log($"{Tag} start failed: {ex.Message}");
        }
    }

    /// <summary>Any GL error or exception in a non-baseline variant lands here: baseline for good.</summary>
    public static void ForceBaseline(string reason)
    {
        lock (StateLock)
        {
            if (_aborted)
            {
                return;
            }
            _aborted = true;
            _abortReason = reason;
            Volatile.Write(ref _mode, (int)GpuDiagMode.Baseline);
        }
        GpuContext.Log($"{Tag} forced to baseline: {reason}");
    }

    private static void SetMode(GpuDiagMode mode)
    {
        GpuDiagMode prev;
        lock (StateLock)
        {
            prev = Mode;
            if (_aborted || prev == mode)
            {
                return;
            }
            Volatile.Write(ref _mode, (int)mode);
        }
        // A job on the GL thread makes the mode take effect at once (unbind after it,
        // rebind before it) and runs the one-shot context actions of the extra-ctx pair.
        RunOnGl(() =>
        {
            if (mode == GpuDiagMode.Swap)
            {
                DisableSwapInterval();
            }
            else if (mode == GpuDiagMode.ExtraCtx)
            {
                CreateExtraContext();
            }
            else if (mode == GpuDiagMode.ExtraDeleted)
            {
                DeleteExtraContext();
            }
        });
    }

    private static void RunOnGl(Action work)
    {
        try
        {
            _glCtx?.Invoke(work);
        }
        catch (Exception ex)
        {
            ForceBaseline($"GL job threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // GL thread. A second context on the same DC and pixel format, made current once and
    // switched straight back to the main one, so the ICD sees a full context lifecycle
    // while the main context stays the only one that ever renders.
    private static void CreateExtraContext()
    {
#if WINDOWS
        var (dc, rc) = _glCtx!.GlHandles;
        if (dc == 0 || _extraRc != 0)
        {
            return;
        }
        var extra = WinWglContext.CreateSecondContext(dc);
        if (extra == 0)
        {
            throw new InvalidOperationException($"second wglCreateContext failed (Win32 {Marshal.GetLastWin32Error()})");
        }
        _extraRc = extra;
        GpuContext.Log($"{Tag} extra-ctx created (not current) {IcdSnapshot()}");
        try
        {
            if (!WinWglContext.MakeCurrent(dc, extra))
            {
                throw new InvalidOperationException($"make-current of the second context failed (Win32 {Marshal.GetLastWin32Error()})");
            }
            GpuContext.Log($"{Tag} extra-ctx made current once {IcdSnapshot()}");
        }
        finally
        {
            if (!WinWglContext.MakeCurrent(dc, rc))
            {
                _unbound = true;
            }
        }
        GpuContext.Log($"{Tag} extra-ctx main context current again {IcdSnapshot()}");
#endif
    }

    // GL thread. Never deletes the main context.
    private static void DeleteExtraContext()
    {
#if WINDOWS
        var (dc, rc) = _glCtx!.GlHandles;
        var extra = _extraRc;
        if (dc == 0 || extra == 0 || extra == rc)
        {
            return;
        }
        _extraRc = 0;
        var ok = WinWglContext.DeleteContext(extra);
        GpuContext.Log($"{Tag} extra-ctx deleted ok={ok} {IcdSnapshot()}");
#endif
    }

    // GL thread, main context current. The variant is skipped when swap-interval 0 cannot be set,
    // since a vsynced SwapBuffers on a hidden window could block the GL thread.
    private static void DisableSwapInterval()
    {
#if WINDOWS
        var ok = WinWglContext.DisableSwapInterval();
        if (!ok)
        {
            _swapUnsupported = true;
        }
        GpuContext.Log($"{Tag} swap interval 0 {(ok ? "set" : "unavailable; swap variant skipped")}");
#endif
    }

    // Leaves the render path as it was: no extra context, main context current.
    private static void Restore()
    {
        RunOnGl(DeleteExtraContext);
    }

    public static string ToName(GpuDiagMode m) => m switch
    {
        GpuDiagMode.Baseline => "baseline",
        GpuDiagMode.BaselineStart => "baseline-start",
        GpuDiagMode.Unbind => "unbind",
        GpuDiagMode.IdleUnbound => "idle-unbound",
        GpuDiagMode.Swap => "swap",
        GpuDiagMode.Finish => "finish",
        GpuDiagMode.ExtraCtx => "extra-ctx",
        GpuDiagMode.ExtraDeleted => "extra-deleted",
        _ => m.ToString(),
    };

    private static bool EnvOff(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v is "0" or "off" or "false";
    }

    private sealed class TrackedThread
    {
        public int Kind;
        public nint Handle;
        public long LastCpu100Ns;
        public long LastCycles;
    }

    private static void EnsureRegistered(int kind)
    {
        if ((t_registeredKinds & (1 << kind)) != 0 || !OperatingSystem.IsWindows())
        {
            return;
        }
        t_registeredKinds |= 1 << kind;
        var h = GpuDiagNative.OpenThread(GpuDiagNative.ThreadQueryLimitedInformation, 0, GpuDiagNative.GetCurrentThreadId());
        if (h == 0)
        {
            return;
        }
        var t = new TrackedThread { Kind = kind, Handle = h };
        (t.LastCpu100Ns, t.LastCycles) = ReadThread(h);
        lock (Tracked)
        {
            Tracked.Add(t);
        }
    }

    private static (long Cpu100Ns, long Cycles) ReadThread(nint h)
    {
        if (GpuDiagNative.GetThreadTimes(h, out _, out _, out var k, out var u) == 0)
        {
            return (0, 0);
        }
        GpuDiagNative.QueryThreadCycleTime(h, out var cyc);
        return (k + u, (long)cyc);
    }

    private static void Run(string renderer)
    {
        try
        {
            RunInner(renderer);
        }
        catch (Exception ex)
        {
            ForceBaseline($"diag thread threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Restore();
            _enabled = false;
            RunOnGl(() => { });
        }
    }

    private static void RunInner(string renderer)
    {
        var sequenceOn = !EnvOff("NEXUS_GPU_DIAG_SEQUENCE");
        var schedule = new GpuDiagSchedule();
        var windows = new List<GpuDiagWindow>();
        var prevThreadCpu = new Dictionary<uint, long>();
        using (var p0 = Process.GetCurrentProcess())
        {
            _ = p0.TotalProcessorTime;
        }
        _ = SampleThreads(prevThreadCpu, 1);
        var lastProc = ProcessCpu();
        DrainCounters();
        var sw = Stopwatch.StartNew();
        var lastElapsed = TimeSpan.Zero;
        var window = 0;
        GpuContext.Log($"{Tag} started; sequence={(sequenceOn ? "on" : "off")}, "
            + $"{schedule.TotalWindows * WindowSeconds}s total, window={WindowSeconds}s, "
            + $"order={string.Join(',', Array.ConvertAll(GpuDiagSchedule.DefaultOrder, ToName))} {IcdSnapshot()}");
        while (true)
        {
            var target = TimeSpan.FromSeconds((window + 1) * WindowSeconds);
            while (sw.Elapsed < target)
            {
                Thread.Sleep(Math.Max(1, (int)(target - sw.Elapsed).TotalMilliseconds));
            }
            var now = sw.Elapsed;
            var secs = (now - lastElapsed).TotalSeconds;
            lastElapsed = now;
            var mode = Mode;
            var procNow = ProcessCpu();
            var rec = Collect(renderer, mode, secs, procNow - lastProc, prevThreadCpu,
                sequenceOn && schedule.IsSettleWindow(window));
            lastProc = procNow;
            if (sequenceOn)
            {
                windows.Add(rec);
            }
            window++;
            if (_aborted)
            {
                Finish($"aborted ({_abortReason})", windows);
                return;
            }
            if (!sequenceOn)
            {
                continue;
            }
            if (schedule.IsDone(window))
            {
                SetMode(GpuDiagMode.Baseline);
                Finish("done", windows);
                return;
            }
            SetMode(schedule.ModeFor(window));
        }
    }

    private static TimeSpan ProcessCpu()
    {
        using var p = Process.GetCurrentProcess();
        return p.TotalProcessorTime;
    }

    private static void DrainCounters()
    {
        for (var i = 0; i < PhaseCount; i++)
        {
            Interlocked.Exchange(ref Cycles[i], 0);
            Interlocked.Exchange(ref WallTicks[i], 0);
            Interlocked.Exchange(ref Counts[i], 0);
        }
        for (var i = 0; i < FrameCounts.Length; i++)
        {
            Interlocked.Exchange(ref FrameCounts[i], 0);
        }
    }

    private static GpuDiagWindow Collect(string renderer, GpuDiagMode mode, double secs, TimeSpan procDelta,
        Dictionary<uint, long> prevThreadCpu, bool settle)
    {
        var procPct = procDelta.TotalSeconds / secs * 100.0;
        var tmPct = procPct / Environment.ProcessorCount;

        // Per thread kind: CPU ms from GetThreadTimes and the cycle delta it converts the phase cycle shares with.
        var kindCpuMs = new double[2];
        var kindCycles = new double[2];
        lock (Tracked)
        {
            foreach (var t in Tracked)
            {
                var (cpu, cyc) = ReadThread(t.Handle);
                kindCpuMs[t.Kind] += (cpu - t.LastCpu100Ns) / 10_000.0;
                kindCycles[t.Kind] += cyc - t.LastCycles;
                t.LastCpu100Ns = cpu;
                t.LastCycles = cyc;
            }
        }

        var cpuPerSec = new double[PhaseCount];
        var wallPerSec = new double[PhaseCount];
        var counts = new long[PhaseCount];
        var rawCycles = new long[PhaseCount];
        var phaseSum = new double[2];
        for (var i = 0; i < PhaseCount; i++)
        {
            var cyc = Interlocked.Exchange(ref Cycles[i], 0);
            var wall = Interlocked.Exchange(ref WallTicks[i], 0);
            counts[i] = Interlocked.Exchange(ref Counts[i], 0);
            rawCycles[i] = cyc;
            var kind = i >= FirstGlPhase ? 1 : 0;
            var msPerCycle = kindCycles[kind] > 0 ? kindCpuMs[kind] / kindCycles[kind] : 0;
            cpuPerSec[i] = cyc * msPerCycle / secs;
            wallPerSec[i] = wall * 1000.0 / Stopwatch.Frequency / secs;
            phaseSum[kind] += cpuPerSec[i];
        }
        var mainFrames = (int)Interlocked.Exchange(ref FrameCounts[(int)FrameContext.Main], 0);
        var pinFrames = Interlocked.Exchange(ref FrameCounts[(int)FrameContext.Pin], 0);
        var aheadFrames = Interlocked.Exchange(ref FrameCounts[(int)FrameContext.Ahead], 0);
        var otherFrames = Interlocked.Exchange(ref FrameCounts[(int)FrameContext.Other], 0);

        var threads = SampleThreads(prevThreadCpu, secs);
        var icdCount = 0;
        var icdCpu = 0.0;
        var icdText = new StringBuilder();
        foreach (var t in threads)
        {
            if (!IsIcd(t.Module))
            {
                continue;
            }
            icdCount++;
            icdCpu += t.Ms;
            icdText.Append(' ').Append(t.Tid.ToString(CultureInfo.InvariantCulture)).Append('=')
                .Append(t.Ms.ToString("0.0", CultureInfo.InvariantCulture));
        }

        var sb = new StringBuilder(512);
        var ci = CultureInfo.InvariantCulture;
        sb.Append(Tag).Append(" mode=").Append(ToName(mode)).Append(settle ? "(settle)" : "")
            .Append(" renderer='").Append(renderer).Append("' win=").Append(secs.ToString("0.0", ci)).Append("s frames=")
            .Append(mainFrames).Append(" pin=").Append(pinFrames).Append(" ahead=").Append(aheadFrames).Append(" other=").Append(otherFrames)
            .Append(" proc=").Append(procPct.ToString("0.0", ci)).Append("%core (")
            .Append(tmPct.ToString("0.00", ci)).Append("% TM)")
            .Append(" thread(cpu ms/s): eng=").Append((kindCpuMs[0] / secs).ToString("0.0", ci))
            .Append(" gl=").Append((kindCpuMs[1] / secs).ToString("0.0", ci))
            .Append(" eng-other=").Append((kindCpuMs[0] / secs - phaseSum[0]).ToString("0.0", ci))
            .Append(" gl-other=").Append((kindCpuMs[1] / secs - phaseSum[1]).ToString("0.0", ci))
            .Append(" phases(cpu/wall ms/s):");
        for (var i = 0; i < PhaseCount; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }
            sb.Append(' ').Append(PhaseNames[i]).Append('=').Append(cpuPerSec[i].ToString("0.0", ci))
                .Append('/').Append(wallPerSec[i].ToString("0.0", ci));
        }
        sb.Append(" cycles/s(M):");
        for (var i = 0; i < PhaseCount; i++)
        {
            if (counts[i] != 0)
            {
                sb.Append(' ').Append(PhaseNames[i]).Append('=').Append((rawCycles[i] / secs / 1e6).ToString("0.0", ci));
            }
        }
        sb.Append(" kind-cycles/s(M): eng=").Append((kindCycles[0] / secs / 1e6).ToString("0.0", ci))
            .Append(" gl=").Append((kindCycles[1] / secs / 1e6).ToString("0.0", ci));
        double[] swaps;
        lock (SwapMs)
        {
            swaps = SwapMs.ToArray();
            SwapMs.Clear();
        }
        var inFlight = Volatile.Read(ref _swapStartTicks);
        if (swaps.Length > 0 || inFlight != 0)
        {
            sb.Append(" swap-ms(n=").Append(swaps.Length).Append(" max=")
                .Append((swaps.Length > 0 ? Max(swaps) : 0).ToString("0.00", ci)).Append(" med=")
                .Append(GpuDiagSummary.Median(swaps).ToString("0.00", ci)).Append(')');
            if (inFlight != 0)
            {
                sb.Append(" swap-in-flight-ms=")
                    .Append(((Stopwatch.GetTimestamp() - inFlight) * 1000.0 / Stopwatch.Frequency).ToString("0", ci));
            }
        }
        sb.Append(" icd-threads=").Append(icdCount).Append(" icd-cpu=").Append(icdCpu.ToString("0.0", ci))
            .Append(" icd[tid=ms/s]:").Append(icdCount == 0 ? " none" : icdText.ToString());
        sb.Append(" top-threads:");
        for (var i = 0; i < Math.Min(5, threads.Count); i++)
        {
            sb.Append(' ').Append(threads[i].Tid.ToString(CultureInfo.InvariantCulture)).Append(":'")
                .Append(ThreadName(threads[i].Tid)).Append("'@").Append(threads[i].Module).Append('=')
                .Append(threads[i].Ms.ToString("0.0", ci));
        }
        GpuContext.Log(sb.ToString());

        return new GpuDiagWindow(mode, settle, mainFrames, procPct, cpuPerSec, secs, icdCount, icdCpu);
    }

    private static double Max(double[] v)
    {
        var m = v[0];
        foreach (var x in v)
        {
            m = Math.Max(m, x);
        }
        return m;
    }

    private readonly record struct ThreadRow(uint Tid, double Ms, string Module);

    private static bool IsIcd(string module)
    {
        var m = module.ToLowerInvariant();
        return m.Contains("icd", StringComparison.Ordinal) || m.StartsWith("nvoglv", StringComparison.Ordinal)
            || m.StartsWith("atio6", StringComparison.Ordinal) || m.StartsWith("atig6", StringComparison.Ordinal);
    }

    // Count of GL ICD threads right now, no CPU figures; logged around the extra-ctx steps.
    private static string IcdSnapshot()
    {
        var n = 0;
        foreach (var t in SampleThreads(new Dictionary<uint, long>(), 1))
        {
            if (IsIcd(t.Module))
            {
                n++;
            }
        }
        return $"icd-threads={n}";
    }

    // Every thread of this process, busiest first, with the file name of the module holding its start address.
    private static List<ThreadRow> SampleThreads(Dictionary<uint, long> prev, double secs)
    {
        var rows = new List<ThreadRow>();
        if (!OperatingSystem.IsWindows())
        {
            return rows;
        }
        var snap = GpuDiagNative.CreateToolhelp32Snapshot(GpuDiagNative.SnapThread, 0);
        if (snap == -1 || snap == 0)
        {
            return rows;
        }
        try
        {
            var pid = (uint)Environment.ProcessId;
            var e = new GpuDiagNative.ThreadEntry32 { Size = (uint)Marshal.SizeOf<GpuDiagNative.ThreadEntry32>() };
            for (var ok = GpuDiagNative.Thread32First(snap, ref e); ok != 0; ok = GpuDiagNative.Thread32Next(snap, ref e))
            {
                if (e.OwnerProcessId != pid)
                {
                    continue;
                }
                var h = GpuDiagNative.OpenThread(GpuDiagNative.ThreadQueryLimitedInformation, 0, e.ThreadId);
                if (h == 0)
                {
                    continue;
                }
                try
                {
                    var (cpu, _) = ReadThread(h);
                    GpuDiagNative.GetThreadTimes(h, out var created, out _, out _, out _);
                    prev.TryGetValue(e.ThreadId, out var before);
                    prev[e.ThreadId] = cpu;
                    rows.Add(new ThreadRow(e.ThreadId, (cpu - before) / 10_000.0 / secs, ThreadModule(e.ThreadId, created)));
                }
                finally
                {
                    GpuDiagNative.CloseHandle(h);
                }
            }
        }
        finally
        {
            GpuDiagNative.CloseHandle(snap);
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        return rows;
    }

    // File name of the module containing the thread's Win32 start address; cached, the address never changes.
    private static unsafe string ThreadModule(uint tid, long created)
    {
        lock (ModuleCache)
        {
            if (ModuleCache.TryGetValue(tid, out var cached) && cached.Created == created)
            {
                return cached.Name;
            }
        }
        var name = "?";
        var h = GpuDiagNative.OpenThread(GpuDiagNative.ThreadQueryLimitedInformation | GpuDiagNative.ThreadQueryInformation, 0, tid);
        if (h != 0)
        {
            try
            {
                if (GpuDiagNative.NtQueryInformationThread(h, GpuDiagNative.ThreadQuerySetWin32StartAddress,
                        out var start, nint.Size, out _) == 0 && start != 0
                    && GpuDiagNative.GetModuleHandleExW(GpuDiagNative.ModuleFromAddress | GpuDiagNative.ModuleUnchangedRefcount,
                        start, out var module) != 0)
                {
                    var buf = stackalloc char[260];
                    var len = GpuDiagNative.GetModuleFileNameW(module, buf, 260);
                    if (len > 0)
                    {
                        var full = new string(buf, 0, (int)len);
                        name = full.Substring(full.LastIndexOf('\\') + 1);
                    }
                }
            }
            catch (Exception)
            {
                name = "?";
            }
            finally
            {
                GpuDiagNative.CloseHandle(h);
            }
        }
        lock (ModuleCache)
        {
            ModuleCache[tid] = (created, name);
        }
        return name;
    }

    private static string ThreadName(uint tid)
    {
        var h = GpuDiagNative.OpenThread(GpuDiagNative.ThreadQueryLimitedInformation, 0, tid);
        if (h == 0)
        {
            return "";
        }
        try
        {
            if (GpuDiagNative.GetThreadDescription(h, out var p) < 0 || p == 0)
            {
                return "";
            }
            var s = Marshal.PtrToStringUni(p) ?? "";
            GpuDiagNative.LocalFree(p);
            return s;
        }
        catch (Exception)
        {
            return "";
        }
        finally
        {
            GpuDiagNative.CloseHandle(h);
        }
    }

    private static void Finish(string how, List<GpuDiagWindow> windows)
    {
        Restore();
        GpuContext.Log($"{Tag} {how}; render path is baseline");
        var ci = CultureInfo.InvariantCulture;
        var summary = GpuDiagSummary.Summarize(windows, PhaseCount);
        GpuContext.Log($"{Tag} summary: median over non-settle windows; proc in % of one core, icd-cpu in ms/s");
        foreach (var s in summary)
        {
            var sb = new StringBuilder();
            sb.Append(Tag).Append(" summary mode=").Append(ToName(s.Mode)).Append(" windows=").Append(s.Windows);
            if (s.Windows == 0)
            {
                sb.Append(" (no windows)");
            }
            else
            {
                sb.Append(" proc=").Append(s.MedianProcPctOfCore.ToString("0.0", ci)).Append("%core (")
                    .Append((s.MedianProcPctOfCore / Environment.ProcessorCount).ToString("0.00", ci)).Append("% TM)")
                    .Append(" icd-threads=").Append(s.MedianIcdThreads.ToString("0.#", ci))
                    .Append(" icd-cpu=").Append(s.MedianIcdCpuMsPerSec.ToString("0.0", ci))
                    .Append(" phase-ms/s:");
                for (var i = 0; i < PhaseCount; i++)
                {
                    if (s.MedianPhaseCpuMsPerSec[i] > 0.05)
                    {
                        sb.Append(' ').Append(PhaseNames[i]).Append('=').Append(s.MedianPhaseCpuMsPerSec[i].ToString("0.0", ci));
                    }
                }
            }
            GpuContext.Log(sb.ToString());
        }
    }
}

internal static partial class GpuDiagNative
{
    public const uint ThreadQueryLimitedInformation = 0x800;
    public const uint SnapThread = 0x4;
    public const uint ThreadQueryInformation = 0x40;
    public const int ThreadQuerySetWin32StartAddress = 9;
    public const uint ModuleFromAddress = 0x4;
    public const uint ModuleUnchangedRefcount = 0x2;

    private const nint CurrentThreadPseudoHandle = -2;

    [StructLayout(LayoutKind.Sequential)]
    public struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    public static long ThreadCycles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }
        return QueryThreadCycleTime(CurrentThreadPseudoHandle, out var c) != 0 ? (long)c : 0;
    }

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    public static partial nint OpenThread(uint access, int inherit, uint threadId);

    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial int GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    public static partial int QueryThreadCycleTime(nint thread, out ulong cycles);

    [LibraryImport("kernel32.dll")]
    public static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial int Thread32First(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll")]
    public static partial int Thread32Next(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll")]
    public static partial int GetThreadDescription(nint thread, out nint description);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationThread(nint thread, int infoClass, out nint info, int length, out uint returned);

    [LibraryImport("kernel32.dll")]
    public static partial int GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll")]
    public static unsafe partial uint GetModuleFileNameW(nint module, char* buffer, uint size);
}
