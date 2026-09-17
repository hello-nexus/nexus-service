using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Platform;
using Silk.NET.GLFW;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Owns a single shared offscreen OpenGL 3.3 core context used by every shader
/// effect. Thread-safety: all GL work runs on the dedicated <c>_glThread</c>;
/// callers off that thread go through <see cref="Invoke"/>, which marshals the
/// work onto it and blocks until completion. <see cref="Lock"/> only guards
/// lazy initialization.
///
/// If init fails the context is marked unavailable and shader effects do not
/// render at all - there is no CPU fallback.
/// </summary>
public sealed class GpuContext : IDisposable
{
    private readonly object _lock = new();
    private readonly int _width;
    private readonly int _height;
#if MACOS
    // macOS path: CGL context pointer, no window.
    private IntPtr _cglCtx;
#elif LINUX
    // Linux path: headless EGL device context, no window (works under the root daemon).
    private bool _eglUsed;
#else
    // Windows path: WGL owns a hidden window and its context (WinWglContext).
    private WglHandles _wgl;
    // Same, for the GLFW fallback backend. Held as IntPtr so the field needs no
    // unsafe context; cast back at the call sites.
    private IntPtr _glfwWindow;
#endif
    private GL? _gl;
    private uint _fbo;
    private uint _fboTex;
    private uint _quadVao;
    private uint _quadVbo;
    private volatile bool _initStarted;
    private volatile bool _ready;
    private volatile bool _failed;
    private volatile bool _abandoned;
    private volatile bool _initSuppressed;
    private int _retries;
    private volatile bool _disposed;

    // Dedicated GL thread. The engine loop is a Task that hops thread-pool
    // threads between awaits, but GLFW/wgl contexts are sticky to the
    // thread that created them, so every GL call must marshal to this one
    // thread via Invoke().
    private Thread? _glThread;
    private readonly BlockingCollection<Action> _workQueue = new(new ConcurrentQueue<Action>());
    private readonly ManualResetEventSlim _initDone = new(false);
    // Set only when an attempt actually produced a context. _initDone is also
    // set by a failure or an abandon, so a late-landing watcher needs its own
    // handle or it returns the moment the attempt is written off.
    private readonly ManualResetEventSlim _landed = new(false);
    private Exception? _initError;

    // How long a caller BLOCKS waiting for the context. Not a verdict on the
    // card: the thread keeps going and Available flips on its own if it lands
    // late. Sized off a cold-boot AMD iGPU measured at 28.4s.
    public TimeSpan InitTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Backend to use in place of the stored one. Set by the probe
    /// child, which runs with no config store.</summary>
    public string? BackendOverride { get; init; }

    // GL_RENDERER of the bound context (which physical card was selected), set
    // once init succeeds. Null until then.
    public string? Renderer { get; private set; }


    // Per-thread reusable completion handle used by Invoke(). Invoke blocks the
    // caller until its work runs, so at most one outstanding per thread -- safe
    // to reuse without pooling. Avoids allocating a fresh MRE (~60 bytes +
    // internal Lazy<ManualResetEvent>) on every render frame.
    private readonly ThreadLocal<ManualResetEventSlim> _invokeDone =
        new(() => new ManualResetEventSlim(false), trackAllValues: true);

    // Optional: lets the Linux EGL path read the saved render-GPU choice. Null
    // in contexts that don't need it (e.g. tests).
    private readonly Nexus.Service.Persistence.IConfigStore? _store;

    // Substituted by tests: a real GL init is host-dependent, and concurrent
    // glfwInit calls in a parallelized suite are undefined.
    private readonly Action _initAction;

    public GpuContext(int width, int height, Nexus.Service.Persistence.IConfigStore? store = null)
        : this(width, height, store, null) { }

    internal GpuContext(int width, int height, Nexus.Service.Persistence.IConfigStore? store, Action? initAction)
    {
        _width = width;
        _height = height;
        _store = store;
        _initAction = initAction ?? InitInternal;
    }

    public object Lock => _lock;
    public int Width => _width;
    public int Height => _height;
    public uint Fbo => _fbo;
    public uint QuadVao => _quadVao;
    public GL Gl => _gl ?? throw new InvalidOperationException("GpuContext not initialized");
    public bool Available => _ready && !_failed && !_disposed;

    /// <summary>An init attempt has run and thrown. Distinct from "not ready
    /// yet": only this is terminal for the card.</summary>
    public bool Failed => _failed;

