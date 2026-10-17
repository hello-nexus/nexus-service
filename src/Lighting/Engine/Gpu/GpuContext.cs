using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Platform;
using Silk.NET.OpenGL;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Owns a single shared offscreen OpenGL 3.3 core (or ES 3.0 on ANGLE) context used by every shader
/// effect. Thread-safety: all GL work runs on the dedicated <c>_glThread</c>;
/// callers off that thread go through <see cref="Invoke"/>, which marshals the
/// work onto it and blocks until completion. <see cref="Lock"/> only guards
/// lazy initialization.
///
/// If init fails the context is marked unavailable and shader effects do not
/// render at all, except that the ANGLE backend falls back to WARP (see
/// GpuRenderSelect.TryWarpFallback).
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
    // ANGLE (ES on D3D11) context; the GL thread owns and frees it like _wgl.
    private AngleHandles _angle;
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
    // Set by ReportContextLost: the GL thread leaves its loop so the context is freed and a fresh one can start.
    private volatile bool _lostExit;
    private volatile bool _upgradeGate;
    private volatile bool _parallelCompile;
    private int _generation;
#if !MACOS && !LINUX
    private volatile bool _isEs;
    private volatile bool _isWarp;
#endif

    // Dedicated GL thread. The engine loop is a Task that hops thread-pool
    // threads between awaits, but wgl contexts are sticky to the
    // thread that created them, so every GL call must marshal to this one
    // thread via Invoke().
    private Thread? _glThread;
    // One queue per GL thread: a lost context completes its queue so a late Invoke
    // fails fast instead of parking on an item nobody will take.
    private GlWorkQueue _workQueue = new();
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

    /// <summary>ANGLE only: render on this DXGI adapter ("high:low" LUID as GpuAdapterLuids formats it), in place of the render-GPU setting.</summary>
    public string? AdapterLuid { get; init; }

    /// <summary>ANGLE only: pick the adapter by the selection's class (0 = integrated, 2 = discrete), in place of the render-GPU setting. Settable until init starts.</summary>
    public int? AdapterClass { get; set; }

    /// <summary>ANGLE only: render on the WARP software rasterizer instead of an adapter. Settable until init starts.</summary>
    public bool UseWarp { get; set; }

    /// <summary>The bound context is OpenGL ES (ANGLE): shaders take the 3.00 es dialect and readback is RGBA.</summary>
#if !MACOS && !LINUX
    public bool IsEs => _isEs;

    /// <summary>The bound context renders on the CPU (WARP), not a GPU.</summary>
    public bool IsWarp => _isWarp;
#else
    public bool IsEs => false;
    public bool IsWarp => false;
