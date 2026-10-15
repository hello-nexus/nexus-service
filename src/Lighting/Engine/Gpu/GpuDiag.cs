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
/// Throwaway diagnostic: per-phase CPU accounting for the shader render path and an
/// automatic A/B sequence of render variants. Starts on the first shader frame,
/// stops after the sequence, and always leaves the render path in baseline.
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
    private static int _unsupportedMask;
    private static bool _pboSupported;

    [ThreadStatic]
    private static int t_registeredKinds;

    [ThreadStatic]
    private static FrameContext t_context;

    public static bool Enabled => _enabled;

    public static GpuDiagMode Mode => (GpuDiagMode)Volatile.Read(ref _mode);

    /// <summary>The calling thread's frame context; only the engine loop's own main-canvas render sets Main.</summary>
    public static FrameContext CallerContext => t_context;

    public static bool PboSupported => _pboSupported;

    /// <summary>The variant a frame of this context renders with; only main-canvas frames leave baseline.</summary>
    public static GpuDiagMode ModeFor(FrameContext ctx)
    {
        if (!_enabled || ctx != FrameContext.Main)
        {
            return GpuDiagMode.Baseline;
        }
        var m = Mode;
        return (Volatile.Read(ref _unsupportedMask) & (1 << (int)m)) != 0 ? GpuDiagMode.Baseline : m;
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
    public static void OnGlFrame(GL gl, string? renderer)
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
            _pboSupported = major > 3 || (major == 3 && minor >= 2);
            GpuContext.Log($"{Tag} GL_VENDOR='{vendor}' GL_RENDERER='{renderer}' GL_VERSION='{version}' "
                + $"context={major}.{minor} fence+pbo={_pboSupported} logical-cpus={Environment.ProcessorCount}");
            if (!_pboSupported)
            {
                MarkUnsupported(GpuDiagMode.AsyncReadback, "context below 3.2");
                MarkUnsupported(GpuDiagMode.SleepWait, "context below 3.2");
            }
        }
        catch (Exception ex)
        {
            GpuContext.Log($"{Tag} GL query failed: {ex.Message}");
            return;
        }
        _enabled = true;
        var thread = new Thread(() => Run(renderer ?? "?")) { IsBackground = true, Name = "nexus-gpu-diag" };
        thread.Priority = ThreadPriority.BelowNormal;
        thread.Start();
    }

    public static void MarkUnsupported(GpuDiagMode mode, string reason)
    {
        int old;
        int next;
        do
        {
            old = Volatile.Read(ref _unsupportedMask);
            next = old | (1 << (int)mode);
        }
        while (Interlocked.CompareExchange(ref _unsupportedMask, next, old) != old);
        if (old != next)
        {
            GpuContext.Log($"{Tag} mode {ToName(mode)} unsupported ({reason}); runs as baseline");
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
        lock (StateLock)
        {
            if (!_aborted)
            {
                Volatile.Write(ref _mode, (int)mode);
            }
        }
    }

    public static string ToName(GpuDiagMode m) => m switch
    {
        GpuDiagMode.Baseline => "baseline",
        GpuDiagMode.AsyncReadback => "async-readback",
        GpuDiagMode.SleepWait => "sleep-wait",
        GpuDiagMode.BgraReadback => "bgra-readback",
        GpuDiagMode.NoReadback => "no-readback",
        GpuDiagMode.NoRender => "no-render",
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
            _enabled = false;
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
        _ = TopThreads(prevThreadCpu, 1);
        var lastProc = ProcessCpu();
        DrainCounters();
        var sw = Stopwatch.StartNew();
        var lastElapsed = TimeSpan.Zero;
        var window = 0;
        GpuContext.Log($"{Tag} started; sequence={(sequenceOn ? "on" : "off")}, "
            + $"warmup then {schedule.TotalWindows * WindowSeconds}s total, window={WindowSeconds}s");
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
                sequenceOn && (schedule.IsWarmup(window) || schedule.IsSettleWindow(window)));
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
        var phaseSum = new double[2];
        for (var i = 0; i < PhaseCount; i++)
        {
            var cyc = Interlocked.Exchange(ref Cycles[i], 0);
            var wall = Interlocked.Exchange(ref WallTicks[i], 0);
            counts[i] = Interlocked.Exchange(ref Counts[i], 0);
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
        var unsupported = (Volatile.Read(ref _unsupportedMask) & (1 << (int)mode)) != 0;

        var sb = new StringBuilder(512);
        var ci = CultureInfo.InvariantCulture;
        sb.Append(Tag).Append(" mode=").Append(ToName(mode)).Append(settle ? "(settle)" : "").Append(unsupported ? "(unsupported)" : "")
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
        sb.Append(" top-threads:").Append(TopThreads(prevThreadCpu, secs));
        GpuContext.Log(sb.ToString());

        return new GpuDiagWindow(mode, settle, unsupported ? 0 : mainFrames, procPct, cpuPerSec);
    }

    private static string TopThreads(Dictionary<uint, long> prev, double secs)
    {
        if (!OperatingSystem.IsWindows())
        {
            return " n/a";
        }
        var rows = new List<(uint Tid, double Ms)>();
        var snap = GpuDiagNative.CreateToolhelp32Snapshot(GpuDiagNative.SnapThread, 0);
        if (snap == -1 || snap == 0)
        {
            return " n/a";
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
                    prev.TryGetValue(e.ThreadId, out var before);
                    prev[e.ThreadId] = cpu;
                    rows.Add((e.ThreadId, (cpu - before) / 10_000.0 / secs));
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
        var sb = new StringBuilder();
        for (var i = 0; i < Math.Min(5, rows.Count); i++)
        {
            sb.Append(" ").Append(rows[i].Tid.ToString(CultureInfo.InvariantCulture)).Append(":'")
                .Append(ThreadName(rows[i].Tid)).Append("'=").Append(rows[i].Ms.ToString("0.0", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
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
        GpuContext.Log($"{Tag} {how}; render path is baseline");
        var ci = CultureInfo.InvariantCulture;
        var summary = GpuDiagSummary.Summarize(windows, PhaseCount);
        GpuContext.Log($"{Tag} summary: median over non-settle windows with frames; proc in % of one core, phases in cpu ms/s");
        foreach (var s in summary)
        {
            var sb = new StringBuilder();
            sb.Append(Tag).Append(" summary mode=").Append(ToName(s.Mode)).Append(" windows=").Append(s.Windows);
            if (s.Windows == 0)
            {
                sb.Append(" (unsupported or no frames)");
            }
            else
            {
                sb.Append(" proc=").Append(s.MedianProcPctOfCore.ToString("0.0", ci)).Append("%core (")
                    .Append((s.MedianProcPctOfCore / Environment.ProcessorCount).ToString("0.00", ci)).Append("% TM)");
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
}
