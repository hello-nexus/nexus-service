using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nexus.Service.Lighting.Engine.Gpu;
using Silk.NET.OpenGL;

namespace Nexus.Service.Benchmarks.Kernels;

/// <summary>
/// Single-precision GPU throughput via a fragment shader of dependent FMA
/// chains, the portable stand-in for the bundled clpeak/vkpeak CLIs. Runs on
/// the shared <see cref="GpuContext"/> (its Linux EGL display and context are
/// static, so a second context is not an option) in short draws, so the
/// lighting engine's own frames interleave between them rather than stalling
/// for the whole measurement.
/// </summary>
internal static class GpuFlopsKernel
{
    private const int SurfaceSize = 1024;

    /// <summary>Per-loop-iteration cost: 8 fma statements x 4 lanes x (multiply + add).</summary>
    private const long FlopsPerIteration = 8 * 4 * 2;

    /// <summary>Per-draw target, bounding both submit overhead's share of the measurement and how long a lighting frame queued behind it waits.</summary>
    private static readonly TimeSpan DrawTarget = TimeSpan.FromMilliseconds(50);

    private const int DrawsPerTrial = 4;
    private const int TrialCount = 3;

    /// <summary>
    /// Trip-count ceiling. A trial also draws at twice this, and a draw that
    /// runs for seconds trips the GPU watchdog - which returns early rather
    /// than failing, so the timing silently becomes meaningless. Sized well
    /// above what calibration picks even on a fast card.
    /// </summary>
    private const int MaxIterations = 200_000;

    /// <summary>Trip-count floor. A software renderer is already past the per-draw target at the probe count, and clamping up to it would pin every draw at seconds and stall the lighting engine for the whole axis.</summary>
    private const int MinIterations = 1;

    /// <summary>Addend of the x = x*x + k recurrence. Below 0.25 so the fixed point stays attracting.</summary>
    private const float FixedPointK = 0.2f;

    /// <summary>Attempts the scale check gets before the axis fails, so transient load on the machine does not read as a folded loop.</summary>
    private const int ScaleCheckAttempts = 3;

    /// <summary>Minimum time ratio for a doubled trip count. Below a perfectly linear 2.0 to absorb per-draw submit overhead, far enough above 1.0 to catch a loop that was optimised away.</summary>
    private const double MinDoublingRatio = 1.6;

    private const string VertexSrc = """
        #version 330 core
        layout (location = 0) in vec2 a_pos;
        void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }
        """;

    // The FLOP total is fragments x trip count, so two properties are load
    // bearing and dropping either inflates the rate by orders of magnitude.
    // Seeded from gl_FragCoord: seeded only from uniforms the result is
    // fragment-invariant and the driver evaluates it once per draw, not once
    // per fragment. Cross-coupled: a = a * m + k is a linear recurrence whose
    // closed form Apple's compiler substitutes, leaving the loop free.
    // x = x*x + k has an attracting fixed point for k < 0.25, keeping the
    // accumulators off the denormal and infinity slow paths.
    private const string FragmentSrc = """
        #version 330 core
        out vec4 fragColor;
        uniform int u_iters;
        uniform vec4 u_k;
        uniform vec4 u_seed;
        void main() {
            vec4 jitter = vec4(
                gl_FragCoord.x,
                gl_FragCoord.y,
                gl_FragCoord.x + gl_FragCoord.y,
                gl_FragCoord.x * 0.5) * 1e-6;
            vec4 a = u_seed + jitter;
            vec4 b = u_seed - jitter;
            vec4 c = u_seed + jitter * 0.5;
            vec4 d = u_seed - jitter * 0.5;
            for (int i = 0; i < u_iters; ++i) {
                a = a * b + u_k;
                b = b * c + u_k;
                c = c * d + u_k;
                d = d * a + u_k;
                a = a * b + u_k;
                b = b * c + u_k;
                c = c * d + u_k;
                d = d * a + u_k;
            }
            fragColor = a + b + c + d;
        }
        """;

    internal readonly record struct GpuMeasurement(double[] TrialGflops, string Renderer);

