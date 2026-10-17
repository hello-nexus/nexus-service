#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Bench;

/// <summary>
/// Probe for evaluating an effect only at LED positions. P0 is the live path (full canvas draw, readback, flip, CPU sample),
/// P1 draws the LED taps one frame at a time, P2 draws a whole window in one draw and one readback.
/// </summary>
internal static partial class LightingBench
{
    private const int LsWindow = 30;
    private const int LsTrialMs = 150;
    private const int LsTrials = 3;
    private const int LsGpuReps = 9;
    private const int LsBatchSize = 8;
    private const string LsVertexSrc = "#version 330 core\nlayout (location = 0) in vec2 a_pos;\nvoid main() { gl_Position = vec4(a_pos, 0.0, 1.0); }\n";

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    private static ulong ProcessCycles()
    {
        QueryProcessCycleTime(GetCurrentProcess(), out var c);
        return c;
    }

    private interface ILsGl
    {
        GL Gl { get; }
        uint Quad { get; }
        int CanvasH { get; }
        void Invoke(Action work);
    }

    private sealed class LsGpuAdapter : ILsGl
    {
        private readonly GpuContext _ctx;

        public LsGpuAdapter(GpuContext ctx) => _ctx = ctx;

        public GL Gl => _ctx.Gl;
        public uint Quad => _ctx.QuadVao;
        public int CanvasH => _ctx.Height;

        public void Invoke(Action work) => _ctx.Invoke(work);
    }

    private sealed record LsPlacementDef(string Name, float X, float Y, float W, float H, int Rotation, bool Timed);

    private static readonly LsPlacementDef[] LsPlacementDefs =
    {
        new("wide", 280, 200, 440, 120, 0, true),
        new("small", 450, 260, 110, 30, 0, true),
        new("wide rotated", 280, 200, 440, 120, 33, false),
    };

    private sealed class LsPlacementState
    {
        public LsPlacementDef Def = null!;
        public DeviceFrame Device = null!;
        public LedTapLayout Layout = null!;
        public uint TapTex;
        public uint Fbo;
        public uint Tex;
        public int Width;
    }

    private sealed class LsProgram
    {
        public uint Handle;
        public int UTime = -1;
        public int UTap = -1;
        public int ULedT = -1;
    }

