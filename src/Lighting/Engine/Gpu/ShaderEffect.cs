using System;
using System.Threading;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// An <see cref="IEffect"/> backed by a fragment shader. Compiles lazily on
/// first <see cref="RenderFrame"/>, renders a full-screen triangle strip to
/// the shared FBO, and glReadPixels into the CPU-side CanvasBuffer.
///
/// The shader is authored as if (0,0) is the top-left pixel - the readback
/// path flips rows on its way into the CanvasBuffer so downstream device
/// sampling keeps the same top-left convention.
///
/// GPU-only. If the shared <see cref="GpuContext"/> can't initialise or the
/// shader fails to compile, the canvas stays dark; there is no CPU fallback.
/// </summary>
public sealed partial class ShaderEffect : IEffect
{
    private const string VertexShaderSrc = """
        #version 330 core
        layout (location = 0) in vec2 a_pos;
        void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }
        """;

    /// <summary>Uniforms bound through this class's dedicated fields rather than
    /// ExtraParams. u_audioBoost is included because AudioBoost is never assigned
    /// by any caller today - it reaches GL through ExtraParams like any other
    /// per-effect param, so it must not also get a spec-default push.</summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> BaseParamNames =
        new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            "u_speed", "u_intensity", "u_hue", "u_colorize", "u_saturation", "u_contrast", "u_audioBoost",
        };

    private readonly GpuContext _ctx;
    private readonly string _fragSource;
    private readonly Action<GL, int, double>? _setUniforms;
    private System.Collections.Generic.Dictionary<string, ShaderParamSpec>? _specs;

    private uint _program;
    private readonly UniformLocations _u = new();
    private readonly System.Collections.Generic.Dictionary<string, int> _uniformCache = new();
    private bool _compiled;
    private bool _failed;
    // In-flight non-blocking compile, GL thread only. Null on the synchronous path and once the program is built.
    private readonly BuildHolder _build = new();
    private volatile bool _renderPending;
    private int _disposedFlag;
    private bool _disposed => Volatile.Read(ref _disposedFlag) != 0;
    private volatile bool _pendingBackground;
    // The effect this one replaced, drawn in its place until this program is built.
    private ShaderEffect? _predecessor;
    // Context generation the program and uniform locations belong to; a re-created
    // context invalidates every GL object, so they are rebuilt when it moves.
    private int _generation;
    private byte[] _readback = Array.Empty<byte>();

    // Cached Invoke-callback that reads per-frame args from instance fields.
    // Avoids allocating a fresh lambda (display class) every render frame.
    // RenderFrame is only called from the engine loop thread, so stashing
    // args on the instance is safe.
    private CanvasBuffer? _pendingCanvas;
    private ReadbackBatch? _pendingBatch;
    private double _pendingTick;
    private Action? _cachedRenderDelegate;
    private Func<StepState>? _cachedStepDelegate;

    internal GpuContext Context => _ctx;

    public string Name { get; }
    public float Speed { get; set; } = 1f;
    public float Intensity { get; set; } = 1f;
    public float Hue { get; set; } = 0f;
    public float Colorize { get; set; } = 0f;
    public float Saturation { get; set; } = 1f;
    public float Contrast { get; set; } = 1f;
    /// <summary>True once compiling or rendering failed; the canvas then keeps whatever the caller left.</summary>
    public bool HasFailed => _failed;
    /// <summary>This effect is standing on an outgoing one, which draws its frames until this program lands.</summary>
    internal bool HasPredecessor => _predecessor is not null;
    /// <summary>The canvas shows this effect, or the outgoing one it is standing in for; false while a first compile leaves it empty.</summary>
    internal bool CanPaint => !_failed && (!_renderPending || _predecessor is not null);
    internal int DisposeCount;
    internal void MarkCompiledForTest() => _compiled = true;
    /// <summary>Test seam: replaces the GL render. Returns true once the program has landed.</summary>
    internal Func<CanvasBuffer, double, bool>? TestRender;
    /// <summary>True while the last frame was skipped because the program is still compiling; the canvas then holds whatever the caller left.</summary>
    public bool IsRenderPending => _renderPending;
    private volatile bool _drewFrame;
    /// <summary>True only when the last RenderFrame wrote a finished frame into the canvas; false for every skipped, deferred, failed or dropped render.</summary>
    internal bool DrewFrame => _drewFrame;
    /// <summary>True once the compiled program reads a live audio uniform, so a frame depends on more than time.</summary>
    public bool ReadsAudio =>
        _u.AudioLevel >= 0 || _u.AudioBass >= 0 || _u.AudioMid >= 0 || _u.AudioHigh >= 0 || _u.AudioBeat >= 0
        || _u.Spectrum >= 0 || _u.Spectrum64 >= 0 || _u.SpecHist >= 0 || _u.BassPeak >= 0 || _u.MidPeak >= 0 || _u.HighPeak >= 0;

    /// <summary>0 = ignore audio, 1 = full reactive. Bound every frame to u_audioBoost.</summary>
    public float AudioBoost { get; set; } = 0f;
    /// <summary>Per-effect uniforms keyed by GLSL name (e.g. "u_zoom", "u_freq").</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, float>? ExtraParams { get; set; }

    public ShaderEffect(string name, GpuContext ctx, string fragSource,
        Action<GL, int, double>? setUniforms = null)
    {
        Name = name;
        _ctx = ctx;
        _fragSource = fragSource;
        _setUniforms = setUniforms;
    }

    public void RenderFrame(CanvasBuffer canvas, double tickMs)
    {
        _drewFrame = false;
        if (_generation != _ctx.Generation)
        {
            _generation = _ctx.Generation;
            _compiled = false;
            _failed = false;
            _program = 0;
            _build.Build = null;
            _build.Held = false;
            _renderPending = false;
            DropPredecessor();
            _uniformCache.Clear();
            ResetUniformLocations();
        }
        if (_failed)
        {
            // GPU unavailable / shader broken - canvas stays as the caller left it.
            return;
        }

        try
        {
            RenderFrameInternal(canvas, tickMs);
            if (_predecessor is { } outgoing)
            {
                if (_renderPending && !_failed)
                {
                    outgoing.RenderFrame(canvas, tickMs);
                }
                else
                {
                    DropPredecessor();
                }
            }
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu/{Name}] RenderFrame threw: {ex}");
            _failed = true;
            _renderPending = false;
        }
    }

    private void RenderFrameInternal(CanvasBuffer canvas, double tickMs)
    {
        if (TestRender is { } test)
        {
            _renderPending = !test(canvas, tickMs);
            _compiled = !_renderPending;
            _drewFrame = !_renderPending;
            return;
        }
        if (!_ctx.Available)
        {
            // Backstop for a context the warmup never started, gated so an init
            // the selection logic declined is not re-armed from the render path.
            // Skip the frame rather than latch: the context can still land.
            if (!_ctx.InitSuppressed)
            {
                lock (_ctx.Lock)
                {
                    _ctx.EnsureInitializedLocked();
                }
            }
            return;
        }

        _pendingCanvas = canvas;
        _pendingTick = tickMs;
        var batch = ReadbackBatch.Current;
        _pendingBatch = batch is not null && batch.Matches(_ctx) ? batch : null;
        _pendingBackground = GpuContext.InBackground;
        if (!_compiled && _ctx.UseAsyncCompile)
        {
            // Compiling in the driver's workers: a background caller is re-queued until the program lands, a live one skips the frame.
            _cachedStepDelegate ??= RenderPendingStep;
            _ctx.InvokeStep(_cachedStepDelegate);
            return;
        }
        _cachedRenderDelegate ??= RenderPending;
        _ctx.Invoke(_cachedRenderDelegate);
    }

    private void ResetUniformLocations() => _u.Reset();

    private void RenderPending() => RenderOnGlThread(_pendingCanvas!, _pendingTick);

    private StepState RenderPendingStep() => RenderOnGlThread(_pendingCanvas!, _pendingTick);

    // Parsed once per instance (GL thread only, no concurrent access) rather
    // than every frame; RenderFrame is only ever invoked from the engine loop.
    private System.Collections.Generic.Dictionary<string, ShaderParamSpec> Specs => _specs ??= ShaderParamSpec.Parse(_fragSource);

    private float ClampBase(string uniformName, float value) =>
        Specs.TryGetValue(uniformName, out var spec) ? ShaderParamSpec.Clamp(spec, value) : value;

    /// <summary>Done unless the frame was skipped because the program is still compiling or waiting for a compile slot.</summary>
    private StepState RenderOnGlThread(CanvasBuffer canvas, double tickMs)
    {
        {
            if (_disposed)
            {
                return StepState.Done;
            }
            var gl = _ctx.Gl;

            if (!_compiled && _ctx.UseAsyncCompile)
            {
                switch (AdvanceAsyncCompile(gl, _pendingBackground))
                {
                    case BuildPhase.Failed:
                        _failed = true;
                        _renderPending = false;
                        return StepState.Done;
                    case BuildPhase.Succeeded:
                        break;
                    case BuildPhase.Idle:
                        _renderPending = true;
                        return StepState.WaitSlot;
                    default:
                        _renderPending = true;
                        return StepState.Poll;
                }
            }
            _renderPending = false;
            if (!_compiled)
            {
                try
                {
                    var compileSw = System.Diagnostics.Stopwatch.StartNew();
                    CompileProgram(gl);
                    _compiled = true;
                    GpuContext.Log($"[gpu/{Name}] shader compiled, prog={_program}, {compileSw.Elapsed.TotalMilliseconds:0}ms");
                }
                catch (Exception ex)
                {
                    GpuContext.Log($"[gpu/{Name}] shader compile failed: {ex}");
                    _failed = true;
                    return StepState.Done;
                }
            }

            int w = _ctx.Width;
            int h = _ctx.Height;

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ctx.Fbo);
            gl.Viewport(0, 0, (uint)w, (uint)h);
            gl.UseProgram(_program);
            PushUniforms(gl, _u, _uniformCache, _program, w, h, tickMs);
            _setUniforms?.Invoke(gl, (int)_program, tickMs);

            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.DepthTest);
            // Defensive: drivers vary on whether culling stays disabled
            // across GL state batches; explicitly off per-draw so the
            // fullscreen strip never gets silently culled.
            gl.Disable(EnableCap.CullFace);
            gl.BindVertexArray(_ctx.QuadVao);
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            // ES only guarantees RGBA / UNSIGNED_BYTE for glReadPixels, so an ES
            // context reads four channels and the conversion drops alpha.
            bool es = _ctx.IsEs;
            if (_pendingBatch is { } batch && batch.Enqueue(gl, _ctx, w, h, es, canvas))
            {
                // The read is queued; the batch maps it together with its siblings and fills the canvas.
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                return StepState.Done;
            }
            int readBytes = w * h * (es ? 4 : 3);
            if (_readback.Length < readBytes)
            {
                _readback = new byte[readBytes];
            }
            unsafe
            {
                fixed (byte* p = _readback)
                {
                    gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                    gl.ReadPixels(0, 0, (uint)w, (uint)h, es ? PixelFormat.Rgba : PixelFormat.Rgb, PixelType.UnsignedByte, p);
                }
            }
            if (es && _ctx.CheckContextLost())
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                return StepState.Done;
            }

            // GL origin is bottom-left - flip rows into the top-left convention the
            // rest of the pipeline (and the old CPU effects) use.
            if (canvas.Width == w && canvas.Height == h)
            {
                canvas.WriteFromGl(_readback.AsSpan(0, readBytes), es);
                _drewFrame = true;
            }
            // Canvas resized outside what we rendered: drop this frame silently.

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return StepState.Done;
        }
    }

    private static double NowMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The build for a fragment source on an ES context: the original, plus the widened-literal variant as its retry.</summary>
    private static AsyncProgramBuild NewBuild(GL gl, string fragSource)
    {
        var fixedSrc = GlslDialect.FixIntLiteralsForEs(fragSource);
        var build = new AsyncProgramBuild(
            new GlProgramBuildOps(gl, GlslDialect.For(true, VertexShaderSrc)),
            GlslDialect.For(true, fragSource),
            fixedSrc != fragSource ? GlslDialect.For(true, fixedSrc) : null,
            NowMs);
        build.Start();
        return build;
    }

    // Start the compile on first use, poll it afterwards. A background caller waits for a free
    // slot first (reported as Idle, i.e. still pending). On success binds the program exactly as the synchronous path does.
    private static BuildPhase PollBuild(GpuContext ctx, BuildHolder holder, string fragSource, bool background)
    {
        if (holder.Build is null)
        {
            if (background && !ctx.TryAcquireCompileSlot())
            {
                return BuildPhase.Idle;
            }
            try
            { holder.Build = NewBuild(ctx.Gl, fragSource); }
            catch (Exception)
            {
                if (background)
                {
                    ctx.ReleaseCompileSlot();
                }
                throw;
            }
            holder.Held = background;
        }
        BuildPhase phase;
        try
        { phase = holder.Build.Poll(); }
        catch (Exception)
        {
            ReleaseHeld(ctx, holder);
            holder.Build = null;
            throw;
        }
        if (phase is BuildPhase.Succeeded or BuildPhase.Failed)
        {
            ReleaseHeld(ctx, holder);
        }
        return phase;
    }

    private static StepState StepOf(BuildPhase phase) => phase switch
    {
        BuildPhase.Succeeded or BuildPhase.Failed => StepState.Done,
        BuildPhase.Idle => StepState.WaitSlot,
        _ => StepState.Poll,
    };

    private static void ReleaseHeld(GpuContext ctx, BuildHolder holder)
    {
        if (holder.Held)
        {
            holder.Held = false;
            ctx.ReleaseCompileSlot();
        }
    }

    private BuildPhase AdvanceAsyncCompile(GL gl, bool background)
    {
        var phase = PollBuild(_ctx, _build, _fragSource, background);
        var build = _build.Build;
        if (phase == BuildPhase.Succeeded)
        {
            _program = build!.Program;
            LookupUniforms(gl, _program);
            _compiled = true;
            GpuContext.Log($"[gpu/{Name}] shader compiled, prog={_program}, submit to complete {build.LatencyMs:0.0}ms, {build.Polls} polls ({(background ? "background" : "live")}, parallel)");
            if (build.UsedFallback)
            {
                GpuContext.Log("[gpu] shader compiled for ES only after widening integer literals to floats");
            }
            _build.Build = null;
            if (!background)
            {
                _ctx.NoteLiveProgramLanded();
            }
        }
        else if (phase == BuildPhase.Failed)
        {
            GpuContext.Log($"[gpu/{Name}] shader compile failed: {build!.Error}");
            _build.Build = null;
        }
        return phase;
    }

    /// <summary>
    /// Take over from <paramref name="old"/>, the effect this one replaces: until this program is
    /// built, frames are drawn by the old one, so a switch to a shader that has to compile keeps the
    /// previous animation running. False when there is nothing to hand over; the caller then disposes old.
    /// </summary>
    internal bool AdoptPredecessor(ShaderEffect old)
    {
        // A disposed effect would never release a predecessor it took; the caller disposes the outgoing one.
        if (_disposed)
        {
            return false;
        }
        // A stand-in from another generation would compile again from cold itself.
        if (ReferenceEquals(old, this) || _compiled || _failed || !_ctx.UseAsyncCompile || !ReferenceEquals(old._ctx, _ctx)
            || old._generation != _ctx.Generation)
        {
            return false;
        }
        ShaderEffect? source = null;
        if (old._compiled && !old._failed)
        {
            source = old;
            old.DropPredecessor();
        }
        else if (old._predecessor is { } inherited)
        {
            // Rapid switching: keep drawing the last effect that actually had a program.
            old._predecessor = null;
            source = inherited;
            try
            { old.Dispose(); }
            catch { }
        }
        if (source is null)
        {
            return false;
        }
        // The adopted effect belongs to the live generation; this one has no GL objects yet, so it starts there too
        // rather than at zero, where its first frame would run the reset and drop the predecessor.
        _generation = _ctx.Generation;
        _predecessor = source;
        // Dispose sets its flag before it drops the predecessor, so a dispose that began meanwhile may have missed this one.
        if (_disposed)
        {
            DropPredecessor();
        }
        return true;
    }

    private void DropPredecessor()
    {
        var p = _predecessor;
        _predecessor = null;
        if (p is null)
        {
            return;
        }
        try
        { p.Dispose(); }
        catch { }
    }

    /// <summary>Submit a compile and link of this effect's source and wait for it without holding the GL thread, then free the program. Fills the driver's program cache.</summary>
    internal bool Prewarm()
    {
        if (!_ctx.Available)
        {
            return false;
        }
        var ok = false;
        using var background = GpuContext.BeginBackground();
        var holder = new BuildHolder();
        var generation = _ctx.Generation;
        _ctx.InvokeStep(() =>
        {
            if (_ctx.Generation != generation || !_ctx.Available)
            {
                return StepState.Done;
            }
            var gl = _ctx.Gl;
            if (!_ctx.UseAsyncCompile)
            {
                try
                {
                    var prog = BuildSync(gl, _ctx.IsEs, _fragSource);
                    gl.DeleteProgram(prog);
                    ok = true;
                }
                catch (InvalidOperationException ex) { GpuContext.Log($"[gpu/{Name}] prewarm compile failed: {ex.Message}"); }
                return StepState.Done;
            }
            var phase = PollBuild(_ctx, holder, _fragSource, true);
            if (phase == BuildPhase.Succeeded)
            {
                gl.DeleteProgram(holder.Build!.Program);
                ok = true;
                GpuContext.Log($"[gpu/{Name}] prewarm compile {holder.Build.LatencyMs:0.0}ms");
            }
            return StepOf(phase);
        });
        return ok;
    }

    private static uint BuildSync(GL gl, bool es, string fragSource)
    {
        uint vs = CompileShader(gl, ShaderType.VertexShader, GlslDialect.For(es, VertexShaderSrc));
        uint fs = CompileFragment(gl, es, fragSource);
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, GLEnum.LinkStatus, out int ok);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (ok == 0)
        {
            var log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            throw new InvalidOperationException($"link failed: {log}");
        }
        return prog;
    }

    /// <summary>
    /// Compile and link a fragment source on the GL thread without keeping the
    /// program. Returns the driver's info log on failure, null when it links or
    /// when no GPU context is available to check with. Sources the ES rules
    /// cannot run are refused on every platform, so Studio and the custom
    /// editor reject on a Mac what a Windows ANGLE context would not compile.
    /// </summary>
    public static string? ValidateFragment(GpuContext ctx, string fragSource)
    {
        var incompatible = GlslDialect.EsIncompatibility(fragSource);
        if (incompatible is not null)
        {
            return incompatible;
        }
        if (!ctx.Available)
        {
            return null;
        }
        string? error = null;
        using var background = GpuContext.BeginBackground();
        if (ctx.UseAsyncCompile)
        {
            var holder = new BuildHolder();
            ctx.InvokeStep(() =>
            {
                var phase = PollBuild(ctx, holder, fragSource, true);
                if (phase == BuildPhase.Succeeded)
                {
                    ctx.Gl.DeleteProgram(holder.Build!.Program);
                }
                else if (phase == BuildPhase.Failed)
                {
                    error = holder.Build!.Error ?? "shader failed to compile";
                }
                return StepOf(phase);
            });
            return error;
        }
        ctx.Invoke(() =>
        {
            var gl = ctx.Gl;
            error = BuildOnce(gl, ctx.IsEs, fragSource);
            if (error is not null && ctx.IsEs)
            {
                var fixedSrc = GlslDialect.FixIntLiteralsForEs(fragSource);
                if (fixedSrc != fragSource && BuildOnce(gl, true, fixedSrc) is null)
                {
                    error = null;
                }
            }
        });
        if (error is not null && string.IsNullOrWhiteSpace(error))
        {
            error = "shader failed to compile";
        }
        return error;
    }

    private static string? BuildOnce(GL gl, bool es, string fragSource)
    {
        string? error = null;
        uint vs = 0, fs = 0, prog = 0;
        try
        {
            vs = gl.CreateShader(ShaderType.VertexShader);
            gl.ShaderSource(vs, GlslDialect.For(es, VertexShaderSrc));
            gl.CompileShader(vs);
            gl.GetShader(vs, ShaderParameterName.CompileStatus, out int vsOk);
            if (vsOk == 0)
            {
                return gl.GetShaderInfoLog(vs);
            }
            fs = gl.CreateShader(ShaderType.FragmentShader);
            gl.ShaderSource(fs, GlslDialect.For(es, fragSource));
            gl.CompileShader(fs);
            gl.GetShader(fs, ShaderParameterName.CompileStatus, out int fsOk);
            if (fsOk == 0)
            {
                return gl.GetShaderInfoLog(fs);
            }
            prog = gl.CreateProgram();
            gl.AttachShader(prog, vs);
            gl.AttachShader(prog, fs);
            gl.LinkProgram(prog);
            gl.GetProgram(prog, GLEnum.LinkStatus, out int linkOk);
            if (linkOk == 0)
            {
                error = gl.GetProgramInfoLog(prog);
            }
        }
        finally
        {
            if (prog != 0)
            {
                gl.DeleteProgram(prog);
            }
            if (vs != 0)
            {
                gl.DeleteShader(vs);
            }
            if (fs != 0)
            {
                gl.DeleteShader(fs);
            }
        }
        return error;
    }

    /// <summary>
    /// Fragment compile for the render path. A source authored against desktop
    /// GLSL that ES rejects (an integer literal where a float is expected) gets
    /// one retry with those literals made floats, so effects saved before the
    /// ANGLE switch keep running; when the retry fails too the original error stands.
    /// </summary>
    private static uint CompileFragment(GL gl, bool es, string src)
    {
        InvalidOperationException first;
        try
        {
            return CompileShader(gl, ShaderType.FragmentShader, GlslDialect.For(es, src));
        }
        catch (InvalidOperationException ex) when (es)
        {
            first = ex;
        }
        var fixedSrc = GlslDialect.FixIntLiteralsForEs(src);
        if (fixedSrc == src)
        {
            throw first;
        }
        try
        {
            var fs = CompileShader(gl, ShaderType.FragmentShader, GlslDialect.For(true, fixedSrc));
            GpuContext.Log("[gpu] shader compiled for ES only after widening integer literals to floats");
            return fs;
        }
        catch (InvalidOperationException)
        {
            throw first;
        }
    }

    private void CompileProgram(GL gl)
    {
        uint vs = CompileShader(gl, ShaderType.VertexShader, GlslDialect.For(_ctx.IsEs, VertexShaderSrc));
        uint fs = CompileFragment(gl, _ctx.IsEs, _fragSource);
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, GLEnum.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            throw new InvalidOperationException($"link failed: {log}");
        }
        gl.DetachShader(prog, vs);
        gl.DetachShader(prog, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        _program = prog;
        LookupUniforms(gl, prog);
    }

    private void LookupUniforms(GL gl, uint prog) => _u.Lookup(gl, prog);

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
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }
        return s;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }
        Interlocked.Increment(ref DisposeCount);
        ReleaseLedUser();
        DropPredecessor();
        if (_ctx.Available && _generation == _ctx.Generation && (_program != 0 || _build.Build is not null))
        {
            try
            {
                // On the GL thread, so it is ordered against any frame step already queued (which sees _disposed and draws nothing).
                _ctx.Invoke(() =>
                {
                    if (_build.Build is { IsPending: true } pending)
                    {
                        pending.Abandon();
                    }
                    ReleaseHeld(_ctx, _build);
                    _build.Build = null;
                    if (_program != 0)
                    {
                        _ctx.Gl.DeleteProgram(_program);
                        _program = 0;
                    }
                });
            }
            catch { }
        }
    }
}
