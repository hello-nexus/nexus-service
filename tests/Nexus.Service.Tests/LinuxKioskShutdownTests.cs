using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests;

public class LinuxKioskShutdownTests
{
    private static Process StartSleeper()
    {
        var psi = new ProcessStartInfo("sleep", "60") { UseShellExecute = false, CreateNoWindow = true };
        return Process.Start(psi)!;
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
    public async Task Exited_handler_runs_off_the_callback_thread()
    {
        if (OperatingSystem.IsWindows())
            return;
        var proc = StartSleeper();
        var ran = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        LinuxPanelKioskHost.AttachExited(proc, () => ran.TrySetResult(Environment.CurrentManagedThreadId));
        proc.Kill();

        var thread = await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
        proc.Dispose();
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
