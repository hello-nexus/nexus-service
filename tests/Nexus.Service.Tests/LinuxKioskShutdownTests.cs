using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Auth;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Linux;
using Nexus.Service.Platform.Linux.DBus;
using Xunit;

namespace Nexus.Service.Tests;

public class LinuxKioskShutdownTests
{
    private static Process StartSleeper()
    {
        var psi = new ProcessStartInfo("sleep", "60") { UseShellExecute = false, CreateNoWindow = true };
        var p = Process.Start(psi)!;
        Pids.Add(p, (object)p.Id);
        return p;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Process, object> Pids = new();

    private static LinuxPanelKioskHost NewHost()
    {
        var store = new InMemoryConfigStore();
        return new LinuxPanelKioskHost(new TokenService(store), new PanelDeviceRegistry(store), store,
            TestHandlers.FakeTopology(), new DBusConnection());
    }

    // The host disposes the Process object, so liveness is checked by pid.
    private static bool Dead(Process p)
    {
        var pid = Pids.TryGetValue(p, out var boxed) ? (int)boxed : p.Id;
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var probe = Process.GetProcessById(pid);
                if (probe.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>
    /// The deadlock shape: a closer holds the host lock and disposes the Process while the
    /// process was killed from outside. A handler that takes the host lock inline blocks the
    /// Process monitor forever; the dispatched handler must not.
    /// </summary>
    [Fact]
    public async Task Dispose_under_the_host_lock_completes_while_an_external_kill_fires_Exited()
    {
        if (OperatingSystem.IsWindows())
            return;
        var hostLock = new object();
        var handlerEntered = new ManualResetEventSlim();
        var proc = StartSleeper();
        LinuxPanelKioskHost.AttachExited(proc, () =>
        {
            handlerEntered.Set();
            lock (hostLock) { }
        });

        var disposed = await Task.Run(() => HoldLockAndDispose(hostLock, proc, handlerEntered));

        Assert.True(disposed, "Process.Dispose blocked behind the Exited handler");
    }

    private static bool HoldLockAndDispose(object hostLock, Process proc, ManualResetEventSlim handlerEntered)
    {
        lock (hostLock)
        {
            proc.Kill();
            // The pool thread running the handler is now parked on hostLock.
            if (!handlerEntered.Wait(TimeSpan.FromSeconds(10)))
                return false;
            var dispose = Task.Run(() => { _ = proc.HasExited; proc.Dispose(); });
            return dispose.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Closing_a_slot_reserves_it_until_the_browser_is_dead_then_reconciles_once()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var host = NewHost();
        var proc = StartSleeper();
        host.AddRunningForTest("d1", "dev1", proc);

        Assert.True(host.DetachForTest("d1", out var kiosk));
        Assert.False(host.IsRunning("d1"));
        Assert.True(host.IsClosing("d1"));
        Assert.True(host.DeferIfClosingForTest("d1"));
        Assert.True(host.ReconcileDeferred);

        await Task.Run(() => host.CloseDetachedForTest(kiosk!, "d1"));

        Assert.True(Dead(proc));
        Assert.False(host.IsClosing("d1"));
        Assert.False(host.ReconcileDeferred);
        Assert.False(host.DeferIfClosingForTest("d1"));
    }

    [Fact]
    public async Task CloseY70_kills_the_browser_and_clears_state()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var host = NewHost();
        var proc = StartSleeper();
        host.AddRunningForTest(LinuxPanelKioskHost.Y70Slot, LinuxPanelKioskHost.Y70Slot, proc);

        await Task.Run(host.CloseY70);

        Assert.True(Dead(proc));
        Assert.Equal(0, host.RunningCount);
        Assert.False(host.IsClosing(LinuxPanelKioskHost.Y70Slot));
    }

    [Fact]
    public async Task Shutdown_closes_every_kiosk_and_is_idempotent()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var host = NewHost();
        var a = StartSleeper();
        var b = StartSleeper();
        host.AddRunningForTest("d1", "dev1", a);
        host.AddRunningForTest("d2", "dev2", b);

        await Task.Run(host.Shutdown);
        await Task.Run(host.Shutdown);

        Assert.True(Dead(a));
        Assert.True(Dead(b));
        Assert.Equal(0, host.RunningCount);
    }

    [Fact]
    public async Task A_late_exit_of_a_superseded_process_leaves_its_successor_alone()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var host = NewHost();
        var old = StartSleeper();
        var successor = StartSleeper();
        host.AddRunningForTest("d1", "dev1", successor);
        old.Kill();
        Assert.True(Dead(old));

        await Task.Run(() => host.OnKioskExited("d1", old));

        Assert.True(host.IsRunning("d1"));
        Assert.False(successor.HasExited);
        host.Shutdown();
    }

    [Fact]
    public async Task The_exit_of_the_current_process_removes_the_slot()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var host = NewHost();
        var proc = StartSleeper();
        host.AddRunningForTest("d1", "dev1", proc);
        proc.Kill();
        Assert.True(Dead(proc));

        await Task.Run(() => host.OnKioskExited("d1", proc));

        Assert.False(host.IsRunning("d1"));
    }

    [Theory]
    [InlineData(2, 4)]
    [InlineData(4, 8)]
    [InlineData(16, 30)]
    [InlineData(30, 30)]
    public void ScreenInhibit_backoff_doubles_up_to_the_cap(int currentSeconds, int expectedSeconds)
    {
        var next = LinuxScreenInhibit.NextDelay(TimeSpan.FromSeconds(currentSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), next);
    }
}

/// <summary>Shares the inhibit's static state, so these never run beside each other.</summary>
[Collection("LinuxScreenInhibit")]
public class LinuxScreenInhibitTests
{
    private static Func<uint, Task<bool>> RecordUnInhibit(System.Collections.Concurrent.ConcurrentQueue<uint> log)
        => c => { log.Enqueue(c); return Task.FromResult(true); };

    [Fact]
    public async Task A_cookie_that_arrives_after_release_is_released_and_not_stored()
    {
        var log = new System.Collections.Concurrent.ConcurrentQueue<uint>();
        var reply = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquire = LinuxScreenInhibit.AcquireAsync(() => reply.Task, RecordUnInhibit(log), TimeSpan.FromMilliseconds(10));

        await LinuxScreenInhibit.ReleaseAsync(RecordUnInhibit(log));
        reply.SetResult(41);
        await acquire.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new uint[] { 41 }, log.ToArray());
        // Nothing orphaned: the next acquire starts fresh and its own release returns its own cookie.
        var second = LinuxScreenInhibit.AcquireAsync(() => Task.FromResult(42u), RecordUnInhibit(log), TimeSpan.FromMilliseconds(10));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await LinuxScreenInhibit.ReleaseAsync(RecordUnInhibit(log));
        Assert.Equal(new uint[] { 41, 42 }, log.ToArray());
    }

    [Fact]
    public async Task A_failing_provider_is_retried_until_it_appears()
    {
        var log = new System.Collections.Concurrent.ConcurrentQueue<uint>();
        var calls = 0;
        await LinuxScreenInhibit.AcquireAsync(
            () => ++calls < 3 ? Task.FromException<uint>(new InvalidOperationException("ServiceUnknown")) : Task.FromResult(7u),
            RecordUnInhibit(log), TimeSpan.FromMilliseconds(10)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, calls);
        await LinuxScreenInhibit.ReleaseAsync(RecordUnInhibit(log));
        Assert.Equal(new uint[] { 7 }, log.ToArray());
    }
}
