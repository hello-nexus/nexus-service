using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>The LED-window variant of one fragment source, shared by every effect instance with that source.</summary>
internal sealed class LedProgram
{
    public const int Building = 0;
    public const int Ready = 1;
    public const int Failed = 2;

    private int _state;
    public ManualResetEventSlim Done { get; } = new(false);
    public string Source { get; init; } = "";
    public int Generation { get; init; }
    public uint Handle;
    public UniformLocations U { get; } = new();
    public int TapLoc = -1;
    public int TimesLoc = -1;
    public Dictionary<string, int> Cache { get; } = new();
    public string? Error;
    public int State => Volatile.Read(ref _state);

    public void Finish(int state, string? error = null)
    {
        Error = error;
        Volatile.Write(ref _state, state);
        Done.Set();
    }
}

/// <summary>Per-context home of the LED-window programs, keyed by the effect's own fragment source.</summary>
internal sealed class LedWindowStore
{
    private static readonly ConditionalWeakTable<GpuContext, LedWindowStore> Stores = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, LedProgram> _programs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _users = new(StringComparer.Ordinal);

    public static LedWindowStore For(GpuContext ctx) => Stores.GetValue(ctx, static _ => new LedWindowStore());

    /// <summary>The program for the source, starting its background build the first time it is asked for. Null when the source cannot be rewritten.</summary>
    public LedProgram? Program(GpuContext ctx, string fragSource)
    {
        lock (_gate)
        {
            if (_programs.TryGetValue(fragSource, out var existing) && existing.Generation == ctx.Generation)
            {
                return existing;
            }
            var rewritten = LedWindowShader.Rewrite(fragSource);
            var lp = new LedProgram { Source = rewritten ?? "", Generation = ctx.Generation };
            _programs[fragSource] = lp;
            if (rewritten is null)
            {
                lp.Finish(LedProgram.Failed, "source cannot be rewritten");
                return lp;
            }
            Task.Run(() => ShaderEffect.BuildLedProgram(ctx, lp));
            return lp;
        }
    }

    /// <summary>One more effect instance keeps the source's program alive.</summary>
    public void AddUser(string fragSource)
    {
        lock (_gate)
        {
            _users[fragSource] = _users.GetValueOrDefault(fragSource) + 1;
        }
    }

    /// <summary>An effect instance is gone. With no instance left on the source, its program is deleted from the context.</summary>
    public void RemoveUser(GpuContext ctx, string fragSource)
    {
        LedProgram? gone = null;
        lock (_gate)
        {
            var left = _users.GetValueOrDefault(fragSource) - 1;
            if (left > 0)
            {
                _users[fragSource] = left;
                return;
            }
            _users.Remove(fragSource);
            if (_programs.Remove(fragSource, out var lp))
            {
                gone = lp;
            }
        }
        if (gone is null)
        {
            return;
        }
        var program = gone;
        Task.Run(() =>
        {
            try
            {
                if (!program.Done.Wait(TimeSpan.FromMinutes(3)) || program.Handle == 0 || program.Generation != ctx.Generation || !ctx.Available)
                {
                    return;
                }
                ctx.Invoke(() =>
                {
                    if (program.Generation == ctx.Generation)
                    {
                        ctx.Gl.DeleteProgram(program.Handle);
                    }
                });
            }
            catch (Exception)
            {
            }
        });
    }
}

public sealed partial class ShaderEffect
{
    /// <summary>Most pixel centers one LED-window draw evaluates.</summary>
    internal const int LedWindowMaxTaps = 8192;

    internal string FragSource => _fragSource;

    private int _ledUser;
    private sealed record GateKeyBox(int Generation, string Key);

    private volatile GateKeyBox? _ledGateKey;