    /// <summary>An attempt is running and has neither succeeded nor thrown.</summary>
    public bool Initializing => _initStarted && !_ready && !_failed && !_disposed;

    /// <summary>The failed attempt never returned, so its thread is still parked
    /// in native GLFW. No second init may run beside it.</summary>
    public bool InitAbandoned => _abandoned;

    /// <summary>Auto-init is off until a re-probe rearms it.</summary>
    public bool InitSuppressed => _initSuppressed;

    public bool IsDisposed => _disposed;

    /// <summary>Lift a decline so a scheduled re-probe can attempt the card
    /// again, with a fresh retry budget. Refused after an abandon: that thread is
    /// still parked in native GLFW.</summary>
    public bool RearmAfterLatch()
    {
        lock (_lock)
        {
            if (_disposed || _abandoned || _ready)
            {
                return false;
            }
            _initSuppressed = false;
            _initError = null;
            _failed = false;
            _ready = false;
            _retries = 0;
            _initDone.Reset();
            _landed.Reset();
            _initStarted = false;
            _gl = null;
            Renderer = null;
            _fbo = 0;
            _fboTex = 0;
            _quadVao = 0;
            _quadVbo = 0;
#if MACOS
            _cglCtx = IntPtr.Zero;
#elif LINUX
            _eglUsed = false;
#else
            _glfwWindow = IntPtr.Zero;
            _wgl = default;
#endif
            return true;
        }
    }