    /// <summary>
    /// Measures GFLOPS on <paramref name="ctx"/>, or throws if the context is
    /// unavailable or the shader will not compile.
    /// </summary>
    internal static GpuMeasurement Measure(
        GpuContext ctx, Action<double>? onProgress, CancellationToken ct)
    {
        lock (ctx.Lock)
        {
            ctx.EnsureInitializedLocked();
        }
        if (!ctx.WaitForInit(ctx.InitTimeout, ct))
        {
            throw new InvalidOperationException("no GPU context available");
        }

        uint program = 0;
        uint fbo = 0;
        uint tex = 0;
        try
        {
            ctx.Invoke(() =>
            {
                var created = CreateResources(ctx.Gl, ctx.IsEs);
                program = created.Program;
                fbo = created.Fbo;
                tex = created.Tex;
            });

            int iterations = Calibrate(ctx, program, fbo, ct);
            var trials = new List<double>(TrialCount);
            for (int trial = 0; trial < TrialCount; trial++)
            {
                ct.ThrowIfCancellationRequested();
                double gflops = RunTrial(ctx, program, fbo, iterations, trial, ct);
                if (gflops > 0)
                {
                    trials.Add(gflops);
                }
                onProgress?.Invoke((double)(trial + 1) / TrialCount);
            }
            return new GpuMeasurement(trials.ToArray(), ctx.Renderer ?? "");
        }
        finally
        {
            try
            {
                ctx.Invoke(() =>
                {
                    var gl = ctx.Gl;
                    if (program != 0) { gl.DeleteProgram(program); }
                    if (fbo != 0) { gl.DeleteFramebuffer(fbo); }
                    if (tex != 0) { gl.DeleteTexture(tex); }
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                });
            }
            catch { }
        }
    }

    private readonly record struct GlResources(uint Program, uint Fbo, uint Tex);

