using System;
using System.Threading;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// Running out of init budget must not be recorded as a verdict on the card: a
/// customer's context arrived at 28.4s against a 10s budget, so latching left
/// every shader-rendered effect black until a restart.
///
/// The init body is substituted throughout - a real GL init is host-dependent
/// and would put concurrent glfwInit calls into a parallelized suite.
/// </summary>
public class GpuContextInitLatchTests
{
    private static GpuContext WithInit(Action init) => new(160, 90, null, init);

    /// <summary>Init that blocks until the test releases it.</summary>
    private static GpuContext Blocking(ManualResetEventSlim gate) =>
        WithInit(() => gate.Wait(TimeSpan.FromSeconds(30)));

    private static void Start(GpuContext gpu)
    {
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
    }

    [Fact]
    public void FreshContext_IsNeitherAvailableNorInitializingNorFailed()
    {
        using var gpu = WithInit(() => { });

        Assert.False(gpu.Available);
        Assert.False(gpu.Initializing);
        Assert.False(gpu.Failed);
    }

    [Fact]
    public void ElapsedBudget_LeavesTheAttemptRunning()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);

        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));
        Assert.False(gpu.Failed);
        Assert.True(gpu.Initializing);
        gate.Set();
    }

    [Fact]
    public void ContextThatLandsAfterTheBudget_StillBecomesAvailable()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));

        gate.Set();

        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        Assert.True(gpu.Available);
        Assert.False(gpu.Failed);
    }

    [Fact]
    public void InitThatThrows_IsTerminal()
    {
        using var gpu = WithInit(() => throw new InvalidOperationException("no adapter"));
        Start(gpu);

        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        Assert.True(gpu.Failed);
        Assert.False(gpu.Available);
        Assert.False(gpu.Initializing);
    }

    [Fact]
    public void AbandonInit_TurnsAnEndlessWaitIntoAFailure()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));

        gpu.AbandonInit();

        Assert.True(gpu.Failed);
        Assert.False(gpu.Initializing);
        gate.Set();
    }

    [Fact]
    public void AbandonInit_DoesNotDemoteAContextThatAlreadyLanded()
    {
        using var gpu = WithInit(() => { });
        Start(gpu);
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(10)));

        gpu.AbandonInit();

        Assert.True(gpu.Available);
        Assert.False(gpu.Failed);
    }

    [Fact]
    public void ResetForRetry_IsRefusedBeforeAnyAttempt()
    {
        using var gpu = WithInit(() => { });

        Assert.False(gpu.ResetForRetry());
    }

    [Fact]
    public void ResetForRetry_IsRefusedWhileAnAttemptIsStillRunning()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        gpu.WaitForInit(TimeSpan.FromMilliseconds(50));

        // A second GLFW init beside a live one shares process-global state.
        Assert.False(gpu.ResetForRetry());
        gate.Set();
    }

    [Fact]
    public void ResetForRetry_IsBoundedByTheRetryBudget()
    {
        var attempts = 0;
        using var gpu = WithInit(() =>
        {
            attempts++;
            throw new InvalidOperationException("no adapter");
        });
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));

        for (var i = 0; i < GpuInitRetry.MaxAttempts; i++)
        {
            Assert.True(gpu.ResetForRetry());
            // Does not start the attempt itself: the caller sets the OS GPU
            // preference first, which is read at context-creation time.
            Assert.Equal(i + 1, attempts);
            Start(gpu);
            Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal(GpuInitRetry.MaxAttempts + 1, attempts);
        Assert.False(gpu.ResetForRetry());
    }

    [Fact]
    public void ResetForRetry_IsRefusedAfterAnAbandon()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));
        gpu.AbandonInit();

        // The abandoned thread is still inside native init; a second one beside
        // it is undefined.
        Assert.True(gpu.Failed);
        Assert.True(gpu.InitAbandoned);
        Assert.False(gpu.ResetForRetry());
        gate.Set();
    }

    [Fact]
    public void AnAbandonedAttemptThatLandsAnywayIsRecovered()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));
        gpu.AbandonInit();
        Assert.True(gpu.Failed);

        gate.Set();

        Assert.True(gpu.WaitForLateLanding(TimeSpan.FromSeconds(10)));
        Assert.True(gpu.Available);
        Assert.False(gpu.Failed);
        Assert.False(gpu.InitAbandoned);
    }

    [Fact]
    public void WaitForLateLanding_GivesUpOnAnAttemptThatNeverLands()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        gpu.AbandonInit();

        Assert.False(gpu.WaitForLateLanding(TimeSpan.FromMilliseconds(50)));
        gate.Set();
    }

    [Fact]
    public void DeclineInit_ReportsUnavailableRatherThanInitializing()
    {
        var attempts = 0;
        using var gpu = WithInit(() => attempts++);

        gpu.DeclineInit("no card produced a working context");

        // /lighting/status maps Failed to "unavailable" and everything else to
        // "initializing", which also gates the render-GPU shortcut.
        Assert.True(gpu.Failed);
        Assert.False(gpu.Initializing);
        Assert.False(gpu.Available);
        Assert.True(gpu.InitSuppressed);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void DeclineInit_StopsTheRenderPathReArmingTheInit()
    {
        var attempts = 0;
        using var gpu = WithInit(() => attempts++);

        gpu.DeclineInit("declined");
        Start(gpu);
        Start(gpu);

        Assert.Equal(0, attempts);
        Assert.False(gpu.ResetForRetry());
    }

    [Fact]
    public void RearmAfterLatch_LetsAScheduledReprobeAttemptAgain()
    {
        var attempts = 0;
        using var gpu = WithInit(() =>
        {
            attempts++;
            throw new InvalidOperationException("no adapter");
        });
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        gpu.DeclineInit("latched off");
        Start(gpu);
        Assert.Equal(1, attempts);

        Assert.True(gpu.RearmAfterLatch());

        Assert.False(gpu.InitSuppressed);
        Assert.False(gpu.Failed);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void RearmAfterLatch_RestoresTheRetryBudget()
    {
        using var gpu = WithInit(() => throw new InvalidOperationException("no adapter"));
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        for (var i = 0; i < GpuInitRetry.MaxAttempts; i++)
        {
            Assert.True(gpu.ResetForRetry());
            Start(gpu);
            Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        }
        Assert.False(gpu.ResetForRetry());

        Assert.True(gpu.RearmAfterLatch());
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));

        Assert.True(gpu.ResetForRetry());
    }

    [Fact]
    public void RearmAfterLatch_IsRefusedAfterAnAbandon()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));
        gpu.AbandonInit();

        Assert.False(gpu.RearmAfterLatch());
        gate.Set();
    }

    [Fact]
    public void RearmAfterLatch_LeavesAWorkingContextAlone()
    {
        using var gpu = WithInit(() => { });
        Start(gpu);
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(10)));

        Assert.False(gpu.RearmAfterLatch());
        Assert.True(gpu.Available);
    }

    [Fact]
    public void DisposedContext_ReportsItself()
    {
        var gpu = WithInit(() => { });
        gpu.Dispose();

        Assert.True(gpu.IsDisposed);
        Assert.False(gpu.RearmAfterLatch());
    }

    [Fact]
    public void DeclineInit_LeavesAWorkingContextAlone()
    {
        using var gpu = WithInit(() => { });
        Start(gpu);
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(10)));

        gpu.DeclineInit("late decline");

        Assert.True(gpu.Available);
        Assert.False(gpu.Failed);
    }

    // The Windows GL backend defaults to WGL: GLFW's own init measured 30.1s
    // under LocalSystem where the WGL calls it wraps took 83ms, and the service
    // pays context init on every start. "glfw" stays reachable as an escape.
    [Fact]
    public void Windows_gl_backend_defaults_to_wgl()
    {
        Assert.Equal("wgl", new Nexus.Service.Persistence.LightingSettings().RenderBackend);
    }
}