    /// <summary>Decline the card for the rest of the process: no attempt starts,
    /// and the context reports Failed rather than Initializing. Both halves are
    /// load-bearing. Without the suppression the render path re-arms the init the
    /// selection just declined; without the Failed flag /lighting/status reports
    /// "initializing" forever, which also withholds the render-GPU shortcut the
    /// UI gates on a failed card.</summary>
    public void DeclineInit(string reason)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _initSuppressed = true;
            if (_ready)
            {
                return;
            }
            _initError ??= new InvalidOperationException(reason);
            _failed = true;
            _initDone.Set();
        }
    }

    // Co-located with nexus-service.log; ServiceLog.LogsDirectory resolves the
    // writable per-OS logs dir (HOME-based on Linux, so the old /usr/share
    // read-only concern doesn't apply).
    private static readonly string LogPath = System.IO.Path.Combine(ServiceLog.LogsDirectory, "nexus-gpu.log");

    public static void Log(string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            System.IO.File.AppendAllText(LogPath, $"{DateTime.UtcNow:HH:mm:ss.fff} {line}\n");
        }
        catch { }
        // These are GL init/shader traces, not failures - INF, not ERR. Full
        // detail still lands in the dedicated nexus-gpu.log above.
        ServiceLog.Info(line);
    }

    /// <summary>
    /// Start GL init on the dedicated nexus-gl thread. Returns immediately; a
    /// caller that needs the context ready follows with <see cref="WaitForInit"/>
    /// OUTSIDE the lock. Must hold <see cref="Lock"/>.
    /// </summary>
    public void EnsureInitializedLocked()
    {
        if (_initStarted || _disposed || _initSuppressed)
        {
            return;
        }

        _initStarted = true;
        _glThread = new Thread(GlThreadMain) { IsBackground = true, Name = "nexus-gl" };
        _glThread.Start();
    }

    /// <summary>Block until the attempt finishes or <paramref name="timeout"/>
    /// elapses; the thread keeps running past it and <see cref="Available"/>
    /// flips on its own. Call WITHOUT holding <see cref="Lock"/>, which the
    /// render path takes every frame.</summary>
    public bool WaitForInit(TimeSpan timeout) => WaitForInit(timeout, CancellationToken.None);

    /// <inheritdoc cref="WaitForInit(TimeSpan)"/>
    public bool WaitForInit(TimeSpan timeout, CancellationToken ct)
    {
        if (!_initDone.Wait(timeout, ct))
        {
            Log($"[gpu] context init still running after {timeout.TotalSeconds:0.#}s; "
                + "shader effects stay dark until it lands");
            return false;
        }
        if (_initError is not null)
        {
            Log($"[gpu] context init failed: {_initError.Message}");
        }
        return Available;
    }

    /// <summary>Block until an attempt produces a context, or
    /// <paramref name="timeout"/> elapses. Unlike <see cref="WaitForInit"/> this
    /// keeps waiting after the attempt has been written off, which is the only
    /// way an abandoned-but-still-running init can be recovered.</summary>
    public bool WaitForLateLanding(TimeSpan timeout) => _landed.Wait(timeout) && Available;

    /// <summary>Abandon a FAILED attempt so another can run, up to
    /// <see cref="GpuInitRetry.MaxAttempts"/> per process. Only a TERMINATED
    /// attempt qualifies: glfwInit/glfwCreateWindow share process-global state,
    /// so a second init beside a still-running or abandoned one is
    /// undefined.</summary>
    public bool ResetForRetry()
    {
        lock (_lock)
        {
            if (_disposed || _initSuppressed || _abandoned || !_failed
                || _retries >= GpuInitRetry.MaxAttempts)
            {
                return false;
            }
            _retries++;
            _initError = null;
            _failed = false;
            _ready = false;
            _initDone.Reset();
            _landed.Reset();
            _initStarted = false;
            _gl = null;
            Renderer = null;
            _fbo = 0;
            _fboTex = 0;
            _quadVao = 0;
            _quadVbo = 0;
#if MACOS
            _cglCtx = IntPtr.Zero;
#elif LINUX
            _eglUsed = false;
#else
            // WGL is already freed: the GL thread frees it on its way out, which
            // is before an attempt can be reset. GLFW's window is deliberately
            // not destroyed - that belongs to the thread that created it, and
            // that thread has exited. One leaked hidden window per process is
            // harmless; reaching across threads to free it is not.
            _glfwWindow = IntPtr.Zero;
            _wgl = default;
#endif
            // Deliberately does NOT start the next attempt: the caller sets the
            // OS GPU preference (read at context-creation time) first.
            return true;
        }
    }

    /// <summary>Mark an attempt that never returned as failed; its thread stays
    /// parked in native init and dies with the process.</summary>
    public void AbandonInit()
    {
        lock (_lock)
        {
            if (_disposed || _ready || _failed || !_initStarted)
            {
                return;
            }
            _initError = new TimeoutException("GL context init never returned");
            _abandoned = true;
            _failed = true;
            _initDone.Set();
        }
    }

    private void GlThreadMain()
    {
        try
        {
            RunGlThread();
        }
        finally
        {
            // Every exit lands here: a throwing init that already created the
            // context, the completed work queue, and Dispose. Nothing else may
            // free these - the window is this thread's.
            ReleaseThreadOwnedContext();
        }
    }

    private void RunGlThread()
    {
        try
        { _initAction(); }
        catch (Exception ex)
        {
            // Published under the lock so ResetForRetry cannot land between the
            // flag write and the Set().
            lock (_lock)
            {
                _initError = ex;
                _failed = true;
                _initDone.Set();
            }
            return;
        }
        lock (_lock)
        {
            // An abandoned attempt that lands anyway is a working context: the
            // abandon only stopped callers waiting on it. ResetForRetry refuses
            // while _abandoned, so no second attempt can be racing this one.
            _abandoned = false;
            _failed = false;
            _initError = null;
            _ready = true;
            _landed.Set();
            _initDone.Set();
        }

        while (!_disposed)
        {
            Action work;
            try
            { work = _workQueue.Take(); }
            catch (InvalidOperationException) { return; }
            try
            { work(); }
            catch (Exception ex) { Log($"[gpu] work item threw: {ex}"); }
        }
    }

    /// <summary>
    /// Free what only this thread may free. Win32 refuses a DestroyWindow from
    /// any thread but the window's creator, and the context is current here, so
    /// the WGL teardown rides the GL thread's exit rather than Dispose. The CGL
    /// and EGL paths are torn down in Dispose, after the join.
    /// </summary>
    private void ReleaseThreadOwnedContext()
    {
#if !MACOS && !LINUX
        WglHandles handles;
        // Same lock the rearm paths write the field under.
        lock (_lock)
        {
            handles = _wgl;
            _wgl = default;
        }
        if (handles.Created)
        {
            WinWglContext.Destroy(handles);
        }
#endif
    }

    /// <summary>
    /// Run <paramref name="work"/> on the dedicated GL thread and block the
    /// caller until it returns. The caller does not need to hold <see cref="Lock"/>.
    /// </summary>
    public void Invoke(Action work)
    {
        if (!Available || _glThread is null)
        {
            return;
        }
        if (Thread.CurrentThread == _glThread)
        {
            work();
            return;
        }
        Exception? caught = null;
        var done = _invokeDone.Value!;
        done.Reset();
        _workQueue.Add(() =>
        {
            try
            { work(); }
            catch (Exception ex) { caught = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (caught is not null)
        {
            throw caught;
        }
    }

    private void InitInternal()
    {
        ApplyForcedFailure();
#if MACOS
        // macOS: skip GLFW / NSWindow entirely and create a headless GL 4.1
        // core context via CGL. Works from any thread, no AppKit needed.
        Log("[gpu] macOS: CGL create core context");
        _cglCtx = MacGlContext.CreateAndMakeCurrent();
        _gl = GL.GetApi(new CglNativeContext());
#elif LINUX
        // Linux: headless EGL on the GPU device platform - no X/Wayland, no
        // window. GLFW needs a display and crashes creating an nvidia GL
        // context as root on the user's XWayland, so the root daemon can't
        // use it; EGL device-platform is windowless like macOS's CGL.
        Log("[gpu] Linux: EGL device-platform headless context");
        LinuxEglContext.CreateAndMakeCurrent(_store?.Load().Lighting.RenderGpu ?? "auto");
        _eglUsed = true;
        _gl = GL.GetApi(new EglNativeContext());
#else
        // Windows: WGL directly (WinWglContext). D3D11CreateDevice screens out a
        // ghost adapter but does not prove WGL works; the GL verdict is the
        // context creation itself.
        var preCheckSw = System.Diagnostics.Stopwatch.StartNew();
        if (!Nexus.Service.Sensors.GpuAdapterLuids.HasUsableHardwareGpu())
        {
            throw new InvalidOperationException("no usable GPU adapter present");
        }
        var preCheckMs = preCheckSw.ElapsedMilliseconds;
        // An impossible version is how the dev seam makes creation fail.
        var (major, minor) = GpuTestSeam.Mode == GpuForceMode.GlfwError ? (9, 9) : (3, 3);
        var glSw = System.Diagnostics.Stopwatch.StartNew();
        if (UseGlfwBackend())
        {
            InitViaGlfw(major, minor);
            Log($"[gpu] context init: adapter pre-check {preCheckMs}ms, GLFW backend {glSw.ElapsedMilliseconds}ms");
        }
        else
        {
            _wgl = WinWglContext.CreateAndMakeCurrent(_width, _height, major, minor);
            _gl = GL.GetApi(new WglNativeContext());
            Log($"[gpu] context init: adapter pre-check {preCheckMs}ms, "
                + $"WGL {major}.{minor} core {glSw.ElapsedMilliseconds}ms");
        }
#endif
        Log("[gpu] GL ready");
        try
        {
            // Which physical card the context bound to - the proof the
            // GpuPreference class steered selection, and a diagnostic on a
            // customer capture.
            Renderer = _gl!.GetStringS(StringName.Renderer);
            Log($"[gpu] GL context on renderer='{Renderer}' "
                + $"vendor='{_gl.GetStringS(StringName.Vendor)}' "
                + $"version='{_gl.GetStringS(StringName.Version)}'");
        }
        catch (Exception ex) { Log($"[gpu] GL renderer query failed: {ex.Message}"); }

        _fboTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fboTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8,
                (uint)_width, (uint)_height, 0, PixelFormat.Rgb, PixelType.UnsignedByte, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _fboTex, 0);
        // Deterministic flat-shading across drivers (some Mesa/Intel paths
        // default to FirstVertexConvention).
        try
        { _gl.ProvokingVertex(VertexProvokingMode.LastVertexConvention); }
        catch { }
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"FBO incomplete: {status}");
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Fullscreen triangle strip (covers the whole clip-space quad)
        _quadVao = _gl.GenVertexArray();
        _quadVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_quadVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        Span<float> quad = stackalloc float[] { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
        unsafe
        {
            fixed (float* p = quad)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            }
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (uint)(2 * sizeof(float)), (void*)0);
        }
        _gl.BindVertexArray(0);

        Log($"[gpu] OpenGL context ready ({_width}x{_height})");
    }

    // Compiled out entirely unless this is a DEV_TOOLS build, so a release binary
    // carries neither the behaviour nor the variable names.