    private static unsafe GlResources CreateResources(GL gl, bool es)
    {
        uint program = LinkProgram(gl, es);

        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            SurfaceSize, SurfaceSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, 0);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"benchmark FBO incomplete: {status}");
        }
        return new GlResources(program, fbo, tex);
    }

    private static uint LinkProgram(GL gl, bool es)
    {
        uint vs = CompileShader(gl, ShaderType.VertexShader, GlslDialect.For(es, VertexSrc));
        uint fs = CompileShader(gl, ShaderType.FragmentShader, GlslDialect.For(es, FragmentSrc));
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, GLEnum.LinkStatus, out int ok);
        gl.DetachShader(prog, vs);
        gl.DetachShader(prog, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (ok == 0)
        {
            string log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            throw new InvalidOperationException($"benchmark shader link failed: {log}");
        }
        return prog;
    }

    private static uint CompileShader(GL gl, ShaderType type, string src)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = gl.GetShaderInfoLog(s);
            gl.DeleteShader(s);
            throw new InvalidOperationException($"benchmark {type} compile failed: {log}");
        }
        return s;
    }

    /// <summary>
    /// Scales the loop trip count so one draw lands near <see cref="DrawTarget"/>,
    /// then proves the loop is real. The first draw also absorbs the driver's
    /// one-time shader compile, so it is measured and discarded before the ramp.
    /// </summary>
    private static int Calibrate(GpuContext ctx, uint program, uint fbo, CancellationToken ct)
    {
        const int probeIterations = 256;
        Draw(ctx, program, fbo, probeIterations, 0);

        double seconds = Draw(ctx, program, fbo, probeIterations, 1);
        if (seconds <= 0)
        {
            AssertLoopScales(ctx, program, fbo, probeIterations, ct);
            return probeIterations;
        }

        double scale = DrawTarget.TotalSeconds / seconds;
        long scaled = (long)(probeIterations * scale);
        int iterations = (int)Math.Clamp(scaled, MinIterations, MaxIterations);

        AssertLoopScales(ctx, program, fbo, iterations, ct);
        return iterations;
    }

    /// <summary>
    /// Fails the axis when doubling the trip count does not cost meaningfully
    /// more time. The FLOP total is derived from the trip count, so a driver
    /// that solved the recurrence in closed form - or hoisted it off the
    /// per-fragment path - would otherwise be reported as a GPU orders of
    /// magnitude faster than any that exists, rather than as the measurement
    /// failure it is. Retried because a machine busy with something else can
    /// distort a single pair of draws, and a real fold fails every attempt.
    /// </summary>
    private static void AssertLoopScales(
        GpuContext ctx, uint program, uint fbo, int iterations, CancellationToken ct)
    {
        double single = 0;
        double doubled = 0;
        for (int attempt = 0; attempt < ScaleCheckAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            single = Draw(ctx, program, fbo, iterations, 2 + attempt);
            doubled = Draw(ctx, program, fbo, iterations * 2, 100 + attempt);
            if (single > 0 && doubled >= single * MinDoublingRatio)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"shader loop did not scale with its trip count over "
            + $"{ScaleCheckAttempts} attempts (last: {single * 1000:F2}ms at {iterations} "
            + $"iterations, {doubled * 1000:F2}ms at {iterations * 2}); "
            + "the driver optimised it away, so no rate can be derived");
    }

    /// <summary>
    /// Rate from the slope between two trip counts rather than from one draw's
    /// absolute time: rasterising the surface and submitting the draw cost the
    /// same at N and 2N iterations, so the difference is the loop's own time and
    /// the fixed overhead cancels instead of biasing the result downward.
    /// Returns 0 when the extra iterations cost no measurable time.
    /// </summary>
    private static double RunTrial(
        GpuContext ctx, uint program, uint fbo, int iterations, int trial,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        double single = 0;
        double doubled = 0;
        for (int i = 0; i < DrawsPerTrial; i++)
        {
            ct.ThrowIfCancellationRequested();
            single += Draw(ctx, program, fbo, iterations, trial * DrawsPerTrial + i);
            doubled += Draw(ctx, program, fbo, iterations * 2, trial * DrawsPerTrial + i);
        }

        double loopSeconds = doubled - single;
        if (loopSeconds <= 0 || doubled < single * MinDoublingRatio)
        {
            return 0;
        }

        double flops = (double)SurfaceSize * SurfaceSize * iterations * FlopsPerIteration * DrawsPerTrial;
        return flops / loopSeconds / 1e9;
    }

    /// <summary>Draws one full-surface pass and returns the seconds it took to complete on the GPU.</summary>
    private static double Draw(GpuContext ctx, uint program, uint fbo, int iterations, int seed)
    {
        double seconds = 0;
        ctx.Invoke(() =>
        {
            var gl = ctx.Gl;
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.Viewport(0, 0, SurfaceSize, SurfaceSize);
            gl.UseProgram(program);
            int uIters = gl.GetUniformLocation(program, "u_iters");
            int uSeed = gl.GetUniformLocation(program, "u_seed");
            int uK = gl.GetUniformLocation(program, "u_k");
            if (uIters >= 0) { gl.Uniform1(uIters, iterations); }
            // A per-draw seed stops a driver from reusing the previous draw's
            // result for an identical invocation. Kept inside the attracting
            // basin of the recurrence's fixed point.
            if (uSeed >= 0)
            {
                float s = 0.4f + seed * 0.0001f;
                gl.Uniform4(uSeed, s, s, s, s);
            }
            if (uK >= 0) { gl.Uniform4(uK, FixedPointK, FixedPointK, FixedPointK, FixedPointK); }
            gl.BindVertexArray(ctx.QuadVao);

            // Submit and drain once so the timed section contains only this
            // draw, then time the draw with a Finish - GL is asynchronous, so
            // without it this would measure queue submission, not execution.
            gl.Finish();
            var sw = Stopwatch.StartNew();
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            gl.Finish();
            sw.Stop();
            seconds = sw.Elapsed.TotalSeconds;

            // The lighting engine shares this context; hand it back the state
            // it had rather than the benchmark's surface size and program.
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Viewport(0, 0, (uint)ctx.Width, (uint)ctx.Height);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        });
        return seconds;
    }
}