    /// <summary>The LED program for this effect's source, counting this instance as a user so disposing the last one frees it.</summary>
    internal LedProgram? LedProgramForSource()
    {
        var store = LedWindowStore.For(_ctx);
        var lp = store.Program(_ctx, _fragSource);
        if (Interlocked.Exchange(ref _ledUser, 1) == 0)
        {
            store.AddUser(_fragSource);
            if (_disposed)
            {
                ReleaseLedUser();
            }
        }
        return lp;
    }

    internal void ReleaseLedUser()
    {
        if (Interlocked.Exchange(ref _ledUser, 0) == 1)
        {
            LedWindowStore.For(_ctx).RemoveUser(_ctx, _fragSource);
        }
    }

    /// <summary>The exactness-gate key for this source on the live context, computed once per context generation.</summary>
    internal string LedGateKey()
    {
        var generation = _ctx.Generation;
        if (_ledGateKey is { } cached && cached.Generation == generation)
        {
            return cached.Key;
        }
        var key = LedSpaceGate.Key(_fragSource, _ctx.Renderer, _ctx.GlVersion, _ctx.OwnsD3dDevice);
        _ledGateKey = new GateKeyBox(generation, key);
        return key;
    }

    /// <summary>Test seam: stands for a proven-exact LED window. Fills RGBA texels, frame-major, in the order the pixels are given.</summary>
    internal Func<int[], int[], double[], int, byte[], bool>? TestLedWindow;

    /// <summary>Compiled, healthy and drawing its own program: the state in which its canvas and its LED window agree on what is running.</summary>
    internal bool LedWindowCapable =>
        _compiled && !_failed && !_renderPending && !_disposed && _predecessor is null && _setUniforms is null
        && TestRender is null && !ReadsAudio && _generation == _ctx.Generation;

    internal static void BuildLedProgram(GpuContext ctx, LedProgram lp)
    {
        try
        {
            using var background = GpuContext.BeginBackground();
            var holder = new BuildHolder();
            var generation = lp.Generation;
            ctx.InvokeStep(() =>
            {
                if (ctx.Generation != generation || !ctx.Available)
                {
                    lp.Finish(LedProgram.Failed, "context changed");
                    return StepState.Done;
                }
                var gl = ctx.Gl;
                uint program;
                if (!ctx.UseAsyncCompile)
                {
                    try
                    {
                        program = BuildSync(gl, ctx.IsEs, lp.Source);
                    }
                    catch (InvalidOperationException ex)
                    {
                        lp.Finish(LedProgram.Failed, ex.Message);
                        return StepState.Done;
                    }
                }
                else
                {
                    var phase = PollBuild(ctx, holder, lp.Source, true);
                    if (phase == BuildPhase.Failed)
                    {
                        lp.Finish(LedProgram.Failed, holder.Build?.Error ?? "compile failed");
                        return StepState.Done;
                    }
                    if (phase != BuildPhase.Succeeded)
                    {
                        return StepOf(phase);
                    }
                    program = holder.Build!.Program;
                }
                lp.Handle = program;
                lp.U.Lookup(gl, program);
                lp.TapLoc = gl.GetUniformLocation(program, "u_ledTap");
                lp.TimesLoc = gl.GetUniformLocation(program, "u_ledT[0]");
                if (lp.TimesLoc < 0)
                {
                    lp.TimesLoc = gl.GetUniformLocation(program, "u_ledT");
                }
                lp.Finish(lp.TapLoc >= 0 && lp.TimesLoc >= 0 ? LedProgram.Ready : LedProgram.Failed, "tap or clock uniform optimized out");
                return StepState.Done;
            });
        }
        catch (Exception ex)
        {
            lp.Finish(LedProgram.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Evaluates this effect at the given canvas pixels for each of the given ticks in one draw and one readback.
    /// <paramref name="rgba"/> receives <c>frames * count</c> RGBA texels, frame-major, pixel order as given. False on
    /// anything unusual; nothing is partially written then. Does not consult the exactness verdict.
    /// </summary>
    internal bool TryRenderLedWindowUnchecked(ReadOnlySpan<int> px, ReadOnlySpan<int> py, double[] ticks, int frames, ref byte[] rgba)
    {
        var count = px.Length;
        if (count == 0 || count > LedWindowMaxTaps || py.Length != count || frames is < 1 or > LedWindowShader.MaxFrames || ticks.Length < frames)
        {
            return false;
        }
        if (!LedWindowCapable || !_ctx.Available)
        {
            return false;
        }
        var lp = LedProgramForSource();
        if (lp is null || lp.State != LedProgram.Ready || lp.Generation != _ctx.Generation)
        {
            return false;
        }
        var need = frames * count * 4;
        if (rgba.Length < need)
        {
            rgba = new byte[need];
        }
        var xs = px.ToArray();
        var ys = py.ToArray();
        var target = rgba;
        var ok = false;
        _ctx.Invoke(() => ok = DrawLedWindow(lp, xs, ys, ticks, frames, target));
        return ok;
    }

    private unsafe bool DrawLedWindow(LedProgram lp, int[] xs, int[] ys, double[] ticks, int frames, byte[] rgba)
    {
        if (_disposed || _generation != _ctx.Generation || lp.Generation != _ctx.Generation)
        {
            return false;
        }
        var gl = _ctx.Gl;
        var count = xs.Length;
        var h = _ctx.Height;
        var data = new float[count * 4];
        for (var i = 0; i < count; i++)
        {
            data[i * 4] = xs[i] + 0.5f;
            data[i * 4 + 1] = h - ys[i] - 0.5f;
            data[i * 4 + 3] = 1f;
        }
        var times = new float[LedWindowShader.MaxFrames];
        for (var f = 0; f < frames; f++)
        {
            times[f] = UTime(ticks[f]);
        }
        var tapTex = gl.GenTexture();
        var tex = gl.GenTexture();
        var fbo = gl.GenFramebuffer();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, tapTex);
            fixed (float* p = data)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)count, 1, 0, PixelFormat.Rgba, PixelType.Float, p);
            }
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