#if !MACOS && !LINUX
    /// <summary>True only when the user has pinned the old backend; WGL is the
    /// default (see WinWglContext).</summary>
    private bool UseGlfwBackend() =>
        string.Equals(BackendOverride ?? _store?.Load().Lighting.RenderBackend, "glfw",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The pre-WGL path, reachable by setting Lighting.RenderBackend to
    /// "glfw" if a card ever refuses the direct one.</summary>
    private void InitViaGlfw(int major, int minor)
    {
        var guard = GlfwErrorGuard.Install();
        Log($"[gpu] GLFW error guard: installed={guard.Installed} "
            + $"preempted-silk-default={guard.PreEmptedSilkDefault} "
            + $"self-test={guard.SelfTestPassed} ({guard.Detail})");
        if (!guard.SelfTestPassed)
        {
            // Not fatal on its own: the crash guard plus the off latch bound a
            // still-throwing callback to one crash per boot rather than a loop.
            ServiceLog.Warn("[gpu] GLFW errors may still be fatal: the error guard did not verify");
        }
        var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
        glfw.DefaultWindowHints();
        glfw.WindowHint(WindowHintBool.Visible, false);
        glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.OpenGL);
        glfw.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
        glfw.WindowHint(WindowHintInt.ContextVersionMajor, major);
        glfw.WindowHint(WindowHintInt.ContextVersionMinor, minor);
        Log($"[gpu] glfwCreateWindow ({major}.{minor} core, hidden)");
        unsafe
        {
            var mark = GlfwErrorGuard.SwallowedCount;
            var handle = glfw.CreateWindow(_width, _height, "nexus-gpu", null, null);
            if (handle == null)
            {
                throw new InvalidOperationException(
                    $"GLFW could not create an OpenGL {major}.{minor} core context "
                    + $"({GlfwErrorGuard.ErrorSince(mark)})");
            }
            _glfwWindow = (IntPtr)handle;
            glfw.MakeContextCurrent(handle);
            _gl = GL.GetApi(new Silk.NET.GLFW.GlfwContext(glfw, handle));
        }
    }