#endif

    /// <summary>Pixel-pack buffers behind <see cref="ReadbackBatch"/>. GL thread only.</summary>
    internal PackPool Pack { get; } = new();

    // NEXUS_GL_BATCH_READBACK: 0 off everywhere, 1 on everywhere, unset = on for ANGLE (ES) and Windows WGL.
    private static readonly string? BatchReadbackSetting = Environment.GetEnvironmentVariable("NEXUS_GL_BATCH_READBACK");

    /// <summary>Renders inside a <see cref="ReadbackBatch"/> queue their glReadPixels and pay the GPU wait once per batch.</summary>
    public bool BatchReadback => BatchReadbackSetting switch
    {
        "0" => false,
        "1" => true,
        _ => IsEs || OperatingSystem.IsWindows(),
    };

    /// <summary>Allow compile and link to run in the driver's worker threads (KHR_parallel_shader_compile) when the context offers it. A caller that must have a finished render from its first frame sets this false.</summary>
    public bool AsyncCompile { get; set; } = true;

    internal void SetParallelCompileForTest(bool on) => _parallelCompile = on;

    /// <summary>Shader programs compile and link without blocking the GL thread: the extension is present on the bound context and <see cref="AsyncCompile"/> is on.</summary>
    public bool UseAsyncCompile => AsyncCompile && _parallelCompile;

    /// <summary>Upper bound handed to glMaxShaderCompilerThreadsKHR; this value asks for the implementation default.</summary>
    private const uint ParallelCompileThreadsDefault = 0xFFFFFFFF;

    /// <summary>Counts contexts that came up. GL objects belong to one generation; a holder rebuilds them when it moves.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Raised on a worker thread once a lost context's GL thread has exited, so the owner can re-create it.</summary>
    public event Action<GpuContext>? ContextLost;

    /// <summary>The backend this context creates, after fallbacks (see <see cref="GpuBackend.Resolve(string?)"/>).</summary>
    public string ResolvedBackend => GpuBackend.Resolve(BackendOverride ?? _store?.Load().Lighting.RenderBackend);

    // GL_RENDERER of the bound context (which physical card was selected), set
    // once init succeeds. Null until then.
    public string? Renderer { get; private set; }

    /// <summary>True when the live context renders on a D3D11 device this process created itself, not on ANGLE's own device.</summary>
    internal bool OwnsD3dDevice { get; private set; }

    /// <summary>GL_VERSION of the live context, which for ANGLE names its build.</summary>
    public string? GlVersion { get; private set; }


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
    // GL init calls in a parallelized suite are undefined.
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
    /// in native GL code. No second init may run beside it.</summary>
    public bool InitAbandoned => _abandoned;

    /// <summary>Auto-init is off until a re-probe rearms it.</summary>
    public bool InitSuppressed => _initSuppressed;

    public bool IsDisposed => _disposed;

    /// <summary>A retired WARP context is being replaced: init stays suppressed, across rearms, until the selection starts it (<see cref="ReleaseUpgradeGate"/>), so the render loop cannot bring up the wrong thing in between.</summary>
    public bool UpgradeGated => _upgradeGate;

    internal void OpenUpgradeGateForTest()
    {
        lock (_lock)
        {
            _upgradeGate = true;
            _initSuppressed = true;
        }
    }

    /// <summary>Let the next init run. Called by whoever owns starting it, under <see cref="Lock"/>.</summary>
    public void ReleaseUpgradeGate()
    {
        lock (_lock)
        {
            _upgradeGate = false;
            _initSuppressed = false;
        }
    }

    /// <summary>Lift a decline so a scheduled re-probe can attempt the card
    /// again, with a fresh retry budget. Refused after an abandon: that thread is
    /// still parked in native GL code.</summary>
    public bool RearmAfterLatch()
    {
        lock (_lock)
        {
            // An attempt that is still running owns the fields below and its own
            // GL thread; resetting them under it starts a second init beside the
            // first, which context creation leaves undefined.
            if (_disposed || _abandoned || _ready || (_initStarted && !_failed))
            {
                return false;
            }
            _initSuppressed = _upgradeGate;
            _initError = null;
            _failed = false;
            _ready = false;
            _retries = 0;
            _initDone.Reset();
            _landed.Reset();
            _initStarted = false;
            _lostExit = false;
            _gl = null;
            Renderer = null;
            GlVersion = null;
            OwnsD3dDevice = false;
            _fbo = 0;
            _fboTex = 0;
            _quadVao = 0;
            _quadVbo = 0;
#if MACOS
            _cglCtx = IntPtr.Zero;
#elif LINUX
            _eglUsed = false;
#else
            // _wgl is left alone: the GL thread owns it and frees it as it exits.
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
        var queue = new GlWorkQueue();
        _workQueue = queue;
        _glThread = new Thread(() => GlThreadMain(queue)) { IsBackground = true, Name = "nexus-gl" };
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
    /// attempt qualifies: context creation shares process-global state,
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
            _lostExit = false;
            _gl = null;
            Renderer = null;
            GlVersion = null;
            OwnsD3dDevice = false;
            _fbo = 0;
            _fboTex = 0;
            _quadVao = 0;
            _quadVbo = 0;
#if MACOS
            _cglCtx = IntPtr.Zero;
#elif LINUX
            _eglUsed = false;
#else
            // _wgl is not touched here: the GL thread owns it and frees it on
            // its way out.
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

    private void GlThreadMain(GlWorkQueue queue)
    {
        try
        {
            RunGlThread(queue);
        }
        finally
        {
            // Every exit lands here: a throwing init that already created the
            // context, the completed work queue, and Dispose. Nothing else may
            // free these - the window is this thread's.
            ReleaseThreadOwnedContext();
        }
    }

    private void RunGlThread(GlWorkQueue queue)
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
            Interlocked.Increment(ref _generation);
            _landed.Set();
            _initDone.Set();
        }

        var stats = new GlQueueStats(TimeSpan.FromSeconds(10));
        while (!_disposed && !_lostExit)
        {
            GlJob job;
            try
            { job = queue.Take(); }
            catch (InvalidOperationException) { break; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!job.Background)
            {
                RecordLiveWait(started - job.EnqueuedTicks);
            }
            try
            { job.Work(); }
            catch (Exception ex) { Log($"[gpu] work item threw: {ex}"); }
            if (stats.Record(job, started, System.Diagnostics.Stopwatch.GetTimestamp()) is { } summary)
            {
                Log(summary);
            }
        }

        // A lost context leaves callers parked in Invoke. Completing the queue first
        // means nothing can be added past the drain below (a late Add throws and
        // Invoke returns), so every item that did get in runs against the dead
        // context (each throws or no-ops) and its waiter is released.
        queue.CompleteAdding();
        while (queue.TryTake(out var pending))
        {
            try
            { pending.Work(); }
            catch (Exception) { }
        }
        DrainSlotWaiters();
    }

    /// <summary>
    /// Called on the GL thread when the device behind the context is gone. The
    /// context is marked failed, the thread leaves its loop (freeing the dead
    /// context), and <see cref="ContextLost"/> fires once it has exited so the
    /// owner re-creates through the normal init path.
    /// </summary>
    public void ReportContextLost(string reason)
    {
        lock (_lock)
        {
            if (_disposed || _lostExit || !_ready)
            {
                return;
            }
            _lostExit = true;
            _ready = false;
            _failed = true;
            _initError = new InvalidOperationException($"GL context lost: {reason}");
        }
        Log($"[gpu] context lost ({reason}); re-creating");
        // Wakes a GL thread blocked waiting for work and refuses any later Invoke.
        _workQueue.CompleteAdding();
        var thread = _glThread;
        var handler = ContextLost;
        // A driver reset can renumber adapters; the cached enumeration would hand back stale LUIDs.
        Nexus.Service.Sensors.GpuAdapterLuids.Invalidate();
        new Thread(() =>
        {
            if (thread is not null && !thread.Join(TimeSpan.FromSeconds(10)))
            {
                // Parked in native code: no second init may run beside it.
                lock (_lock)
                {
                    _abandoned = true;
                }
                Log("[gpu] lost context's GL thread did not exit; not re-creating in this process");
                return;
            }
            handler?.Invoke(this);
        })
        { IsBackground = true, Name = "nexus-gpu-lost" }.Start();
    }

    /// <summary>
    /// Stop a running WARP context so hardware can be tried again: the same exit
    /// a lost context takes, minus the <see cref="ContextLost"/> event. False when
    /// the context is not on WARP or its thread would not exit.
    /// </summary>
    public bool RetireWarp()
    {
        Thread? thread;
        lock (_lock)
        {
            if (_disposed || !_ready || !IsWarp || _lostExit)
            {
                return false;
            }
            _lostExit = true;
            _ready = false;
            _failed = true;
            _initError = new InvalidOperationException("retired WARP context");
            UseWarp = false;
            _upgradeGate = true;
            _initSuppressed = true;
            thread = _glThread;
        }
        // Wake the thread out of its blocking Take.
        _workQueue.CompleteAdding();
        if (thread is not null && !thread.Join(TimeSpan.FromSeconds(10)))
        {
            lock (_lock)
            {
                _abandoned = true;
            }
            return false;
        }
        return true;
    }

    /// <summary>ES only, on the GL thread after a frame: true (and the loss is reported) when ANGLE signals the device is gone, via GL_CONTEXT_LOST or EGL_CONTEXT_LOST.</summary>
    public bool CheckContextLost()
    {
#if WINDOWS
        if (!_isEs || _gl is null)
        {
            return false;
        }
        const uint GlContextLost = 0x0507;
        var lost = false;
        // GL keeps one error flag per kind; a few reads drain them.
        for (var i = 0; i < 8; i++)
        {
            var err = (uint)_gl.GetError();
            if (err == 0)
            {
                break;
            }
            if (err == GlContextLost)
            {
                lost = true;
            }
        }
        if (!lost && AngleContext.PendingEglError() == AngleContext.EglContextLost)
        {
            lost = true;
        }
        if (lost)
        {
            ReportContextLost("device removed or reset");
        }
        return lost;
#else
        return false;
#endif
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
        AngleHandles angle;
        lock (_lock)
        {
            angle = _angle;
            _angle = default;
        }
        if (angle.Created)
        {
            AngleContext.Destroy(angle);
        }
#endif
    }

    // Per calling thread: while > 0, this thread's Invoke calls ride the background lane.
    [ThreadStatic]
    private static int _backgroundDepth;

    private sealed class BackgroundScope : IDisposable
    {
        public void Dispose() => _backgroundDepth--;
    }

    /// <summary>
    /// Mark this thread's GL work as background (thumbnails, validation,
    /// benchmark kernels) until the scope is disposed. Background jobs run only
    /// when no live frame is waiting, so a burst of them cannot delay the
    /// lighting engine by more than the one job already executing.
    /// </summary>
    public static IDisposable BeginBackground()
    {
        _backgroundDepth++;
        return new BackgroundScope();
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
        try
        {
            _workQueue.Add(() =>
            {
                try
                { work(); }
                catch (Exception ex) { caught = ex; }
                finally { done.Set(); }
            }, _backgroundDepth > 0);
        }
        catch (InvalidOperationException)
        {
            // The queue was completed: the context is lost or disposed, so the work is dropped.
            return;
        }
        done.Wait();
        if (caught is not null)
        {
            throw caught;
        }
    }

    /// <summary>
    /// Most background programs (thumbnails, validation, warm-up) compiling in the
    /// driver at once. Small enough that a cold lighting page keeps only a couple
    /// of ANGLE's worker threads busy, so a game or the rest of the PC never sees
    /// a burst, while still overlapping one compile's HLSL pass with the next.
    /// </summary>
    internal const int MaxBackgroundCompiles = 2;

    /// <summary>GL thread only.</summary>
    internal CompileGate BackgroundCompiles { get; } = new(MaxBackgroundCompiles);

    // Background steps waiting for a compile slot, in arrival order; GL thread only. A freed slot wakes the first.
    private sealed class SlotWaiter
    {
        public Action Job = null!;
        public GlWorkQueue Queue = null!;
        // The slot was handed to this waiter when it was woken; its next run owns it.
        public bool Handed;
    }

    private readonly System.Collections.Generic.Queue<SlotWaiter> _slotWaiters = new();

    // Set for the duration of a step run that was handed a slot; TryAcquireCompileSlot consumes it.
    private bool _handedSlot;

    /// <summary>Take a background compile slot: the one handed to this run if any, else a free one. GL thread only.</summary>
    private long _maxLiveWaitTicks;

    private void RecordLiveWait(long ticks)
    {
        long seen;
        while (ticks > (seen = Volatile.Read(ref _maxLiveWaitTicks)))
        {
            if (Interlocked.CompareExchange(ref _maxLiveWaitTicks, ticks, seen) == seen)
            {
                break;
            }
        }
    }

    /// <summary>Longest a live job sat in the queue before the GL thread took it, measured on the GL thread.</summary>
    internal double MaxLiveWaitMs => Volatile.Read(ref _maxLiveWaitTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    internal bool TryAcquireCompileSlot()
    {
        if (_handedSlot)
        {
            _handedSlot = false;
            return true;
        }
        return BackgroundCompiles.TryEnter();
    }

    /// <summary>
    /// Free a background compile slot. With steps waiting, the slot passes straight to the
    /// longest-waiting one (no free moment for a newcomer to take it), otherwise it is freed. GL thread only.
    /// </summary>
    internal void ReleaseCompileSlot()
    {
        if (_slotWaiters.Count == 0)
        {
            BackgroundCompiles.Exit();
            return;
        }
        var waiter = _slotWaiters.Dequeue();
        waiter.Handed = true;
        try
        { waiter.Queue.Add(waiter.Job, true); }
        catch (InvalidOperationException)
        {
            // The queue was completed; run the step so its caller is released (it hands the slot on).
            waiter.Job();
        }
    }

    /// <summary>Steps parked for a compile slot.</summary>
    internal int ParkedWaiters => Volatile.Read(ref _parked);
    private int _parked;

    private void DrainSlotWaiters()
    {
        while (_slotWaiters.Count > 0)
        {
            var waiter = _slotWaiters.Dequeue();
            Interlocked.Decrement(ref _parked);
            try
            { waiter.Job(); }
            catch (Exception) { }
        }
    }

    private readonly ManualResetEventSlim _liveProgramLanded = new(false);

    /// <summary>A live effect's program finished building, so the render path is past its own first-compile cost.</summary>
    internal void NoteLiveProgramLanded() => _liveProgramLanded.Set();

    internal bool WaitForLiveProgram(TimeSpan timeout) => _liveProgramLanded.Wait(timeout);

    /// <summary>The ANGLE program cache is on disk and usable, so a compile now is not repeated next boot.</summary>
    internal bool ProgramCacheActive =>
#if WINDOWS
        IsEs && AngleContext.ProgramCacheActive;
#else
        false;
#endif

    internal static bool InBackground => _backgroundDepth > 0;

    /// <summary>How long a background job that is waiting on a compile sleeps before it polls again.</summary>
    internal static readonly TimeSpan StepPollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Like <see cref="Invoke"/> for work that may not be finishable yet (a
    /// program still compiling). On the background lane <see cref="StepState.Poll"/>
    /// re-queues the step behind a short delay and <see cref="StepState.WaitSlot"/>
    /// parks it until a compile slot frees; the GL thread is free meanwhile and
    /// the caller keeps waiting for the final run. On the live lane anything but
    /// Done completes the call at once: the frame is skipped, never waited for.
    /// </summary>
    public void InvokeStep(Func<StepState> step)
    {
        if (!Available || _glThread is null)
        {
            return;
        }
        if (Thread.CurrentThread == _glThread)
        {
            step();
            return;
        }
        var background = _backgroundDepth > 0;
        var queue = _workQueue;
        Exception? caught = null;
        var done = _invokeDone.Value!;
        done.Reset();
        Action? job = null;
        var waiter = new SlotWaiter { Queue = queue };
        job = () =>
        {
            var state = StepState.Done;
            if (waiter.Handed)
            {
                waiter.Handed = false;
                _handedSlot = true;
                Interlocked.Decrement(ref _parked);
            }
            try
            { state = step(); }
            catch (Exception ex) { caught = ex; }
            if (_handedSlot)
            {
                // The step finished without using the slot it was handed; pass it on.
                _handedSlot = false;
                ReleaseCompileSlot();
            }
            if (state != StepState.Done && background && caught is null)
            {
                if (state == StepState.WaitSlot)
                {
                    if (!queue.IsCompleted)
                    {
                        waiter.Job = job!;
                        _slotWaiters.Enqueue(waiter);
                        Interlocked.Increment(ref _parked);
                        return;
                    }
                }
                else
                {
                    try
                    {
                        queue.AddDeferred(job!, StepPollInterval);
                        return;
                    }
                    catch (InvalidOperationException) { }
                }
            }
            done.Set();
        };
        try
        { queue.Add(job, background); }
        catch (InvalidOperationException)
        {
            return;
        }
        done.Wait();
        if (caught is not null)
        {
            throw caught;
        }
    }

    private void InitInternal()
    {
        _parallelCompile = false;
        BackgroundCompiles.Reset();
        ApplyForcedFailure();
#if MACOS
        // macOS: skip NSWindow entirely and create a headless GL 4.1
        // core context via CGL. Works from any thread, no AppKit needed.
        Log("[gpu] macOS: CGL create core context");
        _cglCtx = MacGlContext.CreateAndMakeCurrent();
        _gl = GL.GetApi(new CglNativeContext());
#elif LINUX
        // Linux: headless EGL on the GPU device platform - no X/Wayland, no
        // window. A display-bound GL path crashes creating an nvidia GL
        // context as root on the user's XWayland, so the root daemon can't
        // use it; EGL device-platform is windowless like macOS's CGL.
        Log("[gpu] Linux: EGL device-platform headless context");
        LinuxEglContext.CreateAndMakeCurrent(_store?.Load().Lighting.RenderGpu ?? "auto");
        _eglUsed = true;
        _gl = GL.GetApi(new EglNativeContext());
#else
        // Windows: ANGLE (ES on D3D11) by default, else WGL directly
        // (WinWglContext). D3D11CreateDevice screens out a
        // ghost adapter but does not prove WGL works; the GL verdict is the
        // context creation itself.
        // A previous attempt's ES/WARP state must not leak into this one, whatever backend it picks.
        _isEs = false;
        _isWarp = false;
        var backend = ResolvedBackend;
        if (backend == GpuBackend.Angle)
        {
            InitViaAngle();
        }
        else
        {
            var preCheckSw = System.Diagnostics.Stopwatch.StartNew();
            if (!Nexus.Service.Sensors.GpuAdapterLuids.HasUsableHardwareGpu())
            {
                throw new InvalidOperationException("no usable GPU adapter present");
            }
            var preCheckMs = preCheckSw.ElapsedMilliseconds;
            // An impossible version is how the dev seam makes creation fail.
            var (major, minor) = GpuTestSeam.Mode == GpuForceMode.ContextError ? (9, 9) : (3, 3);
            var glSw = System.Diagnostics.Stopwatch.StartNew();
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
            GlVersion = _gl.GetStringS(StringName.Version);
            Log($"[gpu] GL context on renderer='{Renderer}' "
                + $"vendor='{_gl.GetStringS(StringName.Vendor)}' "
                + $"version='{_gl.GetStringS(StringName.Version)}'");
        }
        catch (Exception ex) { Log($"[gpu] GL renderer query failed: {ex.Message}"); }
        EnableParallelCompile();

        _fboTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fboTex);
        unsafe
        {
            // ES renders to RGBA8 and reads RGBA back (the only readback it guarantees).
            _gl.TexImage2D(TextureTarget.Texture2D, 0, IsEs ? InternalFormat.Rgba8 : InternalFormat.Rgb8,
                (uint)_width, (uint)_height, 0, IsEs ? PixelFormat.Rgba : PixelFormat.Rgb, PixelType.UnsignedByte, null);
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
        if (!IsEs)
        {
            try
            { _gl.ProvokingVertex(VertexProvokingMode.LastVertexConvention); }
            catch { }
        }
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

    // ES (ANGLE) only: desktop GL keeps the synchronous path. The ANGLE program cache is what the extension's worker compiles feed.
    private void EnableParallelCompile()
    {
        _parallelCompile = false;
        if (!IsEs || _gl is null)
        {
            return;
        }
        try
        {
            var extensions = _gl.GetStringS(StringName.Extensions) ?? "";
            if (!extensions.Contains("GL_KHR_parallel_shader_compile", StringComparison.Ordinal))
            {
                Log("[gpu] GL_KHR_parallel_shader_compile not offered; shader compiles stay synchronous");
                return;
            }
            if (!_gl.Context.TryGetProcAddress("glMaxShaderCompilerThreadsKHR", out var entry) || entry == 0)
            {
                Log("[gpu] glMaxShaderCompilerThreadsKHR not resolvable; shader compiles stay synchronous");
                return;
            }
            unsafe
            {
                ((delegate* unmanaged<uint, void>)entry)(ParallelCompileThreadsDefault);
            }
            _parallelCompile = true;
            Log("[gpu] GL_KHR_parallel_shader_compile on; shader compiles do not block the GL thread");
        }
        catch (Exception ex) { Log($"[gpu] parallel shader compile setup failed: {ex.Message}"); }
    }

#if !MACOS && !LINUX
    /// <summary>Create the ES context through ANGLE on the adapter the render-GPU choice maps to, or WARP.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_gl))]
    private void InitViaAngle()
    {
        if (GpuTestSeam.Mode == GpuForceMode.ContextError)
        {
            throw new InvalidOperationException("forced ANGLE context creation failure");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AngleTarget target;
        var forcedSoftware = GpuBackend.IsSoftwareChoice(_store?.Load().Lighting.RenderGpu);
        if (forcedSoftware)
        {
            GpuBackend.SoftwareForced = true;
        }
        if (UseWarp || forcedSoftware)
        {
            target = AngleTarget.WarpTarget;
        }
        else if (!string.IsNullOrEmpty(AdapterLuid))
        {
            target = new AngleTarget(AdapterLuid, false, $"luid {AdapterLuid}");
        }
        else if (GpuAdapterRequest.Kind == GpuAdapterKind.Warp)
        {
            target = AngleTarget.WarpTarget;
        }
        else if (GpuAdapterRequest.Kind != GpuAdapterKind.Auto && !string.IsNullOrEmpty(GpuAdapterRequest.Luid))
        {
            target = new AngleTarget(GpuAdapterRequest.Luid, false, $"luid {GpuAdapterRequest.Luid}");
        }
        else
        {
            var chosen = AngleAdapterSelector.Choose(
                Nexus.Service.Sensors.GpuAdapterLuids.Enumerate(),
                _store?.Load().Lighting.RenderGpu, AdapterClass);
            if (chosen is not { } t)
            {
                throw new InvalidOperationException("no usable GPU adapter present");
            }
            target = t;
        }
        var pickMs = sw.ElapsedMilliseconds;
        _isEs = true;
        _isWarp = target.Warp;
        try
        {
            _angle = AngleContext.CreateAndMakeCurrent(target, _width, _height);
            OwnsD3dDevice = _angle.Device != 0;
            _gl = GL.GetApi(new AngleNativeContext());
        }
        catch (Exception)
        {
            // A failed attempt must not leave the next one (possibly WGL) reading ES state.
            _isEs = false;
            _isWarp = false;
            throw;
        }
        Log($"[gpu] context init: ANGLE D3D11 ES 3.0 on '{target.Description}'"
            + (target.Warp ? " (WARP software rasterizer, CPU rendering)" : "")
            + $", adapter pick {pickMs}ms, context {sw.ElapsedMilliseconds - pickMs}ms");
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
        _workQueue.CompleteAdding();
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
        // WGL is freed by the GL thread itself (ReleaseThreadOwnedContext).
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