            gl.BindTexture(TextureTarget.Texture2D, tex);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)count, (uint)frames, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            {
                return false;
            }
            gl.Viewport(0, 0, (uint)count, (uint)frames);
            gl.UseProgram(lp.Handle);
            PushUniforms(gl, lp.U, lp.Cache, lp.Handle, _ctx.Width, _ctx.Height, ticks[0]);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, tapTex);
            gl.Uniform1(lp.TapLoc, 0);
            fixed (float* t = times)
            {
                gl.Uniform1(lp.TimesLoc, (uint)LedWindowShader.MaxFrames, t);
            }
            gl.Disable(EnableCap.Blend);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            gl.BindVertexArray(_ctx.QuadVao);
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            fixed (byte* r = rgba)
            {
                gl.ReadPixels(0, 0, (uint)count, (uint)frames, PixelFormat.Rgba, PixelType.UnsignedByte, r);
            }
            return !(_ctx.IsEs && _ctx.CheckContextLost());
        }
        finally
        {
            gl.BindTexture(TextureTarget.Texture2D, 0);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteTexture(tex);
            gl.DeleteTexture(tapTex);
        }
    }

    /// <summary>The window render for the engine: only when the exactness gate has proven this source byte-identical on this GPU.</summary>
    internal bool TryRenderLedWindow(ReadOnlySpan<int> px, ReadOnlySpan<int> py, double[] ticks, int frames, ref byte[] rgba)
    {
        if (TestLedWindow is { } seam)
        {
            if (rgba.Length < frames * px.Length * 4)
            {
                rgba = new byte[frames * px.Length * 4];
            }
            return seam(px.ToArray(), py.ToArray(), ticks, frames, rgba);
        }
        return LedSpaceGate.IsExact(this) && TryRenderLedWindowUnchecked(px, py, ticks, frames, ref rgba);
    }
}