#endif

    private static void ApplyForcedFailure()
    {
#if DEV_TOOLS
        if (GpuTestSeam.ConsumeIntermittentFailure())
        {
            throw new InvalidOperationException("forced intermittent GL init failure (NEXUS_GPU_FAIL_FIRST_N)");
        }
        switch (GpuTestSeam.Mode)
        {
            case GpuForceMode.Fail:
                throw new InvalidOperationException("forced GL init failure (NEXUS_GPU_FORCE_FAIL=fail)");
            case GpuForceMode.Hang:
                Log("[gpu] forced hang: the GL thread parks here for the process lifetime");
                using (var never = new ManualResetEventSlim(false))
                {
                    never.Wait();
                }
                break;
            case GpuForceMode.Crash:
                Log("[gpu] forced crash: killing the process the way a native fast-fail does");
                Environment.FailFast("forced GL init crash (NEXUS_GPU_FORCE_FAIL=crash)");
                break;
        }
#endif
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        try
        { _workQueue.CompleteAdding(); }
        catch { }
        // If the GL thread didn't actually exit it may still be mid-GL-call with
        // the context current; destroying the native context underneath it
        // (eglTerminate / CGLDestroyContext) is undefined and can segfault. Only
        // tear it down once the thread has joined; a leaked context at process
        // exit is harmless.
        var joined = _glThread?.Join(TimeSpan.FromSeconds(2)) ?? true;
#if MACOS
        if (joined && _cglCtx != IntPtr.Zero)
        {
            MacGlContext.Destroy(_cglCtx);
            _cglCtx = IntPtr.Zero;
        }
#elif LINUX
        if (joined && _eglUsed)
        {
            LinuxEglContext.Destroy();
            _eglUsed = false;
        }
#else
        // WGL is freed by the GL thread itself (ReleaseThreadOwnedContext); only
        // the GLFW fallback's window is left, and Win32 refuses a DestroyWindow
        // from any thread but its creator, so this is best-effort.
        if (joined && _glfwWindow != IntPtr.Zero)
        {
            try
            {
                unsafe
                {
                    Silk.NET.GLFW.GlfwProvider.GLFW.Value.DestroyWindow((WindowHandle*)_glfwWindow);
                }
            }
            catch { }
            _glfwWindow = IntPtr.Zero;
        }
#endif
        // Dispose the per-thread MREs we created along the way.
        if (_invokeDone.Values is { } values)
        {
            foreach (var mre in values)
            {
                try
                { mre.Dispose(); }
                catch { }
            }
        }
        _invokeDone.Dispose();
    }
}