    private static uint LsCompile(GL gl, ShaderType type, string src)
    {
        var s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            var log = gl.GetShaderInfoLog(s);
            gl.DeleteShader(s);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }
        return s;
    }

    private static LsProgram LsBuild(GL gl, bool es, string frag, int w, int h, IReadOnlyDictionary<string, ShaderParamSpec> specs)
    {
        var vs = LsCompile(gl, ShaderType.VertexShader, GlslDialect.For(es, LsVertexSrc));
        uint fs;
        try
        {
            fs = LsCompile(gl, ShaderType.FragmentShader, GlslDialect.For(es, frag));
        }
        catch (InvalidOperationException) when (es)
        {
            var widened = GlslDialect.FixIntLiteralsForEs(frag);
            if (widened == frag)
            {
                gl.DeleteShader(vs);
                throw;
            }
            fs = LsCompile(gl, ShaderType.FragmentShader, GlslDialect.For(true, widened));
        }
        var prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, GLEnum.LinkStatus, out int linked);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (linked == 0)
        {
            var log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            throw new InvalidOperationException($"link failed: {log}");
        }
        gl.UseProgram(prog);
        var res = gl.GetUniformLocation(prog, "u_resolution");
        if (res >= 0)
        {
            gl.Uniform2(res, (float)w, (float)h);
        }
        foreach (var (n, v) in new[] { ("u_speed", 1f), ("u_intensity", 1f), ("u_hue", 0f), ("u_colorize", 0f), ("u_saturation", 1f), ("u_contrast", 1f), ("u_audioBoost", 0f) })
        {
            var loc = gl.GetUniformLocation(prog, n);
            if (loc >= 0)
            {
                gl.Uniform1(loc, specs.TryGetValue(n, out var sp) ? ShaderParamSpec.Clamp(sp, v) : v);
            }
        }
        foreach (var kv in specs)
        {
            if (!kv.Value.Declared || ShaderEffect.BaseParamNames.Contains(kv.Key))
            {
                continue;
            }
            var loc = gl.GetUniformLocation(prog, kv.Key);
            if (loc >= 0)
            {
                gl.Uniform1(loc, kv.Value.Default);
            }
        }
        var p = new LsProgram
        {
            Handle = prog,
            UTime = gl.GetUniformLocation(prog, "u_time"),
            UTap = gl.GetUniformLocation(prog, "u_ledTap"),
            ULedT = gl.GetUniformLocation(prog, "u_ledT[0]"),
        };
        if (p.ULedT < 0)
        {
            p.ULedT = gl.GetUniformLocation(prog, "u_ledT");
        }
        return p;
    }

    private static unsafe void LsCreatePlacementGl(ILsGl ctx, LsPlacementState s)
    {
        var layout = s.Layout;
        s.Width = layout.TotalTaps;
        ctx.Invoke(() =>
        {
            var gl = ctx.Gl;
            var data = new float[s.Width * 4];
            for (var i = 0; i < s.Width; i++)
            {
                data[i * 4] = layout.TapX[i] + 0.5f;
                data[i * 4 + 1] = ctx.CanvasH - layout.TapY[i] - 0.5f;
                data[i * 4 + 3] = 1f;
            }
            s.TapTex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, s.TapTex);
            fixed (float* p = data)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)s.Width, 1, 0, PixelFormat.Rgba, PixelType.Float, p);
            }
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

            s.Tex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, s.Tex);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)s.Width, LsWindow, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            s.Fbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, s.Fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, s.Tex, 0);
            var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            if (status != GLEnum.FramebufferComplete)
            {
                throw new InvalidOperationException($"led target incomplete: {status}");
            }
        });
    }

    private static void LsDestroyPlacementGl(ILsGl ctx, LsPlacementState s)
    {
        ctx.Invoke(() =>
        {
            var gl = ctx.Gl;
            gl.DeleteFramebuffer(s.Fbo);
            gl.DeleteTexture(s.Tex);
            gl.DeleteTexture(s.TapTex);
        });
    }

    /// <summary>Draws the LED taps of one placement for a number of frame rows and optionally reads them back; GL thread work behind one Invoke.</summary>
    private sealed class LsLedDraw
    {
        private readonly ILsGl _ctx;
        private readonly Action _run;
        public LsProgram Prog = null!;
        public LsPlacementState Place = null!;
        public readonly float[] Times = new float[LsWindow];
        public int Rows;
        public bool Read;
        public byte[] Pixels = Array.Empty<byte>();

        public LsLedDraw(ILsGl ctx)
        {
            _ctx = ctx;
            _run = Run;
        }

        public void Go(LsProgram prog, LsPlacementState place, int rows, bool read)
        {
            Prog = prog;
            Place = place;
            Rows = rows;
            Read = read;
            if (read && Pixels.Length < place.Width * rows * 4)
            {
                Pixels = new byte[place.Width * LsWindow * 4];
            }
            _ctx.Invoke(_run);
        }

        public void Run() => Draw(_ctx.Gl);

        public unsafe void Draw(GL gl)
        {
            LsRasterState(gl, _ctx.Quad);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, Place.Fbo);
            gl.Viewport(0, 0, (uint)Place.Width, (uint)Rows);
            gl.UseProgram(Prog.Handle);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, Place.TapTex);
            gl.Uniform1(Prog.UTap, 0);
            fixed (float* t = Times)
            {
                gl.Uniform1(Prog.ULedT, (uint)LsWindow, t);
            }
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            if (Read)
            {
                gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                fixed (byte* p = Pixels)
                {
                    gl.ReadPixels(0, 0, (uint)Place.Width, (uint)Rows, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
            }
            gl.BindTexture(TextureTarget.Texture2D, 0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private static void LsRasterState(GL gl, uint quad)
    {
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(quad);
    }

    /// <summary>GPU elapsed-time query through the timer-query entry points; the EXT names on an ES context.</summary>
    private sealed unsafe class LsGpuTimer
    {
        private const uint TimeElapsed = 0x88BF;
        private const uint QueryResult = 0x8866;
        private const uint QueryAvailable = 0x8867;
        private const int GpuDisjoint = 0x8FBB;
        private delegate* unmanaged[Cdecl]<int, uint*, void> _gen;
        private delegate* unmanaged[Cdecl]<uint, uint, void> _begin;
        private delegate* unmanaged[Cdecl]<uint, void> _end;
        private delegate* unmanaged[Cdecl]<uint, uint, ulong*, void> _get;
        private uint _query;
        public string Status { get; private set; } = "not initialised";
        public bool Available { get; private set; }

        public void Init(GL gl, bool es)
        {
            try
            {
                var ext = gl.GetStringS(StringName.Extensions) ?? "";
                if (es && !ext.Contains("GL_EXT_disjoint_timer_query", StringComparison.Ordinal))
                {
                    Status = "unavailable: GL_EXT_disjoint_timer_query not in the extension list";
                    return;
                }
                var sfx = es ? "EXT" : "";
                _gen = (delegate* unmanaged[Cdecl]<int, uint*, void>)Proc(gl, "glGenQueries" + sfx);
                _begin = (delegate* unmanaged[Cdecl]<uint, uint, void>)Proc(gl, "glBeginQuery" + sfx);
                _end = (delegate* unmanaged[Cdecl]<uint, void>)Proc(gl, "glEndQuery" + sfx);
                _get = (delegate* unmanaged[Cdecl]<uint, uint, ulong*, void>)Proc(gl, "glGetQueryObjectui64v" + sfx);
                uint q;
                _gen(1, &q);
                _query = q;
                Available = true;
                Status = es ? "GL_EXT_disjoint_timer_query" : "GL_TIME_ELAPSED (core)";
            }
            catch (Exception ex)
            {
                Status = $"unavailable: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static void* Proc(GL gl, string name)
        {
            if (!gl.Context.TryGetProcAddress(name, out var p) || p == 0)
            {
                throw new InvalidOperationException($"{name} not exported");
            }
            return (void*)p;
        }

        /// <summary>Elapsed GPU ms across the work, or null when the driver flagged the measurement disjoint or never answered.</summary>
        public double? Measure(GL gl, Action work)
        {
            gl.GetInteger((GetPName)GpuDisjoint, out _);
            _begin(TimeElapsed, _query);
            work();
            _end(TimeElapsed);
            var sw = Stopwatch.StartNew();
            ulong avail = 0;
            while (avail == 0 && sw.ElapsedMilliseconds < 2000)
            {
                _get(_query, QueryAvailable, &avail);
            }
            if (avail == 0)
            {
                return null;
            }
            ulong ns;
            _get(_query, QueryResult, &ns);
            gl.GetInteger((GetPName)GpuDisjoint, out var disjoint);
            return disjoint != 0 ? null : ns / 1e6;
        }
    }

    private sealed class LsCtx
    {
        public GpuContext Gpu = null!;
        public LightingEngine Engine = null!;
        public LsPlacementState[] Places = Array.Empty<LsPlacementState>();
        public long[] Ticks = new long[LsWindow];
        public float[] UTimes = new float[LsWindow];
        public LsGpuTimer Timer = new();
        public double CyclesPerMs;
        public LsLedDraw Draw = null!;
    }

    private static void RunLedSpace(BenchOptions o, GpuContext ctx, BenchComboResult result)
    {
        var (u, v) = LedSpaceCore.StrimerMap();
        var ls = new BenchLedSpaceCombo
        {
            LayoutSource = "Strimer Wireless 24-pin map (6 rows of 22), generated to match data/builtin-led-mappings.json to its stored precision; device rect is synthetic",
            Leds = u.Length,
            Window = LsWindow,
            CanvasSize = $"{ctx.Width}x{ctx.Height}",
            BatchReadback = ctx.BatchReadback,
        };
        result.LedSpace = ls;
        result.Status = "partial";
        using var engine = new LightingEngine();
        var lc = new LsCtx { Gpu = ctx, Engine = engine, CyclesPerMs = CalibrateCyclesPerMs(), Draw = new LsLedDraw(new LsGpuAdapter(ctx)) };
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < LsWindow; i++)
        {
            lc.Ticks[i] = start + (long)i * PeriodMs;
            lc.UTimes[i] = LedSpaceCore.UTime(lc.Ticks[i]);
        }
        var places = new List<LsPlacementState>();
        for (var i = 0; i < LsPlacementDefs.Length; i++)
        {
            var d = LsPlacementDefs[i];
            var dev = new DeviceFrame(0, "ls-strimer", u.Length, d.X, d.Y, d.W, d.H, d.Rotation) { LedU = u, LedV = v };
            var s = new LsPlacementState { Def = d, Device = dev, Layout = LedSpaceCore.BuildTaps(dev, ctx.Width, ctx.Height) };
            LsCreatePlacementGl(new LsGpuAdapter(ctx), s);
            places.Add(s);
            ls.PlacementNames.Add($"{d.Name} ({s.Layout.TotalTaps} taps)");
        }
        lc.Places = places.ToArray();
        ctx.Invoke(() => lc.Timer.Init(ctx.Gl, ctx.IsEs));
        ls.GpuTimer = lc.Timer.Status;
        Console.WriteLine($"gpu timer: {ls.GpuTimer}; batching {ls.BatchReadback}; placements {string.Join(", ", ls.PlacementNames)}");

        var names = new List<string>((IEnumerable<string>?)o.Effects ?? ShaderLibrary.AllEffectKeys);
        for (var i = 0; i < names.Count; i++)
        {
            Console.WriteLine($"ledspace {i + 1}/{names.Count} {names[i]}");
            BenchLsEffect e;
            try
            {
                e = RunLsEffect(names[i], lc);
            }
            catch (Exception ex)
            {
                e = new BenchLsEffect { Name = names[i], Class = "error", Detail = $"{ex.GetType().Name}: {ex.Message}" };
            }
            ls.Effects.Add(e);
            SaveChild(o.OutPath, result);
        }
        foreach (var p in places)
        {
            LsDestroyPlacementGl(new LsGpuAdapter(ctx), p);
        }
        result.Status = "ok";
    }

    private static double Median3(double[] v)
    {
        var a = (double[])v.Clone();
        Array.Sort(a);
        return a[a.Length / 2];
    }

    private static BenchLsPath LsMeasure(LsCtx lc, Action window)
    {
        window();
        window();
        var cpu = new double[LsTrials];
        var wall = new double[LsTrials];
        for (var t = 0; t < LsTrials; t++)
        {
            var c0 = ProcessCycles();
            var sw = Stopwatch.StartNew();
            var reps = 0;
            do
            {
                window();
                reps++;
            }
            while (sw.ElapsedMilliseconds < LsTrialMs || reps < 3);
            var ms = sw.Elapsed.TotalMilliseconds;
            var c1 = ProcessCycles();
            cpu[t] = (c1 - c0) / lc.CyclesPerMs / reps;
            wall[t] = ms / reps;
        }
        return new BenchLsPath { CpuMsPerWindow = Median3(cpu), WallMsPerWindow = Median3(wall) };
    }

    private static double? LsGpuMs(LsCtx lc, Action<GL> work)
    {
        if (!lc.Timer.Available)
        {
            return null;
        }
        var vals = new List<double>();
        for (var r = 0; r < LsGpuReps + 2; r++)
        {
            double? ms = null;
            lc.Gpu.Invoke(() => ms = lc.Timer.Measure(lc.Gpu.Gl, () => work(lc.Gpu.Gl)));
            if (ms is { } m && r >= 2)
            {
                vals.Add(m);
            }
        }
        if (vals.Count == 0)
        {
            return null;
        }
        vals.Sort();
        return vals[vals.Count / 2];
    }

    private static BenchLsEffect RunLsEffect(string name, LsCtx lc)
    {
        var ctx = lc.Gpu;
        var e = new BenchLsEffect { Name = name, Audio = ShaderLibrary.IsAudioEffect(name) };
        if (!ShaderLibrary.IsKnownKey(name))
        {
            e.Class = "error";
            e.Detail = "unknown effect key";
            return e;
        }
        var frag = ShaderLibrary.Get(name);
        e.Flags = LedSpaceCore.CanvasDependencyFlags(frag);
        var blocking = e.Flags.Any(static f => f is "derivative" or "texture" or "discard");
        using var fx = new ShaderEffect(name, ctx, frag);
        var scratch = new CanvasBuffer(ctx.Width, ctx.Height);
        fx.RenderFrame(scratch, lc.Ticks[0]);
        if (fx.HasFailed)
        {
            e.Class = "error";
            e.Detail = "full-canvas render failed";
            return e;
        }

        var refCanvas = new CanvasBuffer[LsWindow];
        for (var i = 0; i < LsWindow; i++)
        {
            refCanvas[i] = new CanvasBuffer(ctx.Width, ctx.Height);
            fx.RenderFrame(refCanvas[i], lc.Ticks[i]);
        }

        var specs = ShaderParamSpec.Parse(frag);
        LsProgram? orig = null;
        LsProgram? led = null;
        var rewritten = LedSpaceCore.Rewrite(frag, LsWindow);
        string? buildError = null;
        ctx.Invoke(() =>
        {
            var gl = ctx.Gl;
            try
            {
                orig = LsBuild(gl, ctx.IsEs, frag, ctx.Width, ctx.Height, specs);
            }
            catch (InvalidOperationException)
            {
                orig = null;
            }
            if (rewritten is null)
            {
                buildError = "source has no single main or lacks the declarations the rewrite hooks";
                return;
            }
            try
            {
                led = LsBuild(gl, ctx.IsEs, rewritten, ctx.Width, ctx.Height, specs);
            }
            catch (InvalidOperationException ex)
            {
                buildError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            }
        });

        try
        {
            if (led is null || blocking)
            {
                e.Class = "needs-canvas";
                e.Detail = led is null ? buildError : "source reads neighboring pixels or a texture";
            }
            else
            {
                e.Class = "pointwise";
            }
            LsTimeP0(lc, fx, e);
            if (led is null)
            {
                return e;
            }
            LsPlacements(lc, fx, e, led, orig, refCanvas, blocking);
        }
        finally
        {
            ctx.Invoke(() =>
            {
                if (orig is not null)
                {
                    ctx.Gl.DeleteProgram(orig.Handle);
                }
                if (led is not null)
                {
                    ctx.Gl.DeleteProgram(led.Handle);
                }
            });
        }
        return e;
    }

    private static void LsTimeP0(LsCtx lc, ShaderEffect fx, BenchLsEffect e)
    {
        var ctx = lc.Gpu;
        var canvas = new CanvasBuffer(ctx.Width, ctx.Height);
        var devs = new[] { lc.Places[0].Device };
        void P0()
        {
            for (var i = 0; i < LsWindow; i++)
            {
                fx.RenderFrame(canvas, lc.Ticks[i]);
                lc.Engine.SampleForBench(devs, canvas);
                devs[0].Publish();
            }
        }
        void P0b()
        {
            for (var at = 0; at < LsWindow; at += LsBatchSize)
            {
                var n = Math.Min(LsBatchSize, LsWindow - at);
                using var batch = ReadbackBatch.TryBegin(ctx);
                for (var k = 0; k < n; k++)
                {
                    batch?.Group = k;
                    fx.RenderFrame(canvas, lc.Ticks[at + k]);
                    if (batch is null)
                    {
                        lc.Engine.SampleForBench(devs, canvas);
                        devs[0].Publish();
                    }
                }
                if (batch is null)
                {
                    continue;
                }
                batch.Resolve();
                for (var k = 0; k < n; k++)
                {
                    batch.Apply(k);
                    lc.Engine.SampleForBench(devs, canvas);
                    devs[0].Publish();
                }
            }
        }
        e.P0 = LsMeasure(lc, P0);
        e.P0Batched = LsMeasure(lc, P0b);
    }

    private static void LsGatherTaps(byte[] rgba, int width, int row, byte[] tapRgb)
    {
        var o = row * width * 4;
        for (var x = 0; x < width; x++)
        {
            tapRgb[x * 3] = rgba[o];
            tapRgb[x * 3 + 1] = rgba[o + 1];
            tapRgb[x * 3 + 2] = rgba[o + 2];
            o += 4;
        }
    }

    private static BenchLsEquality LsCompare(
        LedTapLayout layout, byte[][] gotTaps, byte[][] wantTaps, byte[][] gotLeds, byte[][] wantLeds)
    {
        var q = new BenchLsEquality { Leds = LsWindow * layout.Leds, Taps = LsWindow * layout.TotalTaps };
        for (var f = 0; f < LsWindow; f++)
        {
            for (var t = 0; t < layout.TotalTaps; t++)
            {
                var d = 0;
                for (var c = 0; c < 3; c++)
                {
                    d = Math.Max(d, Math.Abs(gotTaps[f][t * 3 + c] - wantTaps[f][t * 3 + c]));
                }
                if (d == 0)
                {
                    q.ExactTaps++;
                }
                q.TapMaxChannelDelta = Math.Max(q.TapMaxChannelDelta, d);
            }
            for (var l = 0; l < layout.Leds; l++)
            {
                var d = 0;
                for (var c = 0; c < 3; c++)
                {
                    d = Math.Max(d, Math.Abs(gotLeds[f][l * 3 + c] - wantLeds[f][l * 3 + c]));
                }
                if (d == 0)
                {
                    q.ExactLeds++;
                }
                else if (q.FirstDiff is null)
                {
                    q.FirstDiff = $"first frame {f} led {l}: want {wantLeds[f][l * 3]},{wantLeds[f][l * 3 + 1]},{wantLeds[f][l * 3 + 2]} got {gotLeds[f][l * 3]},{gotLeds[f][l * 3 + 1]},{gotLeds[f][l * 3 + 2]}";
                }
                q.MaxChannelDelta = Math.Max(q.MaxChannelDelta, d);
            }
        }
        return q;
    }

    private static void LsPlacements(
        LsCtx lc, ShaderEffect fx, BenchLsEffect e, LsProgram led, LsProgram? orig, CanvasBuffer[] refCanvas, bool blocking)
    {
        var ctx = lc.Gpu;
        var draw = lc.Draw;
        for (var pi = 0; pi < lc.Places.Length; pi++)
        {
            var place = lc.Places[pi];
            var layout = place.Layout;
            var pr = new BenchLsPlacement { Name = lc.Places[pi].Def.Name, Taps = layout.TotalTaps };
            e.Placements.Add(pr);

            var wantTaps = new byte[LsWindow][];
            var wantLeds = new byte[LsWindow][];
            for (var f = 0; f < LsWindow; f++)
            {
                wantTaps[f] = new byte[layout.TotalTaps * 3];
                LedSpaceCore.TapsFromCanvas(layout, refCanvas[f], wantTaps[f]);
                lc.Engine.SampleForBench(new[] { place.Device }, refCanvas[f]);
                place.Device.Publish();
                wantLeds[f] = place.Device.LedBytes.ToArray();
            }

            var p1Taps = new byte[LsWindow][];
            var p1Leds = new byte[LsWindow][];
            var p2Taps = new byte[LsWindow][];
            var p2Leds = new byte[LsWindow][];
            for (var f = 0; f < LsWindow; f++)
            {
                p1Taps[f] = new byte[layout.TotalTaps * 3];
                p1Leds[f] = new byte[layout.Leds * 3];
                p2Taps[f] = new byte[layout.TotalTaps * 3];
                p2Leds[f] = new byte[layout.Leds * 3];
            }

            void P1Window(bool keep)
            {
                for (var f = 0; f < LsWindow; f++)
                {
                    draw.Times[0] = lc.UTimes[f];
                    draw.Go(led, place, 1, read: true);
                    var taps = keep ? p1Taps[f] : p2Taps[0];
                    LsGatherTaps(draw.Pixels, place.Width, 0, taps);
                    LedSpaceCore.Resolve(layout, taps, keep ? p1Leds[f] : p2Leds[0]);
                }
            }

            void P2Window(bool keep)
            {
                Array.Copy(lc.UTimes, draw.Times, LsWindow);
                draw.Go(led, place, LsWindow, read: true);
                for (var f = 0; f < LsWindow; f++)
                {
                    var taps = keep ? p2Taps[f] : p1Taps[0];
                    LsGatherTaps(draw.Pixels, place.Width, f, taps);
                    LedSpaceCore.Resolve(layout, taps, keep ? p2Leds[f] : p1Leds[0]);
                }
            }

            P1Window(true);
            P2Window(true);
            pr.EqP1 = LsCompare(layout, p1Taps, wantTaps, p1Leds, wantLeds);
            pr.EqP2 = LsCompare(layout, p2Taps, wantTaps, p2Leds, wantLeds);
            pr.P1P2Identical = true;
            for (var f = 0; f < LsWindow && pr.P1P2Identical; f++)
            {
                pr.P1P2Identical = p1Taps[f].AsSpan().SequenceEqual(p2Taps[f]);
            }

            if (!place.Def.Timed)
            {
                continue;
            }
            pr.P1 = LsMeasure(lc, () => P1Window(false));
            pr.P2 = LsMeasure(lc, () => P2Window(false));
            if (pi == 0)
            {
                e.P0!.GpuMsPerWindow = orig is null ? null : LsGpuMs(lc, gl =>
                {
                    LsRasterState(gl, ctx.QuadVao);
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, ctx.Fbo);
                    gl.Viewport(0, 0, (uint)ctx.Width, (uint)ctx.Height);
                    gl.UseProgram(orig.Handle);
                    for (var f = 0; f < LsWindow; f++)
                    {
                        gl.Uniform1(orig.UTime, lc.UTimes[f]);
                        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
                    }
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                });
            }
            pr.P1.GpuMsPerWindow = LsGpuMs(lc, gl =>
            {
                for (var f = 0; f < LsWindow; f++)
                {
                    draw.Times[0] = lc.UTimes[f];
                    draw.Prog = led;
                    draw.Place = place;
                    draw.Rows = 1;
                    draw.Read = false;
                    draw.Draw(gl);
                }
            });
            pr.P2.GpuMsPerWindow = LsGpuMs(lc, gl =>
            {
                Array.Copy(lc.UTimes, draw.Times, LsWindow);
                draw.Prog = led;
                draw.Place = place;
                draw.Rows = LsWindow;
                draw.Read = false;
                draw.Draw(gl);
            });
        }
    }
}
#endif
